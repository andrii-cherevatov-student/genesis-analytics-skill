using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.TimeSeries;

public sealed record StlOptions
{
    public int Period { get; init; } = 12;

    public int SeasonalWindow { get; init; } = 13;

    public int SeasonalDegree { get; init; } = 0;

    public int? TrendWindow { get; init; }

    public int TrendDegree { get; init; } = 1;

    public int? LowPassWindow { get; init; }

    public int LowPassDegree { get; init; } = 1;

    public bool Robust { get; init; } = true;

    public int? InnerIterations { get; init; }

    public int? OuterIterations { get; init; }

    public int ResolvedTrendWindow =>
        TrendWindow ?? NextOdd((int)Math.Ceiling(1.5 * Period / (1.0 - 1.5 / SeasonalWindow)));

    public int ResolvedLowPassWindow => LowPassWindow ?? NextOdd(Period);
    public int ResolvedInner => InnerIterations ?? (Robust ? 1 : 2);
    public int ResolvedOuter => OuterIterations ?? (Robust ? 5 : 0);

    internal static int NextOdd(int x) => x % 2 == 0 ? x + 1 : x;
}

public static class StlDecomposer
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, StlOptions), Lazy<double>> SeasonalDfCache = new();

    public static double SeasonalDegreesOfFreedom(int n, StlOptions options) =>
        SeasonalDfCache.GetOrAdd((n, options), key => new Lazy<double>(() =>
        {
            var engine = new StlEngine(key.Item1, key.Item2 with { Robust = false, InnerIterations = 2, OuterIterations = 0 });
            var trace = 0.0;
            var unit = new double[key.Item1];
            for (var i = 0; i < key.Item1; i++)
            {
                unit[i] = 1.0;
                engine.Run(unit);
                trace += engine.Seasonal[i];
                unit[i] = 0.0;
            }

            return trace;
        })).Value;

    public static TimeSeriesDecomposition Decompose(IReadOnlyList<double> y, StlOptions? options = null)
    {
        options ??= new StlOptions();
        var n = y.Count;
        for (var i = 0; i < n; i++)
            if (double.IsNaN(y[i]) || double.IsInfinity(y[i]))
                throw new ArgumentException("STL input must not contain NaN or infinite values.");

        var engine = new StlEngine(n, options);
        var yArr = y.ToArray();
        engine.Run(yArr);
        var remainder = new double[n];
        for (var i = 0; i < n; i++) remainder[i] = yArr[i] - engine.Trend[i] - engine.Seasonal[i];
        return new TimeSeriesDecomposition(yArr, (double[])engine.Trend.Clone(), (double[])engine.Seasonal.Clone(),
            remainder, (double[])engine.Weights.Clone());
    }
}

internal sealed class StlEngine
{
    private readonly int _n;
    private readonly int _np;
    private readonly int _ns;
    private readonly StlOptions _o;
    private readonly LoessPlan _trend;
    private readonly LoessPlan _lowPass;
    private readonly double[] _w1, _w3, _w4, _cycle, _fit;
    private readonly double[] _sub, _subW, _smooth, _subWork;
    private readonly double[] _absResid, _sortScratch;

    public StlEngine(int n, StlOptions options)
    {
        _np = options.Period;
        if (_np < 2) throw new ArgumentException("Period must be at least 2.");
        if (n < 2 * _np)
            throw new ArgumentException($"STL requires at least two full periods ({2 * _np} observations); got {n}.");
        _n = n;
        _o = options;
        _ns = Math.Max(3, options.SeasonalWindow | 1);
        _trend = new LoessPlan(n, Math.Max(3, options.ResolvedTrendWindow | 1), options.TrendDegree);
        _lowPass = new LoessPlan(n, Math.Max(3, options.ResolvedLowPassWindow | 1), options.LowPassDegree);
        var m = n + 2 * _np;
        _w1 = new double[m];
        _w3 = new double[m];
        _w4 = new double[m];
        _cycle = new double[m];
        _fit = new double[n];
        var k = n / _np + 3;
        _sub = new double[k];
        _subW = new double[k];
        _smooth = new double[k];
        _subWork = new double[k];
        _absResid = new double[n];
        _sortScratch = new double[n];
        Trend = new double[n];
        Seasonal = new double[n];
        Weights = new double[n];
    }

    public double[] Trend { get; }
    public double[] Seasonal { get; }
    public double[] Weights { get; }

    public void Run(double[] y)
    {
        if (y.Length != _n) throw new ArgumentException("Length mismatch.");
        Array.Clear(Trend);
        Array.Clear(Seasonal);
        var userWeights = false;
        var outer = _o.ResolvedOuter;
        for (var k = 0; ; k++)
        {
            InnerLoop(y, _o.ResolvedInner, userWeights);
            if (k >= outer) break;
            for (var i = 0; i < _n; i++) _fit[i] = Trend[i] + Seasonal[i];
            Loess.RobustnessWeights(y, _fit, Weights, _absResid, _sortScratch);
            userWeights = true;
        }

        if (outer <= 0) Array.Fill(Weights, 1.0);
    }

    private void InnerLoop(double[] y, int ni, bool userw)
    {
        var n = _n;
        var np = _np;
        for (var iter = 0; iter < ni; iter++)
        {
            for (var i = 0; i < n; i++) _w1[i] = y[i] - Trend[i];

            CycleSubseriesSmooth(_w1, userw);

            LowPassFilter(_cycle, n + 2 * np, np, _w3, _w4);
            _lowPass.Smooth(_w3, false, Weights, _w1);

            for (var i = 0; i < n; i++) Seasonal[i] = _cycle[np + i] - _w1[i];

            for (var i = 0; i < n; i++) _w1[i] = y[i] - Seasonal[i];
            _trend.Smooth(_w1, userw, Weights, Trend);
        }
    }

    private void CycleSubseriesSmooth(double[] y, bool userw)
    {
        var n = _n;
        var np = _np;
        var ns = _ns;
        var isdeg = _o.SeasonalDegree;
        for (var j = 1; j <= np; j++)
        {
            var k = (n - j) / np + 1;
            for (var i = 1; i <= k; i++)
            {
                _sub[i - 1] = y[(i - 1) * np + j - 1];
                if (userw) _subW[i - 1] = Weights[(i - 1) * np + j - 1];
            }

            Loess.Smooth(_sub.AsSpan(0, k), k, ns, isdeg, userw, _subW.AsSpan(0, k), _smooth.AsSpan(1, k), _subWork);

            var nright = Math.Min(ns, k);
            if (!Loess.Estimate(_sub, k, ns, isdeg, 0, 1, nright, _subWork, userw, _subW, out var left))
                left = _smooth[1];
            _smooth[0] = left;

            var nleft = Math.Max(1, k - ns + 1);
            if (!Loess.Estimate(_sub, k, ns, isdeg, k + 1, nleft, k, _subWork, userw, _subW, out var right))
                right = _smooth[k];
            _smooth[k + 1] = right;

            for (var m = 1; m <= k + 2; m++) _cycle[(m - 1) * np + j - 1] = _smooth[m - 1];
        }
    }

    private static void LowPassFilter(double[] x, int n, int np, double[] output, double[] tmp)
    {
        MovingAverage(x, n, np, tmp);
        MovingAverage(tmp, n - np + 1, np, output);
        MovingAverage(output, n - 2 * np + 2, 3, tmp);
        Array.Copy(tmp, output, n - 2 * np);
    }

    private static void MovingAverage(double[] x, int n, int len, double[] ave)
    {
        var newN = n - len + 1;
        var v = 0.0;
        for (var i = 0; i < len; i++) v += x[i];
        ave[0] = v / len;
        for (var j = 1; j < newN; j++)
        {
            v = v - x[j - 1] + x[j + len - 1];
            ave[j] = v / len;
        }
    }
}
