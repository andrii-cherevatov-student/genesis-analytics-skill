using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Statistics;

namespace WikipediaInterestSkill.Robustness;

public static class LeaveOneOutAnalyzer
{
    private const string MethodNote =
        "Each observed month in the reporting period removed in turn (imputed from STL), decomposition and Theil-Sen " +
        "slope re-estimated; interval width taken from the primary bootstrap.";

    internal static LeaveOneOutResult Analyze(SeriesSlice slice, TrendFit baseFit, AnalysisSettings settings)
    {
        var candidates = baseFit.ObservedWindowIndices.ToArray();
        var fits = new LeaveOneOutFit[candidates.Length];
        Parallel.For(0, candidates.Length, settings.Parallelism, k =>
        {
            var i = candidates[k];
            var excluded = new HashSet<int>(slice.Excluded) { i };
            var estimate = TrendFitter.PointEstimate(slice.AdjustedViews, excluded, slice.WindowStart, slice.WindowEnd,
                settings.Stl);
            var (_, cls) = TrendFitter.ClassifyWithDeviations(estimate.MonthlyLogSlope, baseFit.SortedSlopeDeviations,
                settings);
            fits[k] = new LeaveOneOutFit(slice.Months[i], estimate.AnnualizedGrowth,
                estimate.AnnualizedGrowth - baseFit.Estimate.AnnualizedGrowth, cls);
        });

        if (fits.Length == 0)
            return new LeaveOneOutResult(0, 0, null, 1, 1, MethodNote, fits);

        var worst = fits.MaxBy(f => Math.Abs(f.Shift))!;
        var baseSign = Math.Sign(baseFit.Estimate.AnnualizedGrowth);
        return new LeaveOneOutResult(
            fits.Length,
            Math.Abs(worst.Shift),
            worst.Month,
            Descriptive.Fraction(fits, f => f.Classification == baseFit.Classification),
            Descriptive.Fraction(fits, f => Math.Sign(f.AnnualizedGrowth) == baseSign),
            MethodNote,
            fits);
    }
}
