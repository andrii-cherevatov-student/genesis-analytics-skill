using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WikipediaInterestSkill.Caching;
using WikipediaInterestSkill.Reporting;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries;
using WikipediaInterestSkill.TimeSeries.Models;
using WikipediaInterestSkill.Wikipedia;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.Application;

public interface IAnalysisPipeline
{
    Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken cancellationToken);
}

public sealed class AnalysisPipeline(
    IWikipediaArticleResolver resolver,
    CachedPageviewSource pageviews,
    PageviewCache? cache,
    AnalysisSettings settings,
    ILoggerFactory loggerFactory,
    Func<DateOnly>? today = null) : IAnalysisPipeline
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AnalysisPipeline>();
    private readonly Func<DateOnly> _today = today ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));

    public async Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var languages = ValidateRequest(request);
        var runSettings = settings with { PracticalThreshold = request.PracticalTrendThreshold };
        var window = ReportingWindow.FromDates(request.From, request.To, _today());
        var outputDir = PrepareOutputDirectory(request.OutputDirectory);
        var warnings = new List<AnalysisWarning>();
        warnings.AddRange(window.Notes.Select(n => new AnalysisWarning(WarningCodes.PeriodAdjusted, WarningSeverity.Info, n)));
        if (languages.Count > AnalysisRequest.RecommendedMaxLanguages)
            warnings.Add(new AnalysisWarning(WarningCodes.ManyLanguages, WarningSeverity.Info,
                $"{languages.Count} languages requested; the skill is tuned for up to {AnalysisRequest.RecommendedMaxLanguages}."));
        foreach (var m in request.Excluded.Where(m => m < window.First || m > window.Last))
            warnings.Add(new AnalysisWarning(WarningCodes.ExclusionOutsidePeriod, WarningSeverity.Info,
                $"Excluded month {m} is outside the reporting period {window.First}..{window.Last}."));

        _logger.LogInformation("Analysis started: topic {Topic}, languages {Languages}, window {First}..{Last}",
            request.Topic, string.Join(",", languages), window.First, window.Last);

        var failures = new ConcurrentDictionary<string, LanguageFailure>();
        var analyses = new ConcurrentDictionary<string, LanguageAnalysis>();
        var stageSeconds = new ConcurrentDictionary<string, double>();
        var fetchStart = window.FetchStart(runSettings.HistoryMonths, 3);
        using var cpu = new SemaphoreSlim(1);

        await Task.WhenAll(languages.Select(lang => Guard(lang, failures, async () =>
        {
            var sw = Stopwatch.StartNew();
            request.ArticleOverrides.TryGetValue(lang, out var over);
            var article = await resolver.ResolveAsync(request.Topic, lang, request.SourceLanguage, over, cancellationToken,
                request.AllowSearchFallback).ConfigureAwait(false);
            AddMax(stageSeconds, "resolve", sw.Elapsed.TotalSeconds);

            sw.Restart();
            var daily = await pageviews.GetAsync(article, fetchStart.FirstDay, window.Last.LastDay, cancellationToken)
                .ConfigureAwait(false);
            if (daily.Count == 0)
                throw new SkillException(ErrorCodes.InsufficientData,
                    $"Wikimedia returned no pageview data for the resolved {lang} article in the requested period.");
            var monthly = MonthlyAggregator.Aggregate(daily, fetchStart, window.Last);
            AddMax(stageSeconds, "fetch", sw.Elapsed.TotalSeconds);

            await cpu.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                sw.Restart();
                analyses[lang] = await Task.Run(() => SeriesAnalyzer.Analyze(article, monthly, window.First, window.Last,
                    request.Excluded, runSettings, _logger), cancellationToken).ConfigureAwait(false);
                var seconds = sw.Elapsed.TotalSeconds;
                stageSeconds.AddOrUpdate("analysis", seconds, (_, v) => v + seconds);
                _logger.LogInformation("Analysed {Language} in {ElapsedMs} ms", lang, sw.ElapsedMilliseconds);
            }
            finally
            {
                cpu.Release();
            }
        }))).ConfigureAwait(false);

        var ordered = languages.Where(analyses.ContainsKey).Select(l => analyses[l]).ToList();
        var failureList = languages.Where(failures.ContainsKey).Select(l => failures[l]).ToList();
        foreach (var f in failureList)
            warnings.Add(new AnalysisWarning(WarningCodes.LanguageFailed, WarningSeverity.Serious,
                $"{f.Language} could not be analysed ({f.ErrorCode}): {f.Message}", f.Language));

        if (ordered.Count == 0)
            throw new SkillException(failureList.Count == 1 ? failureList[0].ErrorCode : ErrorCodes.AllLanguagesFailed,
                "No language could be analysed: " + string.Join(" | ", failureList.Select(f => $"{f.Language}: {f.Message}")));

        var comparison = ordered.Count > 1 ? ComparisonBuilder.Build(ordered, runSettings.PracticalThreshold) : null;

        var artifactClock = Stopwatch.StartNew();
        var result = new AnalysisResult(
            AnalysisResult.CurrentSchemaVersion,
            new ToolInfo("wiki-interest", ToolVersion),
            DateTimeOffset.UtcNow,
            failureList.Count == 0 ? "ok" : "partial",
            request.Topic,
            Echo(request, languages, runSettings),
            new ReportingPeriod(request.From, request.To, window.First, window.Last, window.Months,
                window.Notes.Count > 0 ? string.Join(" ", window.Notes) : null),
            Methodology(runSettings),
            ordered.Select(a => a.Result).ToList(),
            failureList,
            comparison,
            warnings,
            Caveats(ordered, runSettings),
            ArtifactPlan(outputDir, request.GeneratePdf),
            new Timings(0, 0, 0, 0, 0, 0, 0));

        var trendChart = Task.Run(() => ChartGenerator.TrendChart(request.Topic, ordered), cancellationToken);
        var rawChart = ordered.Count == 1
            ? trendChart
            : Task.Run(() => ChartGenerator.RawChart(request.Topic, ordered), cancellationToken);
        var pdfEngine = request.GeneratePdf ? Task.Run(PdfReportGenerator.Initialize, cancellationToken) : Task.CompletedTask;
        CsvWriter.Write(result.Artifacts.PageviewsCsv, ordered);

        var trendPng = await trendChart.ConfigureAwait(false);
        await File.WriteAllBytesAsync(result.Artifacts.TrendChart, trendPng, cancellationToken).ConfigureAwait(false);
        if (request.GeneratePdf)
        {
            await pdfEngine.ConfigureAwait(false);
            result = WritePdf(result, trendPng, request.AgentInterpretation, warnings);
        }

        await File.WriteAllBytesAsync(result.Artifacts.RawViewsChart!, await rawChart.ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Artifacts written in {ElapsedMs} ms", artifactClock.ElapsedMilliseconds);

        result = result with
        {
            Timings = new Timings(Math.Round(total.Elapsed.TotalSeconds, 3),
                Math.Round(stageSeconds.GetValueOrDefault("resolve"), 3), Math.Round(stageSeconds.GetValueOrDefault("fetch"), 3),
                Math.Round(stageSeconds.GetValueOrDefault("analysis"), 3), Math.Round(artifactClock.Elapsed.TotalSeconds, 3),
                cache?.Hits ?? 0, cache?.Misses ?? 0),
        };
        JsonResultWriter.Write(result.Artifacts.AnalysisJson, result);
        _logger.LogInformation("Analysis finished in {Seconds:0.00} s ({Ok} ok, {Failed} failed)",
            total.Elapsed.TotalSeconds, ordered.Count, failureList.Count);
        return result;
    }

    private async Task Guard(string language, ConcurrentDictionary<string, LanguageFailure> failures, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (SkillException ex)
        {
            _logger.LogWarning("{Language} failed: {Code} {Message}", language, ex.Code, ex.Message);
            failures[language] = new LanguageFailure(language, ex.Code, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            failures[language] = new LanguageFailure(language, ErrorCodes.WikimediaApiFailure, $"Invalid source data: {ex.Message}");
        }
    }

    private static void AddMax(ConcurrentDictionary<string, double> stages, string stage, double seconds) =>
        stages.AddOrUpdate(stage, seconds, (_, v) => Math.Max(v, seconds));

    private AnalysisResult WritePdf(AnalysisResult result, byte[] trendPng, string? interpretation, List<AnalysisWarning> warnings)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var pdf = PdfReportGenerator.Write(result.Artifacts.Pdf!, result, trendPng, interpretation);
            if (pdf.MissingGlyphs)
                warnings.Add(new AnalysisWarning(WarningCodes.PdfMissingGlyphs, WarningSeverity.Caution,
                    "report.pdf was generated, but some characters (a writing system without a bundled font) are shown blank. " +
                    $"analysis.json and the charts are unaffected. {pdf.Detail}"));
            _logger.LogInformation("PDF report written in {ElapsedMs} ms", clock.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "PDF generation failed");
            warnings.Add(new AnalysisWarning(WarningCodes.PdfGenerationFailed, WarningSeverity.Serious,
                $"report.pdf could not be generated ({ex.GetType().Name}: {ex.Message.FirstLine()}). All other outputs are valid."));
            return result with { Artifacts = result.Artifacts with { Pdf = null } };
        }
    }

    public static string ToolVersion => typeof(AnalysisPipeline).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    internal static IReadOnlyList<string> ValidateRequest(AnalysisRequest request)
    {
        var languages = request.Languages
            .SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(l => l.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (languages.Count == 0)
            throw new SkillException(ErrorCodes.InvalidArgument, "At least one language code is required (e.g. --languages pl cs).");
        if (languages.Count > AnalysisRequest.MaxLanguages)
            throw new SkillException(ErrorCodes.InvalidArgument, $"At most {AnalysisRequest.MaxLanguages} languages per analysis.");
        foreach (var l in languages.Where(l => !WikipediaArticleResolver.IsValidLanguageCode(l)))
            throw new SkillException(ErrorCodes.UnknownLanguage, $"'{l}' is not a valid Wikipedia language code.");
        if (string.IsNullOrWhiteSpace(request.Topic) && languages.Any(l => !request.ArticleOverrides.ContainsKey(l)))
            throw new SkillException(ErrorCodes.InvalidArgument, "A --topic is required unless --article is given for every language.");
        if (request.PracticalTrendThreshold is < 0 or > 1 || double.IsNaN(request.PracticalTrendThreshold))
            throw new SkillException(ErrorCodes.InvalidArgument, "--trend-threshold must be between 0 and 1 (e.g. 0.05 for ±5%/yr).");
        foreach (var key in request.ArticleOverrides.Keys.Where(k => !languages.Contains(k)))
            throw new SkillException(ErrorCodes.InvalidArgument, $"--article given for '{key}', which is not in --languages.");
        return languages;
    }

    internal static string PrepareOutputDirectory(string path)
    {
        if (File.Exists(path))
            throw new SkillException(ErrorCodes.InvalidOutputDirectory, $"Output path '{Path.GetFullPath(path)}' is a file, not a directory.");
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SkillException(ErrorCodes.InvalidOutputDirectory, $"Cannot write to output directory '{path}': {ex.Message}", ex);
        }
    }

    private static ArtifactPaths ArtifactPlan(string dir, bool pdf) => new(
        Path.Combine(dir, "analysis.json"),
        Path.Combine(dir, "pageviews.csv"),
        Path.Combine(dir, "trend.png"),
        Path.Combine(dir, "trend_raw.png"),
        pdf ? Path.Combine(dir, "report.pdf") : null);

    private static RequestEcho Echo(AnalysisRequest r, IReadOnlyList<string> languages, AnalysisSettings s)
    {
        var parts = new List<string>
        {
            "wiki-interest analyze",
            $"--topic {Quote(r.Topic)}",
            $"--languages {string.Join(' ', languages)}",
            $"--from {r.From:yyyy-MM-dd}",
            $"--to {r.To:yyyy-MM-dd}",
            $"--output {Quote(r.OutputDirectory)}",
        };
        if (Math.Abs(r.PracticalTrendThreshold - 0.05) > 1e-12)
            parts.Add($"--trend-threshold {r.PracticalTrendThreshold.ToString(CultureInfo.InvariantCulture)}");
        if (r.SourceLanguage != "en") parts.Add($"--source-language {r.SourceLanguage}");
        parts.AddRange(r.Excluded.Order().Select(m => $"--exclude {m}"));
        parts.AddRange(r.ArticleOverrides.OrderBy(kv => kv.Key).Select(kv => $"--article {kv.Key}={Quote(kv.Value)}"));
        if (r.AllowSearchFallback) parts.Add("--allow-search-fallback");
        if (r.GeneratePdf) parts.Add("--report pdf");
        return new RequestEcho(r.Topic, languages, r.From, r.To, r.PracticalTrendThreshold, r.Excluded.Order().ToList(),
            r.ArticleOverrides, r.SourceLanguage, r.GeneratePdf, s.HistoryMonths, s.BootstrapIterations, s.BlockLength,
            string.Join(' ', parts));
    }

    private static string Quote(string s) => s.Any(char.IsWhiteSpace) || s.Length == 0 ? $"\"{s.Replace("\"", "\\\"")}\"" : s;

    internal static MethodologySummary Methodology(AnalysisSettings s) => new(
        "Monthly totals of daily user pageviews (agent=user); Y = log(1 + views). Partially reported months are scaled; months with < 50% of days reported are imputed for the decomposition only.",
        $"Robust STL (period 12, seasonal window {s.Stl.SeasonalWindow}, trend window {s.Stl.ResolvedTrendWindow}) on the reporting period plus up to {s.HistoryMonths} earlier months: Y = Trend + Seasonal + Remainder.",
        "Theil-Sen slope of the STL trend over the reporting period; annualized growth g = exp(12·slope) − 1.",
        $"95% residual moving-block bootstrap (block {s.BlockLength} months, {s.BootstrapIterations} replicates, full STL re-decomposition per replicate), basic interval, with AR(1) long-run-variance and small-sample (Student-t) corrections validated by Monte Carlo simulation.",
        $"CI lower > +{s.PracticalThreshold:0.###} → Increasing; CI upper < −{s.PracticalThreshold:0.###} → Decreasing; CI within ±{s.PracticalThreshold:0.###} → Stable; otherwise Inconclusive. Statistical significance alone never decides the class.",
        "Mann-Kendall test on seasonally adjusted values (Hamed-Rao variance correction when lag-1 autocorrelation is material); supporting evidence only.",
        "Strength F_S = max(0, 1 − Var(R)/Var(S+R)); calendar-month uplift exp(S_m) − 1 relative to the yearly baseline.",
        $"Robust z-score of the STL remainder (median/MAD); |z| > {s.AnomalyThreshold:0.#} flagged. Anomalies are reported, never removed.",
        "Leave-one-out refits, ±3-month window shifts, alternative estimators (OLS on deseasonalized logs, YoY aggregate, median monthly YoY), residual autocorrelation (Ljung-Box).",
        "Wikimedia REST API pageviews per article (https://wikimedia.org/api/rest_v1/), cached locally.");

    private static IReadOnlyList<string> Caveats(IReadOnlyList<LanguageAnalysis> analyses, AnalysisSettings s)
    {
        var list = new List<string>
        {
            "Wikipedia pageviews measure informational attention to specific articles. They are not a measure of market size, willingness to pay or product demand.",
            "Only views of the resolved article title are counted; views of redirects, related articles and other languages are not included.",
            "Absolute volumes depend on the size and reach of each language edition; compare growth rates rather than raw traffic across languages.",
            "An Inconclusive classification means the data cannot distinguish growth, decline and stability. It is not evidence that interest is stable.",
            $"'Stable' means the whole 95% interval lies within ±{Format.Threshold(s.PracticalThreshold)}/yr; monthly traffic can still vary strongly with the seasons.",
        };
        if (analyses.Any(a => a.Fit.Classification == TrendClassification.Stable &&
                              a.Result.Seasonality.Category is Seasonality.SeasonalityCategory.Moderate or Seasonality.SeasonalityCategory.Strong))
            list.Add("At least one series has a stable long-term trend but pronounced seasonality: interest does vary within the year even though it is not growing or shrinking.");
        if (analyses.Any(a => a.Result.Anomalies.Count > 0))
            list.Add("Anomalous months (e.g. news events) are kept in the data; the robust decomposition limits their influence on the trend. Use --exclude to test their impact explicitly.");
        return list;
    }
}
