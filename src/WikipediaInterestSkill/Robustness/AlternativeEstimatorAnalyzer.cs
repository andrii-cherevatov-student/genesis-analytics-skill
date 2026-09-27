using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries;

namespace WikipediaInterestSkill.Robustness;

public static class AlternativeEstimatorAnalyzer
{
    internal static AlternativeEstimatorsResult Analyze(SeriesSlice slice, TrendFit fit)
    {
        double? Usable(int i) => i < 0 || fit.Imputed[i] ? null : slice.AdjustedViews[i];

        var estimates = new List<(string Name, string Description, double? Growth)>();

        var observed = fit.ObservedWindowIndices.ToArray();
        double? ols = null;
        if (observed.Length >= 6)
        {
            var (_, slope) = MathNet.Numerics.Fit.Line(observed.Select(i => (double)i).ToArray(),
                observed.Select(fit.SeasonallyAdjusted).ToArray());
            ols = LogTransformer.AnnualizeMonthlyLogSlope(slope);
        }

        estimates.Add(("OLS on seasonally adjusted log views",
            "Least-squares slope of log(1+views) − seasonal component over the reporting period, annualized.", ols));

        double? yoyAggregate = null;
        var last = slice.WindowEnd;
        if (last - 23 >= 0)
        {
            double recent = 0, previous = 0;
            var pairs = 0;
            for (var k = 0; k < 12; k++)
            {
                var a = Usable(last - k);
                var b = Usable(last - k - 12);
                if (a is null || b is null) continue;
                recent += a.Value;
                previous += b.Value;
                pairs++;
            }

            if (pairs >= 9 && previous > 0) yoyAggregate = recent / previous - 1;
        }

        estimates.Add(("Year-over-year aggregate growth",
            "Views in the last 12 months divided by views in the preceding 12 months, minus 1.", yoyAggregate));

        var ratios = new List<double>();
        for (var i = slice.WindowStart; i <= slice.WindowEnd; i++)
        {
            var a = Usable(i);
            var b = Usable(i - 12);
            if (a is null || b is null || b.Value <= 0) continue;
            ratios.Add(a.Value / b.Value - 1);
        }

        double? medianYoy = ratios.Count >= 6 ? Median.Of(ratios) : null;
        estimates.Add(("Median monthly year-over-year growth",
            "Median over reporting months of views(t) / views(t−12) − 1.", medianYoy));

        var primary = fit.Estimate.AnnualizedGrowth;
        var result = estimates.Select(e => new AlternativeEstimate(
            e.Name,
            e.Description,
            e.Growth,
            e.Growth is { } g ? Math.Sign(g) == Math.Sign(primary) : null,
            e.Growth is { } g2 ? fit.GrowthInterval.Contains(g2) : null)).ToList();
        var available = result.Where(r => r.AgreesInDirection is not null).ToList();
        return new AlternativeEstimatorsResult(result, Descriptive.Fraction(available, r => r.AgreesInDirection == true));
    }
}
