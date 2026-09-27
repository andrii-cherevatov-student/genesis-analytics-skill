using MathNet.Numerics.Distributions;
using MathNet.Numerics.Statistics;

namespace WikipediaInterestSkill.Statistics;

public static class MannKendallTest
{
    public static MannKendallResult Test(IReadOnlyList<double> x, bool? forceModified = null)
    {
        var n = x.Count;
        if (n < 4) throw new ArgumentException("Mann-Kendall requires at least four observations.");

        var s = 0.0;
        for (var i = 0; i < n - 1; i++)
        for (var j = i + 1; j < n; j++)
            s += Math.Sign(x[j] - x[i]);

        var tieTerm = x.GroupBy(v => v).Where(g => g.Count() > 1)
            .Sum(g => { var t = (double)g.Count(); return t * (t - 1) * (2 * t + 5); });
        var varS = (n * (n - 1.0) * (2 * n + 5.0) - tieTerm) / 18.0;

        var sen = TheilSenEstimator.Slope(x.ToArray());
        var detrended = x.Select((v, i) => v - sen * i).ToArray();
        var lag1 = AutocorrelationAnalyzer.Acf(detrended, 1)[0];
        var material = Math.Abs(lag1) > 1.96 / Math.Sqrt(n);
        var useModified = forceModified ?? material;

        var factor = 1.0;
        if (useModified)
        {
            factor = HamedRaoFactor(detrended);
            varS *= factor;
        }

        double z;
        if (varS <= 0) z = 0;
        else if (s > 0) z = (s - 1) / Math.Sqrt(varS);
        else if (s < 0) z = (s + 1) / Math.Sqrt(varS);
        else z = 0;

        var p = 2.0 * (1.0 - Normal.CDF(0, 1, Math.Abs(z)));
        var tau = s / (n * (n - 1) / 2.0);
        return new MannKendallResult(tau, Math.Clamp(p, 0, 1),
            useModified ? MannKendallVariant.HamedRaoModified : MannKendallVariant.Standard, s, z, factor, n);
    }

    internal static double HamedRaoFactor(IReadOnlyList<double> detrended)
    {
        var n = detrended.Count;
        var ranks = ArrayStatistics.RanksInplace(detrended.ToArray(), RankDefinition.Average);
        var maxLag = Math.Min(n - 1, Math.Max(3, n / 3));
        var acf = AutocorrelationAnalyzer.Acf(ranks, maxLag);
        var bound = 1.96 / Math.Sqrt(n);
        var sum = 0.0;
        for (var k = 1; k <= acf.Length; k++)
        {
            var r = acf[k - 1];
            if (Math.Abs(r) <= bound) continue;
            sum += (n - k) * (n - k - 1.0) * (n - k - 2.0) * r;
        }

        var factor = 1.0 + 2.0 * sum / (n * (n - 1.0) * (n - 2.0));
        return Math.Max(1.0, factor);
    }
}
