using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WikipediaInterestSkill;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Infrastructure;
using WikipediaInterestSkill.Reporting;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries.Models;
using WikipediaInterestSkill.Validation;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var root = new RootCommand(
    "wiki-interest — deterministic Wikipedia pageview interest analysis (trend, uncertainty, seasonality, anomalies, robustness).");
root.Subcommands.Add(Cli.AnalyzeCommand());
root.Subcommands.Add(Cli.SimulateCommand());
root.Subcommands.Add(Cli.ValidateCommand());
return await root.Parse(args).InvokeAsync();

internal static class Cli
{
    public static Command AnalyzeCommand()
    {
        var topic = new Option<string>("--topic", "-t")
            { Description = "Topic in the source language (default English), e.g. \"intermittent fasting\"." };
        var languages = new Option<string[]>("--languages", "-l")
        {
            Description = "Wikipedia language codes, e.g. pl cs uk (space or comma separated).",
            AllowMultipleArgumentsPerToken = true,
            Required = true,
        };
        var from = new Option<string?>("--from") { Description = "Start date yyyy-MM-dd (or yyyy-MM). Only complete months are analysed." };
        var to = new Option<string?>("--to") { Description = "End date yyyy-MM-dd (or yyyy-MM). Only complete months are analysed." };
        var lastMonths = new Option<int?>("--last-months")
            { Description = "Alternative to --from/--to: the N most recent complete months (e.g. 24)." };
        var output = new Option<string>("--output", "-o")
            { Description = "Output directory for analysis.json, pageviews.csv, trend.png, report.pdf.", DefaultValueFactory = _ => "wiki-interest-output" };
        var report = new Option<string>("--report") { Description = "Report type: none (default) or pdf.", DefaultValueFactory = _ => "none" };
        report.AcceptOnlyFromAmong("none", "pdf");
        var threshold = new Option<double>("--trend-threshold")
            { Description = "Practical significance threshold for annualized growth (default 0.05 = ±5%/yr).", DefaultValueFactory = _ => 0.05 };
        var exclude = new Option<string[]>("--exclude")
            { Description = "Month(s) to exclude from the trend estimate for sensitivity analysis, yyyy-MM.", AllowMultipleArgumentsPerToken = true };
        var article = new Option<string[]>("--article")
        {
            Description = "Explicit article per language: lang=\"Title\" or lang=https://xx.wikipedia.org/wiki/Title. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
        };
        var allowFallback = new Option<bool>("--allow-search-fallback")
        {
            Description = "If a language has no interlanguage link, analyse its best search hit (low confidence) instead of failing with candidates.",
        };
        var sourceLanguage = new Option<string>("--source-language")
            { Description = "Language the topic is written in (default en).", DefaultValueFactory = _ => "en" };
        var notes = new Option<string?>("--notes") { Description = "Short interpretation written by the agent, included (labelled) in the PDF." };
        var notesFile = new Option<string?>("--notes-file") { Description = "File containing the agent interpretation for the PDF." };
        var history = new Option<int>("--history-months")
            { Description = "Advanced: months before the period used for seasonal decomposition (default 24).", DefaultValueFactory = _ => 24 };
        var bootstrap = new Option<int>("--bootstrap")
            { Description = "Advanced: bootstrap replicates (default 2000).", DefaultValueFactory = _ => 2000 };
        var cacheDir = new Option<string?>("--cache-dir") { Description = "Advanced: cache directory (default <skill>/.cache)." };
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Write structured progress logs to stderr." };

        var cmd = new Command("analyze", "Analyze interest in one topic across 1–5 Wikipedia language editions.")
        {
            topic, languages, from, to, lastMonths, output, report, threshold, exclude, article, allowFallback, sourceLanguage, notes,
            notesFile, history, bootstrap, cacheDir, verbose,
        };

        cmd.SetAction(async (parse, ct) =>
        {
            var outputDir = parse.GetValue(output)!;
            try
            {
                var (fromDate, toDate) = ResolveDates(parse.GetValue(from), parse.GetValue(to), parse.GetValue(lastMonths));
                var excluded = (parse.GetValue(exclude) ?? Array.Empty<string>())
                    .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Select(s => YearMonth.TryParse(s, out var m)
                        ? m
                        : throw new SkillException(ErrorCodes.InvalidArgument, $"--exclude '{s}' is not a month (expected yyyy-MM)."))
                    .Distinct().ToList();
                var overrides = ParseArticles(parse.GetValue(article));
                var interpretation = parse.GetValue(notes);
                if (parse.GetValue(notesFile) is { } nf) interpretation = await File.ReadAllTextAsync(nf, ct);

                var request = new AnalysisRequest(
                    parse.GetValue(topic) ?? string.Empty,
                    parse.GetValue(languages) ?? Array.Empty<string>(),
                    fromDate,
                    toDate,
                    parse.GetValue(threshold),
                    parse.GetValue(report) == "pdf",
                    excluded)
                {
                    ArticleOverrides = overrides,
                    SourceLanguage = parse.GetValue(sourceLanguage)!.ToLowerInvariant(),
                    OutputDirectory = outputDir,
                    AgentInterpretation = interpretation,
                    AllowSearchFallback = parse.GetValue(allowFallback),
                };
                var settings = new AnalysisSettings
                {
                    HistoryMonths = Math.Clamp(parse.GetValue(history), 0, 120),
                    BootstrapIterations = Math.Clamp(parse.GetValue(bootstrap), 200, 20000),
                };

                using var loggerFactory = CreateLoggers(parse.GetValue(verbose), Path.Combine(TryFullPath(outputDir), "run.log.jsonl"));
                using var services = new SkillServices(loggerFactory, SkillServices.ResolveCacheDirectory(parse.GetValue(cacheDir)), settings);
                var result = await services.Pipeline.RunAsync(request, ct);
                Console.WriteLine(ConsoleSummary.Render(result));
                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(ex, outputDir);
            }
        });
        return cmd;
    }

    internal static (DateOnly From, DateOnly To) ResolveDates(string? from, string? to, int? lastMonths)
    {
        if (lastMonths is { } n)
        {
            if (from is not null || to is not null)
                throw new SkillException(ErrorCodes.InvalidArgument, "Use either --last-months or --from/--to, not both.");
            if (n < AnalysisSettings.MinimumReportingMonths)
                throw new SkillException(ErrorCodes.InvalidDateRange, $"--last-months must be at least {AnalysisSettings.MinimumReportingMonths}.");
            var last = ReportingWindow.LastCompleteMonth(DateOnly.FromDateTime(DateTime.UtcNow));
            return (last.AddMonths(-(n - 1)).FirstDay, last.LastDay);
        }

        if (from is null || to is null)
            throw new SkillException(ErrorCodes.InvalidDateRange, "Provide --from and --to (yyyy-MM-dd), or --last-months N.");
        return (ParseDate(from, isEnd: false), ParseDate(to, isEnd: true));
    }

    private static DateOnly ParseDate(string text, bool isEnd)
    {
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (YearMonth.TryParse(text, out var m)) return isEnd ? m.LastDay : m.FirstDay;
        throw new SkillException(ErrorCodes.InvalidDateRange, $"'{text}' is not a valid date (expected yyyy-MM-dd).");
    }

    internal static IReadOnlyDictionary<string, string> ParseArticles(string[]? specs)
    {
        var map = new Dictionary<string, string>();
        foreach (var spec in specs ?? Array.Empty<string>())
        {
            var eq = spec.IndexOf('=');
            if (eq <= 0 || eq == spec.Length - 1)
                throw new SkillException(ErrorCodes.InvalidArgument, $"--article '{spec}' must look like pl=\"Title\" or pl=https://pl.wikipedia.org/wiki/Title.");
            var lang = spec[..eq].Trim().ToLowerInvariant();
            var value = spec[(eq + 1)..].Trim().Trim('"', '\'');
            map[lang] = value;
        }

        return map;
    }

    private static int Fail(Exception ex, string? outputDir)
    {
        var (code, message) = SkillException.Describe(ex);
        if (ex is not SkillException) Console.Error.WriteLine(ex);
        Console.WriteLine($"ERROR ({code}): {message}");
        Console.Error.WriteLine($"wiki-interest failed: {code}");
        if (outputDir is not null)
        {
            try
            {
                var full = Path.GetFullPath(outputDir);
                if (Directory.Exists(full))
                    File.WriteAllText(Path.Combine(full, "error.json"),
                        JsonSerializer.Serialize(new { status = "error", errorCode = code, message }, JsonOptions.Default));
            }
            catch (Exception)
            {
            }
        }

        return 1;
    }

    private static string TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return Path.Combine(Path.GetTempPath(), "wiki-interest");
        }
    }

    private static ILoggerFactory CreateLoggers(bool verbose, string? logFile)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            if (verbose)
                builder.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss.fff ";
                }).AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(l => l >= LogLevel.Information);
            else
                builder.AddSimpleConsole(o => o.SingleLine = true)
                    .AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(l => l >= LogLevel.Error);
            builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            if (logFile is not null)
            {
                try
                {
                    builder.AddProvider(new JsonLinesLoggerProvider(logFile));
                }
                catch (Exception)
                {
                }
            }
        });
    }

    public static Command SimulateCommand()
    {
        var reps = new Option<int>("--replications", "-n")
            { Description = "Monte Carlo replications per scenario.", DefaultValueFactory = _ => 200 };
        var grid = new Option<string>("--grid") { Description = "quick (5 scenarios) or full (192 scenarios).", DefaultValueFactory = _ => "quick" };
        grid.AcceptOnlyFromAmong("quick", "full");
        var bootstrap = new Option<int>("--bootstrap") { Description = "Bootstrap replicates per fit.", DefaultValueFactory = _ => 2000 };
        var block = new Option<int>("--block-length") { Description = "Moving-block length.", DefaultValueFactory = _ => AnalysisSettings.DefaultBlockLength };
        var seed = new Option<int>("--seed") { DefaultValueFactory = _ => 12345 };
        var output = new Option<string>("--output", "-o")
            { Description = "Output JSON path.", DefaultValueFactory = _ => Path.Combine("validation", "simulation-report.json") };

        var cmd = new Command("simulate", "Monte Carlo validation on synthetic series with known ground truth (development diagnostic).")
        {
            reps, grid, bootstrap, block, seed, output,
        };
        cmd.SetAction(parse =>
        {
            var settings = new AnalysisSettings
            {
                BootstrapIterations = parse.GetValue(bootstrap),
                BlockLength = parse.GetValue(block),
            };
            var scenarios = parse.GetValue(grid) == "full" ? MonteCarloSimulator.DefaultGrid() : MonteCarloSimulator.QuickGrid();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var report = MonteCarloSimulator.Run(scenarios, parse.GetValue(reps), settings, parse.GetValue(seed),
                progress: new Progress<(int Done, int Total)>(p => Console.Error.Write($"\rscenario {p.Done}/{p.Total}")));
            Console.Error.WriteLine();
            var path = WriteReport(parse.GetValue(output)!, report, SimulationMarkdown.Render(report));
            Console.WriteLine(SimulationMarkdown.RenderAcceptance(report));
            Console.WriteLine($"Elapsed {sw.Elapsed.TotalSeconds:0.0}s. Report: {path}");
            return report.Acceptance.All(a => a.Passed) ? 0 : 3;
        });
        return cmd;
    }

    public static Command ValidateCommand()
    {
        var cases = new Option<string>("--cases")
            { Description = "Real-data cases JSON.", DefaultValueFactory = _ => DefaultCasesPath() };
        var output = new Option<string>("--output", "-o")
            { Description = "Output JSON path.", DefaultValueFactory = _ => Path.Combine("validation", "real-data-report.json") };
        var cacheDir = new Option<string?>("--cache-dir");
        var verbose = new Option<bool>("--verbose", "-v");
        var cmd = new Command("validate", "Real-data robustness validation on a curated set of Wikipedia series (development diagnostic).")
        {
            cases, output, cacheDir, verbose,
        };
        cmd.SetAction(async (parse, ct) =>
        {
            try
            {
                var settings = new AnalysisSettings();
                using var loggerFactory = CreateLoggers(parse.GetValue(verbose), null);
                using var services = new SkillServices(loggerFactory, SkillServices.ResolveCacheDirectory(parse.GetValue(cacheDir)), settings);
                var list = RealDataValidator.LoadCases(parse.GetValue(cases)!);
                var report = await RealDataValidator.RunAsync(list, services.Resolver, services.Source, settings,
                    DateOnly.FromDateTime(DateTime.UtcNow), ct);
                var markdown = RealDataMarkdown.Render(report);
                WriteReport(parse.GetValue(output)!, report, markdown);
                Console.WriteLine(markdown);
                return report.Cases.Any(c => c.Error is not null) ? 3 : 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(ex, null);
            }
        });
        return cmd;
    }

    private static string WriteReport<T>(string output, T report, string markdown)
    {
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions.Default));
        File.WriteAllText(Path.ChangeExtension(path, ".md"), markdown);
        return path;
    }

    private static string DefaultCasesPath()
    {
        var root = SkillServices.FindSkillRoot();
        return root is null
            ? Path.Combine("validation", "real-data-cases.json")
            : Path.Combine(root, "validation", "real-data-cases.json");
    }
}
