using System.Text.Json;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Caching;
using WikipediaInterestSkill.Robustness;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries;
using WikipediaInterestSkill.TimeSeries.Models;
using WikipediaInterestSkill.Wikipedia;

namespace WikipediaInterestSkill.Validation;

public sealed record RealDataCase(string Id, string Category, string Language, string Article, DateOnly From, DateOnly To);

public sealed record RollingOriginFit(YearMonth FirstMonth, YearMonth LastMonth, double AnnualizedGrowth, TrendClassification Classification);

public sealed record RealDataCaseResult(
    RealDataCase Case,
    string? Error,
    TrendClassification? Classification,
    double? AnnualizedGrowth,
    double? CiLower,
    double? CiUpper,
    double? SeasonalityStrength,
    int? Anomalies,
    double? LeaveOneOutMaxShift,
    double? LeaveOneOutClassificationAgreement,
    double? WindowDirectionAgreement,
    double? WindowClassificationAgreement,
    double? EstimatorDirectionAgreement,
    double? ResidualLag1,
    double? LjungBoxPValue,
    double? RollingOriginDirectionAgreement,
    double? RollingOriginClassificationAgreement,
    double? RollingOriginGrowthSpread,
    IReadOnlyList<RollingOriginFit>? RollingOrigin,
    IReadOnlyList<string>? Concerns)
{
    public static RealDataCaseResult Failed(RealDataCase c, string error) => new(c, error, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null, null);
}

public sealed record RealDataReport(DateTimeOffset GeneratedAt, string Note, IReadOnlyList<RealDataCaseResult> Cases);

public static class RealDataValidator
{
    public static readonly int[] RollingOffsets = { 0, 3, 6, 9, 12 };

    public static IReadOnlyList<RealDataCase> LoadCases(string path) =>
        JsonSerializer.Deserialize<List<RealDataCase>>(File.ReadAllText(path), JsonOptions.Default)
        ?? throw new InvalidDataException($"Cannot read cases from {path}.");

    public static async Task<RealDataReport> RunAsync(
        IReadOnlyList<RealDataCase> cases,
        IWikipediaArticleResolver resolver,
        CachedPageviewSource source,
        AnalysisSettings settings,
        DateOnly today,
        CancellationToken ct)
    {
        var results = new List<RealDataCaseResult>();
        foreach (var c in cases)
        {
            try
            {
                results.Add(await RunCaseAsync(c, resolver, source, settings, today, ct).ConfigureAwait(false));
            }
            catch (SkillException ex)
            {
                results.Add(RealDataCaseResult.Failed(c, ex.Message));
            }
        }

        return new RealDataReport(DateTimeOffset.UtcNow,
            "Real Wikipedia series have no known true trend; these metrics describe stability of conclusions only.", results);
    }

    private static async Task<RealDataCaseResult> RunCaseAsync(
        RealDataCase c, IWikipediaArticleResolver resolver, CachedPageviewSource source, AnalysisSettings settings,
        DateOnly today, CancellationToken ct)
    {
        var window = ReportingWindow.FromDates(c.From, c.To, today);
        var article = await resolver.ResolveAsync(c.Article, c.Language, c.Language, c.Article, ct).ConfigureAwait(false);
        var earliest = window.FetchStart(settings.HistoryMonths, 3 + RollingOffsets.Max());
        var daily = await source.GetAsync(article, earliest.FirstDay, window.Last.LastDay, ct).ConfigureAwait(false);
        var monthly = MonthlyAggregator.Aggregate(daily, earliest, window.Last);

        var analysis = SeriesAnalyzer.Analyze(article, monthly, window.First, window.Last, Array.Empty<YearMonth>(), settings);
        var r = analysis.Result;

        var rolling = RollingOffsets
            .Select(offset => WindowSensitivityAnalyzer.FitWindow(monthly, window.First.AddMonths(-offset),
                window.Last.AddMonths(-offset), Array.Empty<YearMonth>(), settings, BlockBootstrap.StableSeed(c.Id, offset),
                $"origin −{offset} months"))
            .OfType<WindowFit>()
            .Select(f => new RollingOriginFit(f.FirstMonth, f.LastMonth, f.AnnualizedGrowth, f.Classification))
            .ToList();

        var baseSign = Math.Sign(r.Trend.AnnualizedGrowth);
        double? rollDir = rolling.Count > 0 ? Descriptive.Fraction(rolling, f => Math.Sign(f.AnnualizedGrowth) == baseSign) : null;
        double? rollCls = rolling.Count > 0 ? Descriptive.Fraction(rolling, f => f.Classification == r.Trend.Classification) : null;
        double? spread = rolling.Count > 1 ? rolling.Max(f => f.AnnualizedGrowth) - rolling.Min(f => f.AnnualizedGrowth) : null;

        return new RealDataCaseResult(c, null, r.Trend.Classification, r.Trend.AnnualizedGrowth,
            r.Trend.ConfidenceInterval95.Lower, r.Trend.ConfidenceInterval95.Upper, r.Seasonality.Strength, r.Anomalies.Count,
            r.Robustness.LeaveOneOut.MaxAbsoluteShift, r.Robustness.LeaveOneOut.ClassificationAgreement,
            r.Robustness.WindowSensitivity.DirectionAgreement, r.Robustness.WindowSensitivity.ClassificationAgreement,
            r.Robustness.AlternativeEstimators.DirectionAgreement, r.Robustness.ResidualDiagnostics.Lag1Autocorrelation,
            r.Robustness.ResidualDiagnostics.LjungBoxPValue, rollDir, rollCls, spread, rolling,
            r.Robustness.Concerns.Select(x => $"{x.Code}: {x.Message}").ToList());
    }
}
