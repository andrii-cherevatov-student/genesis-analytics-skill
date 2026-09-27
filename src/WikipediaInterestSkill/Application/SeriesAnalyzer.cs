using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WikipediaInterestSkill.Anomalies;
using WikipediaInterestSkill.Robustness;
using WikipediaInterestSkill.Seasonality;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries.Models;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.Application;

internal sealed record LanguageAnalysis(LanguageAnalysisResult Result, SeriesSlice Slice, TrendFit Fit);

internal static class SeriesAnalyzer
{
    public const double LowVolumeDailyViews = 50;

    public static LanguageAnalysis Analyze(
        ResolvedArticle article,
        IReadOnlyList<MonthlyObservation> observations,
        YearMonth windowStart,
        YearMonth windowEnd,
        IReadOnlyCollection<YearMonth> excludedMonths,
        AnalysisSettings settings,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var sw = Stopwatch.StartNew();
        var slice = SeriesSlice.Create(observations, windowStart, windowEnd, settings.HistoryMonths, excludedMonths);
        var seed = BlockBootstrap.StableSeed(article.Language, article.ArticleTitle, slice.FirstWindowMonth,
            slice.LastWindowMonth, string.Join(",", excludedMonths.OrderBy(m => m)));
        var fit = TrendFitter.Fit(slice.AdjustedViews, slice.Excluded, slice.WindowStart, slice.WindowEnd, settings, seed);
        logger.LogInformation("{Language}: decomposition + bootstrap ({Iterations} replicates, block {Block}) in {ElapsedMs} ms",
            article.Language, fit.BootstrapIterations, fit.BlockLength, sw.ElapsedMilliseconds);
        var warnings = new List<AnalysisWarning>();

        if (slice.WindowStartShifted)
            warnings.Add(new AnalysisWarning(WarningCodes.DataStartsLate, WarningSeverity.Caution,
                $"No pageviews are recorded for '{article.ArticleTitle}' before {slice.FirstWindowMonth}; the analysis " +
                $"starts there instead of {slice.RequestedWindowStart}. The article may have been created or renamed then.",
                article.Language));

        var mk = MannKendallTest.Test(fit.ObservedWindowIndices.Select(fit.SeasonallyAdjusted).ToList());

        var seasonality = SeasonalityAnalyzer.Analyze(fit.Decomposition, slice.Months, slice.WindowStart, slice.WindowEnd,
            fit.Imputed);
        var anomalies = ResidualAnomalyDetector.Detect(fit.Decomposition, slice.Months, fit.Imputed, slice.ObservedViews,
            slice.AdjustedViews, slice.Excluded, slice.WindowStart, slice.WindowEnd, settings.AnomalyThreshold);

        var volume = Volume(slice);
        var quality = Quality(slice);
        sw.Restart();
        var loo = LeaveOneOutAnalyzer.Analyze(slice, fit, settings);
        logger.LogInformation("{Language}: leave-one-out ({Fits} refits) in {ElapsedMs} ms", article.Language, loo.Fits,
            sw.ElapsedMilliseconds);
        sw.Restart();
        var windows = WindowSensitivityAnalyzer.Analyze(observations, slice, fit, excludedMonths, settings, seed);
        logger.LogInformation("{Language}: window sensitivity ({Windows} windows) in {ElapsedMs} ms", article.Language,
            windows.Windows.Count - 1, sw.ElapsedMilliseconds);
        var alternatives = AlternativeEstimatorAnalyzer.Analyze(slice, fit);
        var residuals = Residuals(fit);
        var robustness = BuildRobustness(article, slice, fit, volume, loo, windows, alternatives, residuals, settings);

        var shifts = loo.PerMonth.ToDictionary(f => f.Month, f => f.Shift);
        anomalies = anomalies
            .Select(a => a with { TrendShiftIfExcluded = shifts.TryGetValue(a.Period, out var s) ? s : null })
            .ToList();

        if (article.Confidence != ResolutionConfidence.High)
            warnings.Add(new AnalysisWarning(WarningCodes.UncertainArticleMapping, WarningSeverity.Caution,
                $"The {article.Language} article '{article.ArticleTitle}' was matched with {article.Confidence.ToString().ToLowerInvariant()} " +
                "confidence; check that it represents the same concept before comparing languages.", article.Language));

        var trend = new TrendAnalysis(
            fit.Classification,
            fit.Estimate.AnnualizedGrowth,
            fit.GrowthInterval,
            fit.Estimate.MonthlyLogSlope,
            settings.PracticalThreshold,
            CompatibleWith(fit.GrowthInterval, settings.PracticalThreshold),
            mk,
            new BootstrapInfo(
                "Residual moving-block bootstrap with full STL re-decomposition per replicate; basic (pivotal) interval",
                fit.BootstrapIterations,
                fit.BlockLength,
                fit.SlopeStandardError,
                fit.DependenceCorrection.Lag1Autocorrelation,
                fit.DependenceCorrection.DependenceFactor,
                fit.DependenceCorrection.StudentTFactor),
            TrendInterpretation(fit, settings.PracticalThreshold));

        var summary = Summary(article, trend, seasonality, anomalies, robustness);
        var result = new LanguageAnalysisResult(article.Language, article, summary, volume, trend, seasonality, anomalies,
            robustness, quality, warnings);
        return new LanguageAnalysis(result, slice, fit);
    }

    internal static VolumeMetrics Volume(SeriesSlice slice)
    {
        var window = Enumerable.Range(slice.WindowStart, slice.WindowLength).Select(i => slice.Observations[i]).ToList();
        var total = window.Sum(o => o.ObservedViews);
        var days = window.Sum(o => o.DaysWithData);
        var totalDays = window.Sum(o => o.DaysInMonth);
        var peak = window.MaxBy(o => o.ObservedViews)!;

        long? SumBlock(int endIndex)
        {
            var start = endIndex - 11;
            if (start < 0) return null;
            var block = slice.Observations.Skip(start).Take(12).ToList();
            if (block.Any(o => o.Status == MonthStatus.Missing)) return null;
            return block.Sum(o => o.ObservedViews);
        }

        var last12 = SumBlock(slice.WindowEnd);
        var prev12 = SumBlock(slice.WindowEnd - 12);
        double? change = last12 is { } l && prev12 is > 0 ? (double)l / prev12.Value - 1 : null;
        return new VolumeMetrics(total, window.Count, Math.Round(total / (double)window.Count, 1),
            days > 0 ? Math.Round(total / (double)days, 1) : 0, last12, prev12, change, peak.Period, peak.ObservedViews,
            totalDays > 0 ? Math.Round(days / (double)totalDays, 4) : 0);
    }

    private static DataQuality Quality(SeriesSlice slice)
    {
        var obs = slice.Observations;
        var days = obs.Sum(o => o.DaysWithData);
        var totalDays = obs.Sum(o => o.DaysInMonth);
        return new DataQuality(
            slice.Months[0],
            slice.Months[^1],
            obs.Count,
            slice.WindowStart,
            obs.Where(o => o.Status == MonthStatus.Scaled).Select(o => o.Period).ToList(),
            obs.Where(o => o.Status == MonthStatus.Missing).Select(o => o.Period).ToList(),
            slice.Excluded.Order().Select(i => slice.Months[i]).ToList(),
            totalDays > 0 ? Math.Round(days / (double)totalDays, 4) : 0);
    }

    private static ResidualDiagnostics Residuals(TrendFit fit)
    {
        var r = fit.ObservedWindowIndices.Select(i => fit.Decomposition.Remainder[i]).ToList();
        var ac = AutocorrelationAnalyzer.Analyze(r);
        var text = ac.LjungBoxPValue < 0.05
            ? $"Remainder is autocorrelated (lag-1 r = {ac.Lag1:0.00}, Ljung-Box p = {ac.LjungBoxPValue:0.000}); short-range " +
              "dependence is handled by the block bootstrap, but slow swings can still make the interval optimistic."
            : $"No material residual autocorrelation (lag-1 r = {ac.Lag1:0.00}, Ljung-Box p = {ac.LjungBoxPValue:0.00}).";
        return new ResidualDiagnostics(Math.Round(ac.Lag1, 4), ac.Acf.Select(v => Math.Round(v, 4)).ToList(),
            Math.Round(ac.LjungBoxPValue, 4), ac.LjungBoxLags, ac.LjungBoxPValue < 0.05, text);
    }

    private static HistorySufficiency History(SeriesSlice slice)
    {
        var reporting = slice.WindowLength;
        var total = slice.Months.Count;
        var cycles = Math.Round(total / 12.0, 1);
        var sufficient = reporting >= 24 && total >= 36;
        var note = sufficient
            ? $"{reporting} reporting months and {total} months in the decomposition ({cycles} seasonal cycles)."
            : reporting < 24
                ? $"Only {reporting} reporting months: less than two full years, so trend and seasonality are harder to separate."
                : $"Only {total} months in the decomposition ({cycles} cycles): seasonal pattern estimated from limited history.";
        return new HistorySufficiency(reporting, total, cycles, sufficient, note);
    }

    private static RobustnessResult BuildRobustness(
        ResolvedArticle article,
        SeriesSlice slice,
        TrendFit fit,
        VolumeMetrics volume,
        LeaveOneOutResult loo,
        WindowSensitivityResult windows,
        AlternativeEstimatorsResult alternatives,
        ResidualDiagnostics residuals,
        AnalysisSettings settings)
    {
        var concerns = new List<RobustnessConcern>();
        var cls = fit.Classification;
        var history = History(slice);

        if (!history.Sufficient)
            concerns.Add(new RobustnessConcern("ShortHistory", WarningSeverity.Caution, history.Note));

        var width = fit.GrowthInterval.Width;
        if (width > 6 * settings.PracticalThreshold)
            concerns.Add(new RobustnessConcern("WideInterval", WarningSeverity.Caution,
                $"The 95% interval spans {width * 100:0} percentage points of annual growth, so the data are compatible with a wide range of trends."));

        var looChanges = loo.PerMonth.Where(f => f.Classification != cls).ToList();
        if (looChanges.Count > 0)
        {
            var worst = looChanges.MaxBy(f => Math.Abs(f.Shift))!;
            concerns.Add(new RobustnessConcern("SingleMonthInfluence", WarningSeverity.Serious,
                $"Removing a single month changes the classification in {looChanges.Count} of {loo.Fits} cases " +
                $"(e.g. without {worst.Month}: {worst.Classification}, {Format.Pct(worst.AnnualizedGrowth)}/yr)."));
        }
        else if (loo.MaxAbsoluteShift > Math.Max(settings.PracticalThreshold, 0.5 * Math.Abs(fit.Estimate.AnnualizedGrowth)))
        {
            concerns.Add(new RobustnessConcern("SingleMonthInfluence", WarningSeverity.Caution,
                $"Removing {loo.MostInfluentialMonth} alone shifts the growth estimate by {loo.MaxAbsoluteShift * 100:0.0} percentage points (classification unchanged)."));
        }

        var variantCount = windows.Windows.Count - 1;
        if (variantCount > 0 && windows.DirectionAgreement < 1)
            concerns.Add(new RobustnessConcern("WindowDirection", WarningSeverity.Serious,
                $"Shifting the analysis window by 3 months reverses the direction of the estimate in " +
                $"{Math.Round((1 - windows.DirectionAgreement) * variantCount)} of {variantCount} nearby windows."));
        else if (variantCount > 0 && windows.ClassificationAgreement < 0.6)
            concerns.Add(new RobustnessConcern("WindowClassification", WarningSeverity.Caution,
                $"The classification differs in {Math.Round((1 - windows.ClassificationAgreement) * variantCount)} of {variantCount} nearby windows " +
                $"(estimates range {Format.Pct(windows.MinAnnualizedGrowth)} to {Format.Pct(windows.MaxAnnualizedGrowth)}/yr)."));

        var disagreeing = alternatives.Estimators
            .Where(e => e.AgreesInDirection == false && e.AnnualizedGrowth is { } g && Math.Abs(g) > settings.PracticalThreshold)
            .ToList();
        if (disagreeing.Count > 0)
            concerns.Add(new RobustnessConcern("EstimatorDisagreement", WarningSeverity.Caution,
                "Alternative estimator(s) point the other way: " +
                string.Join("; ", disagreeing.Select(e => $"{e.Name} {Format.Pct(e.AnnualizedGrowth!.Value)}")) + "."));

        if (residuals.MaterialAutocorrelation)
            concerns.Add(new RobustnessConcern("ResidualAutocorrelation", WarningSeverity.Info, residuals.Interpretation));

        if (volume.AverageDailyViews < LowVolumeDailyViews)
            concerns.Add(new RobustnessConcern("LowVolume", WarningSeverity.Caution,
                $"Low traffic (about {volume.AverageDailyViews:0} views/day): small absolute changes, bots or a single external link can move monthly totals."));

        if (article.Confidence != ResolutionConfidence.High)
            concerns.Add(new RobustnessConcern("ArticleMapping", WarningSeverity.Caution,
                $"Article mapping confidence is {article.Confidence}: the page may not represent exactly the requested concept."));

        var imputed = Enumerable.Range(slice.WindowStart, slice.WindowLength).Count(i => fit.Imputed[i]);
        if (imputed > 0)
            concerns.Add(new RobustnessConcern("ImputedMonths", WarningSeverity.Info,
                $"{imputed} reporting month(s) were missing or excluded and were imputed for the decomposition only."));

        var material = concerns.Material().ToList();
        string statement;
        var checks = $"unchanged classification in {loo.ClassificationAgreement * loo.Fits:0} of {loo.Fits} leave-one-out fits, " +
                     $"same direction in {windows.DirectionAgreement * variantCount:0} of {variantCount} nearby windows, " +
                     $"{alternatives.DirectionAgreement * alternatives.Estimators.Count(e => e.AgreesInDirection is not null):0} of " +
                     $"{alternatives.Estimators.Count(e => e.AgreesInDirection is not null)} alternative estimators agree on direction";
        if (material.Count == 0)
            statement = $"The {cls} conclusion is robust to the checks performed: {checks}.";
        else if (material.Any(c => c.Severity == WarningSeverity.Serious))
            statement = $"The {cls} conclusion is fragile: {string.Join(" ", material.Where(c => c.Severity == WarningSeverity.Serious).Select(c => c.Message))} ({checks}).";
        else
            statement = $"The {cls} conclusion holds under the checks performed ({checks}), with caveats: " +
                        string.Join(" ", material.Select(c => c.Message));

        return new RobustnessResult(statement, concerns, width, loo, windows, alternatives, residuals, history);
    }

    internal static IReadOnlyList<string> CompatibleWith(ConfidenceInterval ci, double delta)
    {
        var list = new List<string>();
        if (ci.Lower < -delta) list.Add("decline");
        if (ci.Lower <= delta && ci.Upper >= -delta) list.Add("stability");
        if (ci.Upper > delta) list.Add("growth");
        return list;
    }

    internal static string TrendInterpretation(TrendFit fit, double delta)
    {
        var g = fit.Estimate.AnnualizedGrowth;
        var ci = fit.GrowthInterval;
        var range = $"95% CI {Format.Pct(ci.Lower)} to {Format.Pct(ci.Upper)}";
        var threshold = $"±{Format.Threshold(delta)}";
        return fit.Classification switch
        {
            TrendClassification.Increasing =>
                $"Underlying interest is increasing: {Format.Pct(g)} per year ({range}); the whole interval is above +{Format.Threshold(delta)}.",
            TrendClassification.Decreasing =>
                $"Underlying interest is decreasing: {Format.Pct(g)} per year ({range}); the whole interval is below −{Format.Threshold(delta)}.",
            TrendClassification.Stable =>
                $"Underlying interest is stable: {Format.Pct(g)} per year ({range}); the whole interval lies within the {threshold} practical threshold.",
            _ =>
                $"Inconclusive: the point estimate is {Format.Pct(g)} per year but the {range} is compatible with " +
                $"{JoinOr(CompatibleWith(ci, delta))}. This is not evidence of stability; more history or a clearer signal is needed.",
        };
    }

    private static string Summary(
        ResolvedArticle article,
        TrendAnalysis trend,
        SeasonalityResult seasonality,
        IReadOnlyList<Anomaly> anomalies,
        RobustnessResult robustness)
    {
        var parts = new List<string> { $"[{article.Language}] {article.ArticleTitle}: {trend.Interpretation}" };
        if (seasonality.Category != SeasonalityCategory.Negligible) parts.Add(seasonality.Interpretation);
        if (anomalies.Count > 0)
            parts.Add($"{anomalies.Count} anomalous month(s), largest {anomalies[0].Period} " +
                      $"({Format.Pct0(anomalies[0].RelativeDeviation)} vs expected); kept in the data, down-weighted in the trend.");
        parts.Add(robustness.Statement);
        return string.Join(" ", parts);
    }

    private static string JoinOr(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "no clear pattern",
        1 => items[0],
        2 => $"{items[0]} or {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} or {items[^1]}",
    };
}
