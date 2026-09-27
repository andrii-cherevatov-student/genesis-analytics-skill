using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.Application;

public sealed record AnalysisRequest(
    string Topic,
    IReadOnlyList<string> Languages,
    DateOnly From,
    DateOnly To,
    double PracticalTrendThreshold = 0.05,
    bool GeneratePdf = false,
    IReadOnlyCollection<YearMonth>? ExcludedMonths = null)
{
    public IReadOnlyDictionary<string, string> ArticleOverrides { get; init; } = new Dictionary<string, string>();

    public string SourceLanguage { get; init; } = "en";

    public string OutputDirectory { get; init; } = "wiki-interest-output";

    public bool AllowSearchFallback { get; init; }

    public string? AgentInterpretation { get; init; }

    public IReadOnlyCollection<YearMonth> Excluded => ExcludedMonths ?? Array.Empty<YearMonth>();

    public const int MaxLanguages = 10;
    public const int RecommendedMaxLanguages = 5;
}

public sealed record ReportingWindow(YearMonth First, YearMonth Last, IReadOnlyList<string> Notes)
{
    public int Months => Last.MonthsSince(First) + 1;

    public static YearMonth DataStart => YearMonth.From(Wikipedia.WikimediaPageviewsClient.FirstAvailableDate);

    public static YearMonth LastCompleteMonth(DateOnly today)
    {
        var settled = today.AddDays(-2);
        var month = YearMonth.From(settled);
        return settled == month.LastDay ? month : month.AddMonths(-1);
    }

    public YearMonth FetchStart(int historyMonths, int extraMonths)
    {
        var start = First.AddMonths(-(historyMonths + extraMonths));
        return start < DataStart ? DataStart : start;
    }

    public static ReportingWindow FromDates(DateOnly from, DateOnly to, DateOnly today)
    {
        if (to < from) throw new SkillException(ErrorCodes.InvalidDateRange, $"'to' ({to:yyyy-MM-dd}) is before 'from' ({from:yyyy-MM-dd}).");
        var notes = new List<string>();
        var first = YearMonth.From(from);
        if (from.Day != 1)
        {
            first = first.AddMonths(1);
            notes.Add($"{YearMonth.From(from)} is only partly inside the period and was skipped; analysis starts at {first}.");
        }

        if (first < DataStart)
        {
            notes.Add($"Wikimedia pageview data start in {DataStart}; the period was shortened accordingly.");
            first = DataStart;
        }

        var last = YearMonth.From(to);
        if (to != last.LastDay)
        {
            last = last.AddMonths(-1);
            notes.Add($"{YearMonth.From(to)} is only partly inside the period (or not yet complete) and was skipped; analysis ends at {last}.");
        }

        var lastComplete = LastCompleteMonth(today);
        if (last > lastComplete)
        {
            notes.Add($"Data for months after {lastComplete} are not complete yet; analysis ends at {lastComplete}.");
            last = lastComplete;
        }

        if (last < first || last.MonthsSince(first) + 1 < AnalysisSettings.MinimumReportingMonths)
            throw new SkillException(ErrorCodes.InvalidDateRange,
                $"The reporting period must contain at least {AnalysisSettings.MinimumReportingMonths} complete months with published data " +
                $"(got {Math.Max(0, last.MonthsSince(first) + 1)}: {first}..{last}).");
        return new ReportingWindow(first, last, notes);
    }
}
