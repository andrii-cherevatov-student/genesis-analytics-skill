using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.Robustness;

public static class WindowSensitivityAnalyzer
{
    public const int IterationsPerWindow = 500;

    private static readonly (int Start, int End)[] Shifts = { (-3, 0), (3, 0), (0, -3), (-3, -3), (3, -3) };

    public static int Iterations(AnalysisSettings settings) => Math.Min(IterationsPerWindow, settings.BootstrapIterations);

    internal static WindowSensitivityResult Analyze(
        IReadOnlyList<MonthlyObservation> all,
        SeriesSlice baseSlice,
        TrendFit baseFit,
        IReadOnlyCollection<YearMonth> excludedMonths,
        AnalysisSettings settings,
        int seed)
    {
        var variants = Shifts
            .Select(s => FitWindow(all, baseSlice.FirstWindowMonth.AddMonths(s.Start), baseSlice.LastWindowMonth.AddMonths(s.End),
                excludedMonths, settings, BlockBootstrap.StableSeed(seed, s.Start, s.End), Label(s.Start, s.End)))
            .OfType<WindowFit>()
            .ToList();
        var windows = new List<WindowFit>
        {
            new("reported window", baseSlice.FirstWindowMonth, baseSlice.LastWindowMonth,
                baseFit.Estimate.AnnualizedGrowth, baseFit.GrowthInterval, baseFit.Classification),
        };
        windows.AddRange(variants);

        var baseSign = Math.Sign(baseFit.Estimate.AnnualizedGrowth);
        return new WindowSensitivityResult(windows,
            Descriptive.Fraction(variants, v => Math.Sign(v.AnnualizedGrowth) == baseSign),
            Descriptive.Fraction(variants, v => v.Classification == baseFit.Classification),
            windows.Min(w => w.AnnualizedGrowth), windows.Max(w => w.AnnualizedGrowth), Iterations(settings));
    }

    internal static WindowFit? FitWindow(
        IReadOnlyList<MonthlyObservation> all,
        YearMonth start,
        YearMonth end,
        IReadOnlyCollection<YearMonth> excludedMonths,
        AnalysisSettings settings,
        int seed,
        string label)
    {
        if (end.MonthsSince(start) + 1 < AnalysisSettings.MinimumReportingMonths || start < all[0].Period) return null;
        try
        {
            var slice = SeriesSlice.Create(all, start, end, settings.HistoryMonths, excludedMonths);
            if (slice.WindowStartShifted) return null;
            var fit = TrendFitter.Fit(slice.AdjustedViews, slice.Excluded, slice.WindowStart, slice.WindowEnd, settings,
                seed, Iterations(settings));
            return new WindowFit(label, slice.FirstWindowMonth, slice.LastWindowMonth, fit.Estimate.AnnualizedGrowth,
                fit.GrowthInterval, fit.Classification);
        }
        catch (InsufficientDataException)
        {
            return null;
        }
    }

    private static string Label(int ds, int de)
    {
        var parts = new List<string>();
        if (ds != 0) parts.Add($"start {ds:+0;-0} months");
        if (de != 0) parts.Add($"end {de:+0;-0} months");
        return string.Join(", ", parts);
    }
}
