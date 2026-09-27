using WikipediaInterestSkill.TimeSeries;

namespace WikipediaInterestSkill.Statistics;

public static class TheilSenEstimator
{
    public static double Slope(ReadOnlySpan<double> y) => SlopeWithScratch(y, new double[y.Length * (y.Length - 1) / 2]);

    public static double SlopeWithScratch(ReadOnlySpan<double> y, double[] scratch)
    {
        var n = y.Length;
        if (n < 2) throw new ArgumentException("Theil-Sen requires at least two observations.");
        var slopes = scratch;
        if (slopes.Length != n * (n - 1) / 2) throw new ArgumentException("Scratch buffer has the wrong size.");
        var k = 0;
        for (var i = 0; i < n - 1; i++)
        for (var j = i + 1; j < n; j++)
            slopes[k++] = (y[j] - y[i]) / (j - i);
        return Median.OfInPlace(slopes);
    }

    public static double Intercept(ReadOnlySpan<double> y, double slope)
    {
        var r = new double[y.Length];
        for (var i = 0; i < r.Length; i++) r[i] = y[i] - slope * i;
        return Median.OfInPlace(r);
    }

    public static TrendEstimate Estimate(ReadOnlySpan<double> logTrend)
    {
        var beta = Slope(logTrend);
        return new TrendEstimate(beta, LogTransformer.AnnualizeMonthlyLogSlope(beta));
    }
}
