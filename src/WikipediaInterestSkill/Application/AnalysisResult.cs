using System.Text.Json.Serialization;
using WikipediaInterestSkill.Anomalies;
using WikipediaInterestSkill.Seasonality;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries.Models;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.Application;

public sealed record AnalysisResult(
    string SchemaVersion,
    ToolInfo Tool,
    DateTimeOffset GeneratedAt,
    string Status,
    string Topic,
    RequestEcho Request,
    ReportingPeriod ReportingPeriod,
    MethodologySummary Methodology,
    IReadOnlyList<LanguageAnalysisResult> Results,
    IReadOnlyList<LanguageFailure> Failures,
    ComparisonResult? Comparison,
    IReadOnlyList<AnalysisWarning> Warnings,
    IReadOnlyList<string> Caveats,
    ArtifactPaths Artifacts,
    Timings Timings)
{
    public const string CurrentSchemaVersion = "1.0";
}

public sealed record ToolInfo(string Name, string Version);

public sealed record RequestEcho(
    string Topic,
    IReadOnlyList<string> Languages,
    DateOnly From,
    DateOnly To,
    double PracticalTrendThreshold,
    IReadOnlyList<YearMonth> ExcludedMonths,
    IReadOnlyDictionary<string, string> ArticleOverrides,
    string SourceLanguage,
    bool GeneratePdf,
    int HistoryMonths,
    int BootstrapIterations,
    int BlockLength,
    string RerunCommand);

public sealed record ReportingPeriod(
    DateOnly From,
    DateOnly To,
    YearMonth FirstMonth,
    YearMonth LastMonth,
    int Months,
    string? Note);

public sealed record MethodologySummary(
    string Transform,
    string Decomposition,
    string TrendEstimator,
    string ConfidenceInterval,
    string Classification,
    string SupportingTest,
    string Seasonality,
    string Anomalies,
    string Robustness,
    string DataSource);

public sealed record LanguageFailure(string Language, string ErrorCode, string Message);

[JsonConverter(typeof(JsonStringEnumConverter<WarningSeverity>))]
public enum WarningSeverity
{
    Info,
    Caution,
    Serious,
}

public sealed record AnalysisWarning(string Code, WarningSeverity Severity, string Message, string? Language = null);

public static class WarningCodes
{
    public const string PeriodAdjusted = "PeriodAdjusted";
    public const string ManyLanguages = "ManyLanguages";
    public const string ExclusionOutsidePeriod = "ExclusionOutsidePeriod";
    public const string LanguageFailed = "LanguageFailed";
    public const string DataStartsLate = "DataStartsLate";
    public const string UncertainArticleMapping = "UncertainArticleMapping";
    public const string PdfMissingGlyphs = "PdfMissingGlyphs";
    public const string PdfGenerationFailed = "PdfGenerationFailed";
}

public sealed record ArtifactPaths(
    string AnalysisJson,
    string PageviewsCsv,
    string TrendChart,
    string? RawViewsChart,
    string? Pdf);

public sealed record Timings(double TotalSeconds, double ResolutionSeconds, double FetchSeconds, double AnalysisSeconds,
    double ArtifactSeconds, int CacheHits, int CacheMisses);

public sealed record LanguageAnalysisResult(
    string Language,
    ResolvedArticle Article,
    string Summary,
    VolumeMetrics Volume,
    TrendAnalysis Trend,
    SeasonalityResult Seasonality,
    IReadOnlyList<Anomaly> Anomalies,
    RobustnessResult Robustness,
    DataQuality DataQuality,
    IReadOnlyList<AnalysisWarning> Warnings);

public sealed record VolumeMetrics(
    long TotalViews,
    int Months,
    double AverageMonthlyViews,
    double AverageDailyViews,
    long? Last12MonthsViews,
    long? Previous12MonthsViews,
    double? Last12VsPrevious12Change,
    YearMonth PeakMonth,
    long PeakMonthViews,
    double DayCoverage);

public sealed record TrendAnalysis(
    TrendClassification Classification,
    double AnnualizedGrowth,
    ConfidenceInterval ConfidenceInterval95,
    double MonthlyLogSlope,
    double PracticalThreshold,
    IReadOnlyList<string> CompatibleWith,
    MannKendallResult MannKendall,
    BootstrapInfo Bootstrap,
    string Interpretation);

public sealed record BootstrapInfo(
    string Method,
    int Iterations,
    int BlockLength,
    double SlopeStandardError,
    double ResidualLag1Autocorrelation,
    double DependenceScaleFactor,
    double SmallSampleScaleFactor);

public sealed record DataQuality(
    YearMonth DecompositionStart,
    YearMonth DecompositionEnd,
    int MonthsInDecomposition,
    int HistoryMonthsBeforePeriod,
    IReadOnlyList<YearMonth> ScaledMonths,
    IReadOnlyList<YearMonth> MissingMonths,
    IReadOnlyList<YearMonth> ExcludedMonths,
    double DayCoverage);

public sealed record RobustnessResult(
    string Statement,
    IReadOnlyList<RobustnessConcern> Concerns,
    double ConfidenceIntervalWidth,
    LeaveOneOutResult LeaveOneOut,
    WindowSensitivityResult WindowSensitivity,
    AlternativeEstimatorsResult AlternativeEstimators,
    ResidualDiagnostics ResidualDiagnostics,
    HistorySufficiency History);

public sealed record RobustnessConcern(string Code, WarningSeverity Severity, string Message);

public static class RobustnessConcernExtensions
{
    public static IEnumerable<RobustnessConcern> Material(this IEnumerable<RobustnessConcern> concerns) =>
        concerns.Where(c => c.Severity != WarningSeverity.Info);
}

public sealed record LeaveOneOutFit(YearMonth Month, double AnnualizedGrowth, double Shift, TrendClassification Classification);

public sealed record LeaveOneOutResult(
    int Fits,
    double MaxAbsoluteShift,
    YearMonth? MostInfluentialMonth,
    double ClassificationAgreement,
    double DirectionAgreement,
    string Method,
    IReadOnlyList<LeaveOneOutFit> PerMonth);

public sealed record WindowFit(
    string Label,
    YearMonth FirstMonth,
    YearMonth LastMonth,
    double AnnualizedGrowth,
    ConfidenceInterval ConfidenceInterval95,
    TrendClassification Classification);

public sealed record WindowSensitivityResult(
    IReadOnlyList<WindowFit> Windows,
    double DirectionAgreement,
    double ClassificationAgreement,
    double MinAnnualizedGrowth,
    double MaxAnnualizedGrowth,
    int BootstrapIterationsPerWindow);

public sealed record AlternativeEstimate(
    string Name,
    string Description,
    double? AnnualizedGrowth,
    bool? AgreesInDirection,
    bool? WithinPrimaryInterval);

public sealed record AlternativeEstimatorsResult(IReadOnlyList<AlternativeEstimate> Estimators, double DirectionAgreement);

public sealed record ResidualDiagnostics(
    double Lag1Autocorrelation,
    IReadOnlyList<double> Acf,
    double LjungBoxPValue,
    int LjungBoxLags,
    bool MaterialAutocorrelation,
    string Interpretation);

public sealed record HistorySufficiency(int ReportingMonths, int MonthsInDecomposition, double SeasonalCycles, bool Sufficient,
    string Note);

public sealed record RankedLanguage(
    int Rank,
    string Language,
    TrendClassification Classification,
    double AnnualizedGrowth,
    double CiLower,
    double CiUpper,
    long? Last12MonthsViews,
    string SeasonalityCategory);

public sealed record PairwiseDifference(
    string LanguageA,
    string LanguageB,
    double GrowthDifference,
    ConfidenceInterval ConfidenceInterval95,
    bool Distinguishable);

public sealed record ComparisonResult(
    IReadOnlyList<RankedLanguage> RankingByEvidenceOfGrowth,
    IReadOnlyList<PairwiseDifference> PairwiseDifferences,
    string? StrongestEvidenceOfGrowth,
    string Statement,
    string RankingRule,
    string NormalizedIndexBase);
