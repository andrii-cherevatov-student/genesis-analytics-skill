using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.TimeSeries;
using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.Statistics;

public sealed class InsufficientDataException(string message) : SkillException(ErrorCodes.InsufficientData, message);

internal sealed class TrendFit
{
    public required double[] LogValues { get; init; }

    public required bool[] Imputed { get; init; }

    public required TimeSeriesDecomposition Decomposition { get; init; }

    public required int WindowStart { get; init; }

    public required int WindowEnd { get; init; }

    public required TrendEstimate Estimate { get; init; }

    public required ConfidenceInterval SlopeInterval { get; init; }

    public required ConfidenceInterval GrowthInterval { get; init; }

    public required double[] ReplicateGrowth { get; init; }

    public required double[] SortedSlopeDeviations { get; init; }

    public required double SlopeStandardError { get; init; }

    public required TrendClassification Classification { get; init; }

    public required int BootstrapIterations { get; init; }

    public required int BlockLength { get; init; }

    public required LongRunVarianceCorrection DependenceCorrection { get; init; }

    public IEnumerable<int> ObservedWindowIndices =>
        Enumerable.Range(WindowStart, WindowEnd - WindowStart + 1).Where(i => !Imputed[i]);

    public double SeasonallyAdjusted(int i) => LogValues[i] - Decomposition.Seasonal[i];

    public double TrendIndex(double logValue) => 100 * Math.Exp(logValue - Decomposition.Trend[WindowStart]);
}

internal static class TrendFitter
{
    private const int ImputationPasses = 3;

    private sealed class ReplicateState(int n, int windowLength, StlOptions options)
    {
        public double[] Noise { get; } = new double[n];
        public double[] Replicate { get; } = new double[n];
        public double[] Window { get; } = new double[windowLength];
        public double[] Pairs { get; } = new double[windowLength * (windowLength - 1) / 2];
        public StlEngine Stl { get; } = new(n, options);
    }

    public static TrendFit Fit(
        IReadOnlyList<double?> adjustedViews,
        IReadOnlySet<int> excluded,
        int windowStart,
        int windowEnd,
        AnalysisSettings settings,
        int seed,
        int? bootstrapIterations = null)
    {
        var n = adjustedViews.Count;
        var windowLength = windowEnd - windowStart + 1;
        if (windowStart < 0 || windowEnd >= n || windowEnd < windowStart)
            throw new ArgumentOutOfRangeException(nameof(windowStart), "Reporting window outside the series.");
        if (n < AnalysisSettings.MinimumTotalMonths)
            throw new InsufficientDataException(
                $"Only {n} months of data are available; at least {AnalysisSettings.MinimumTotalMonths} are required for seasonal decomposition.");
        if (windowLength < AnalysisSettings.MinimumReportingMonths)
            throw new InsufficientDataException(
                $"The reporting window has {windowLength} months; at least {AnalysisSettings.MinimumReportingMonths} are required.");

        var (y, imputed) = Prepare(adjustedViews, excluded);
        var observedCount = imputed.Count(m => !m);
        var observedInWindow = imputed.Skip(windowStart).Take(windowLength).Count(m => !m);
        if (observedCount < AnalysisSettings.MinimumTotalMonths * 3 / 4 || observedInWindow < windowLength * 3 / 4)
            throw new InsufficientDataException(
                "Too many months are missing or excluded to estimate a trend reliably (need at least 75% observed months).");

        var decomposition = DecomposeWithImputation(y, imputed, settings.Stl);
        var estimate = WindowEstimate(decomposition, windowStart, windowLength);

        var iterations = bootstrapIterations ?? settings.BootstrapIterations;
        var deviations = BootstrapSlopeDeviations(y, decomposition, windowStart, windowEnd, estimate.MonthlyLogSlope,
            settings, iterations, seed, out var dependence);
        var replicateGrowth = deviations
            .Select(d => LogTransformer.AnnualizeMonthlyLogSlope(estimate.MonthlyLogSlope - d)).ToArray();
        Array.Sort(deviations);

        var slopeCi = SlopeInterval(estimate.MonthlyLogSlope, deviations);
        var (growthCi, classification) = Classify(slopeCi, settings.PracticalThreshold);

        return new TrendFit
        {
            LogValues = y,
            Imputed = imputed,
            Decomposition = decomposition,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Estimate = estimate,
            SlopeInterval = slopeCi,
            GrowthInterval = growthCi,
            ReplicateGrowth = replicateGrowth,
            SortedSlopeDeviations = deviations,
            SlopeStandardError = Descriptive.SampleStandardDeviation(deviations),
            Classification = classification,
            BootstrapIterations = iterations,
            BlockLength = settings.BlockLength,
            DependenceCorrection = dependence,
        };
    }

    public static TrendEstimate PointEstimate(
        IReadOnlyList<double?> adjustedViews, IReadOnlySet<int> excluded, int windowStart, int windowEnd, StlOptions options)
    {
        var (y, imputed) = Prepare(adjustedViews, excluded);
        return WindowEstimate(DecomposeWithImputation(y, imputed, options), windowStart, windowEnd - windowStart + 1);
    }

    public static (ConfidenceInterval Growth, TrendClassification Classification) ClassifyWithDeviations(
        double slope, double[] sortedDeviations, AnalysisSettings settings) =>
        Classify(SlopeInterval(slope, sortedDeviations), settings.PracticalThreshold);

    private static ConfidenceInterval SlopeInterval(double slope, double[] sortedDeviations)
    {
        const double alpha = 1.0 - AnalysisSettings.ConfidenceLevel;
        return new ConfidenceInterval(
            slope - Median.QuantileSorted(sortedDeviations, 1 - alpha / 2),
            slope - Median.QuantileSorted(sortedDeviations, alpha / 2),
            AnalysisSettings.ConfidenceLevel);
    }

    private static (ConfidenceInterval Growth, TrendClassification Classification) Classify(
        ConfidenceInterval slopeInterval, double threshold)
    {
        var growth = new ConfidenceInterval(
            LogTransformer.AnnualizeMonthlyLogSlope(slopeInterval.Lower),
            LogTransformer.AnnualizeMonthlyLogSlope(slopeInterval.Upper),
            slopeInterval.ConfidenceLevel);
        return (growth, EquivalenceClassifier.Classify(growth, threshold));
    }

    private static (double[] Y, bool[] Imputed) Prepare(IReadOnlyList<double?> adjustedViews, IReadOnlySet<int> excluded)
    {
        var n = adjustedViews.Count;
        var y = new double[n];
        var imputed = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (adjustedViews[i] is { } v && !excluded.Contains(i)) y[i] = LogTransformer.Forward(v);
            else imputed[i] = true;
        }

        return (y, imputed);
    }

    private static TrendEstimate WindowEstimate(TimeSeriesDecomposition decomposition, int windowStart, int windowLength) =>
        TheilSenEstimator.Estimate(decomposition.Trend.Skip(windowStart).Take(windowLength).ToArray());

    internal static TimeSeriesDecomposition DecomposeWithImputation(double[] y, bool[] imputed, StlOptions options)
    {
        if (!imputed.Any(b => b)) return StlDecomposer.Decompose(y, options);

        LinearInterpolate(y, imputed);
        var engine = new StlEngine(y.Length, options);
        for (var pass = 0; pass < ImputationPasses; pass++)
        {
            engine.Run(y);
            for (var i = 0; i < y.Length; i++)
                if (imputed[i]) y[i] = engine.Trend[i] + engine.Seasonal[i];
        }

        return StlDecomposer.Decompose(y, options);
    }

    private static void LinearInterpolate(double[] y, bool[] missing)
    {
        var n = y.Length;
        var known = Enumerable.Range(0, n).Where(i => !missing[i]).ToArray();
        if (known.Length == 0) throw new InsufficientDataException("No observed months.");
        for (var i = 0; i < n; i++)
        {
            if (!missing[i]) continue;
            var prev = known.LastOrDefault(k => k < i, -1);
            var next = known.FirstOrDefault(k => k > i, -1);
            if (prev < 0) y[i] = y[next];
            else if (next < 0) y[i] = y[prev];
            else y[i] = y[prev] + (y[next] - y[prev]) * (i - prev) / (double)(next - prev);
        }
    }

    private static double[] BootstrapSlopeDeviations(
        double[] y,
        TimeSeriesDecomposition decomposition,
        int windowStart,
        int windowEnd,
        double estimate,
        AnalysisSettings settings,
        int iterations,
        int seed,
        out LongRunVarianceCorrection dependence)
    {
        var n = y.Length;
        var half = (settings.Stl.ResolvedTrendWindow - 1) / 2;
        var u0 = Math.Max(0, windowStart - half);
        var u1 = Math.Min(n - 1, windowEnd + half);
        var m = u1 - u0 + 1;

        var d = new double[m];
        for (var i = 0; i < m; i++) d[i] = y[u0 + i] - decomposition.Seasonal[u0 + i];
        var b = TheilSenEstimator.Slope(d);
        var a = TheilSenEstimator.Intercept(d, b);

        var dfSeasonal = StlDecomposer.SeasonalDegreesOfFreedom(n, settings.Stl);
        var inflation = Math.Sqrt(n / Math.Max(1.0, n - dfSeasonal - 2.0));
        var residuals = new double[m];
        for (var i = 0; i < m; i++) residuals[i] = (d[i] - (a + b * i)) * inflation;
        dependence = LongRunVarianceCorrection.Compute(residuals, settings.BlockLength);

        var fitted = new double[n];
        for (var i = 0; i < n; i++) fitted[i] = decomposition.Trend[i] + decomposition.Seasonal[i];

        var windowLength = windowEnd - windowStart + 1;
        var deviations = new double[iterations];
        var scale = dependence.ScaleFactor;
        var blockLength = settings.BlockLength;
        var stl = settings.Stl;

        Parallel.For(0, iterations, settings.Parallelism,
            () => new ReplicateState(n, windowLength, stl),
            (r, _, state) =>
            {
                var rng = new Random(unchecked(seed * 1_000_003 + r * 7_919 + 17));
                BlockBootstrap.Resample(residuals, blockLength, rng, state.Noise);
                for (var i = 0; i < n; i++) state.Replicate[i] = fitted[i] + state.Noise[i];
                state.Stl.Run(state.Replicate);
                Array.Copy(state.Stl.Trend, windowStart, state.Window, 0, windowLength);
                deviations[r] = (TheilSenEstimator.SlopeWithScratch(state.Window, state.Pairs) - estimate) * scale;
                return state;
            },
            _ => { });

        return deviations;
    }
}
