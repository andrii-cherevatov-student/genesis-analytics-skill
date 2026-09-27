using MathNet.Numerics.Distributions;

namespace WikipediaInterestSkill.Statistics;

public sealed record AutocorrelationDiagnostics(
    double Lag1,
    IReadOnlyList<double> Acf,
    int LjungBoxLags,
    double LjungBoxPValue,
    bool MaterialLag1Dependence);

public static class AutocorrelationAnalyzer
{
    public static double[] Acf(IReadOnlyList<double> x, int maxLag)
    {
        var n = x.Count;
        if (n < 3) throw new ArgumentException("ACF requires at least three observations.");
        maxLag = Math.Clamp(maxLag, 1, n - 1);
        var mean = x.Average();
        var denom = 0.0;
        for (var t = 0; t < n; t++) denom += (x[t] - mean) * (x[t] - mean);
        var acf = new double[maxLag];
        if (denom <= 0) return acf;
        for (var k = 1; k <= maxLag; k++)
        {
            var num = 0.0;
            for (var t = 0; t < n - k; t++) num += (x[t] - mean) * (x[t + k] - mean);
            acf[k - 1] = num / denom;
        }

        return acf;
    }

    public static AutocorrelationDiagnostics Analyze(IReadOnlyList<double> x, int? maxLag = null)
    {
        var n = x.Count;
        var lags = maxLag ?? Math.Min(12, Math.Max(1, n / 4));
        var acf = Acf(x, lags);
        var q = 0.0;
        for (var k = 1; k <= acf.Length; k++) q += acf[k - 1] * acf[k - 1] / (n - k);
        q *= n * (n + 2.0);
        var p = 1.0 - ChiSquared.CDF(acf.Length, q);
        return new AutocorrelationDiagnostics(acf[0], acf, acf.Length, p, Math.Abs(acf[0]) > 1.96 / Math.Sqrt(n));
    }
}
