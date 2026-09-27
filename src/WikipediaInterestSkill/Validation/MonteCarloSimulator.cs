using System.Collections.Concurrent;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Seasonality;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries;
using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.Validation;

public sealed record ScenarioMetrics(
    SyntheticScenario Scenario,
    int Replications,
    string TrueClassification,
    double GrowthMae,
    double GrowthBias,
    double CiCoverage,
    double MeanCiWidth,
    double ClassificationAccuracy,
    double DirectionalFalsePositiveRate,
    double WrongDirectionRate,
    double InconclusiveRate,
    double PointDirectionAccuracy,
    double ClassDirectionAccuracy,
    double SeasonalityMae,
    IReadOnlyDictionary<string, int> ClassCounts);

public sealed record AcceptanceCheck(string Metric, double Value, string Target, bool Passed, string Scope);

public sealed record SimulationReport(
    DateTimeOffset GeneratedAt,
    int ReplicationsPerScenario,
    int BootstrapIterations,
    int BlockLength,
    double PracticalThreshold,
    int Seed,
    IReadOnlyList<ScenarioMetrics> Scenarios,
    IReadOnlyList<AcceptanceCheck> Acceptance);

public static class MonteCarloSimulator
{
    public static IReadOnlyList<SyntheticScenario> DefaultGrid()
    {
        var list = new List<SyntheticScenario>();
        foreach (var g in new[] { -0.2, -0.1, 0.0, 0.1, 0.2, 0.4 })
        foreach (var noise in new[] { 0.05, 0.15 })
        foreach (var phi in new[] { 0.0, 0.5 })
        foreach (var seas in new[] { 0.0, 0.3 })
        foreach (var spikes in new[] { 0, 2 })
        foreach (var months in new[] { 24, 36 })
            list.Add(new SyntheticScenario
            {
                Name = $"g={g:+0%;-0%;0%} sd={noise} phi={phi} seas={seas} spikes={spikes} n={months}",
                AnnualGrowth = g,
                NoiseSd = noise,
                ArPhi = phi,
                SeasonalAmplitude = seas,
                Spikes = spikes,
                ReportingMonths = months,
            });
        return list;
    }

    public static IReadOnlyList<SyntheticScenario> QuickGrid() => new[]
    {
        new SyntheticScenario { Name = "flat, seasonal, AR(0.5)", AnnualGrowth = 0, NoiseSd = 0.1, ArPhi = 0.5, SeasonalAmplitude = 0.3 },
        new SyntheticScenario { Name = "flat, white noise", AnnualGrowth = 0, NoiseSd = 0.1 },
        new SyntheticScenario { Name = "+30%, seasonal, spikes", AnnualGrowth = 0.3, NoiseSd = 0.1, ArPhi = 0.3, SeasonalAmplitude = 0.3, Spikes = 2 },
        new SyntheticScenario { Name = "-25%, AR(0.5)", AnnualGrowth = -0.25, NoiseSd = 0.1, ArPhi = 0.5 },
        new SyntheticScenario { Name = "+10%, 36 months", AnnualGrowth = 0.1, NoiseSd = 0.08, ArPhi = 0.3, SeasonalAmplitude = 0.2, ReportingMonths = 36 },
    };

    public static SimulationReport Run(
        IReadOnlyList<SyntheticScenario> scenarios,
        int replications,
        AnalysisSettings settings,
        int seed = 12345,
        IProgress<(int Done, int Total)>? progress = null)
    {
        var results = new ScenarioMetrics[scenarios.Count];
        var done = 0;
        var inner = settings with { MaxDegreeOfParallelism = 1 };
        for (var si = 0; si < scenarios.Count; si++)
        {
            var scenario = scenarios[si];
            var reps = new ConcurrentBag<Replicate>();
            Parallel.For(0, replications, settings.Parallelism, r =>
            {
                var repSeed = BlockBootstrap.StableSeed(seed, scenario.Name, r);
                reps.Add(RunOne(scenario, inner with { HistoryMonths = scenario.HistoryMonths }, repSeed));
            });
            results[si] = Summarize(scenario, reps.ToList(), settings.PracticalThreshold);
            progress?.Report((++done, scenarios.Count));
        }

        return new SimulationReport(DateTimeOffset.UtcNow, replications, settings.BootstrapIterations,
            settings.BlockLength, settings.PracticalThreshold, seed, results, Acceptance(results));
    }

    private sealed record Replicate(double TrueGrowth, double TrueSlope, TrendFit Fit, double SeasonalMae);

    private static Replicate RunOne(SyntheticScenario scenario, AnalysisSettings settings, int seed)
    {
        var rng = new Random(seed);
        var series = SyntheticSeriesGenerator.Generate(scenario, rng);
        var fit = TrendFitter.Fit(series.Views.Select(v => (double?)v).ToList(), new HashSet<int>(),
            series.WindowStart, series.WindowEnd, settings, seed ^ 0x5bd1e995);

        var months = Enumerable.Range(0, series.Views.Length)
            .Select(i => YearMonth.FromOrdinal(2020 * 12 + series.FirstCalendarMonth - 1 + i)).ToList();
        var estimated = SeasonalityAnalyzer.MonthlyLogEffects(fit.Decomposition.Seasonal, months, fit.WindowStart, fit.WindowEnd);
        var truth = SeasonalityAnalyzer.MonthlyLogEffects(series.TrueSeasonal, months, fit.WindowStart, fit.WindowEnd);
        var mae = Enumerable.Range(0, 12).Average(m => Math.Abs(estimated[m] - truth[m]));
        return new Replicate(scenario.AnnualGrowth, series.TrueMonthlySlope, fit, mae);
    }

    private static TrendClassification TruthClass(double g, double delta) =>
        g > delta ? TrendClassification.Increasing : g < -delta ? TrendClassification.Decreasing : TrendClassification.Stable;

    private static ScenarioMetrics Summarize(SyntheticScenario s, IReadOnlyList<Replicate> reps, double delta)
    {
        var truth = TruthClass(s.AnnualGrowth, delta);
        var n = (double)reps.Count;
        var classes = reps.GroupBy(r => r.Fit.Classification).ToDictionary(g => g.Key.ToString(), g => g.Count());
        foreach (var c in Enum.GetNames<TrendClassification>()) classes.TryAdd(c, 0);

        var wrongDirection = reps.Count(r =>
            (s.AnnualGrowth >= 0 && r.Fit.Classification == TrendClassification.Decreasing) ||
            (s.AnnualGrowth <= 0 && r.Fit.Classification == TrendClassification.Increasing)) / n;
        var falsePositive = Math.Abs(s.AnnualGrowth) <= delta
            ? reps.Count(r => r.Fit.Classification is TrendClassification.Increasing or TrendClassification.Decreasing) / n
            : double.NaN;
        var pointDirection = s.AnnualGrowth == 0
            ? double.NaN
            : reps.Count(r => Math.Sign(r.Fit.Estimate.MonthlyLogSlope) == Math.Sign(s.AnnualGrowth)) / n;
        var classDirection = s.AnnualGrowth == 0
            ? double.NaN
            : reps.Count(r => r.Fit.Classification ==
                              (s.AnnualGrowth > 0 ? TrendClassification.Increasing : TrendClassification.Decreasing)) / n;

        return new ScenarioMetrics(
            s,
            reps.Count,
            truth.ToString(),
            reps.Average(r => Math.Abs(r.Fit.Estimate.AnnualizedGrowth - r.TrueGrowth)),
            reps.Average(r => r.Fit.Estimate.AnnualizedGrowth - r.TrueGrowth),
            reps.Count(r => r.Fit.SlopeInterval.Contains(r.TrueSlope)) / n,
            reps.Average(r => r.Fit.GrowthInterval.Width),
            reps.Count(r => r.Fit.Classification == truth) / n,
            falsePositive,
            wrongDirection,
            reps.Count(r => r.Fit.Classification == TrendClassification.Inconclusive) / n,
            pointDirection,
            classDirection,
            reps.Average(r => r.SeasonalMae),
            classes);
    }

    public static IReadOnlyList<AcceptanceCheck> Acceptance(IReadOnlyList<ScenarioMetrics> results)
    {
        var checks = new List<AcceptanceCheck>();
        var coverage = WeightedMean(results, r => r.CiCoverage);
        checks.Add(new AcceptanceCheck("95% CI empirical coverage", coverage, "0.92–0.97", coverage is >= 0.92 and <= 0.97,
            "all scenarios"));

        var flat = results.Where(r => r.Scenario.AnnualGrowth == 0).ToList();
        if (flat.Count > 0)
        {
            var fpr = WeightedMean(flat, r => r.DirectionalFalsePositiveRate);
            checks.Add(new AcceptanceCheck("Flat-series directional false-positive rate", fpr, "< 0.10", fpr < 0.10,
                "true growth = 0"));
        }

        var strong = results.Where(r => Math.Abs(r.Scenario.AnnualGrowth) >= 0.2).ToList();
        if (strong.Count > 0)
        {
            var point = WeightedMean(strong, r => r.PointDirectionAccuracy);
            checks.Add(new AcceptanceCheck("Strong-trend direction accuracy (point estimate)", point, "> 0.90",
                point > 0.90, "|true growth| ≥ 20%"));
            var cls = WeightedMean(strong, r => r.ClassDirectionAccuracy);
            checks.Add(new AcceptanceCheck("Strong-trend direction accuracy (classification)", cls, "reported",
                true, "|true growth| ≥ 20%"));
        }

        var wrong = WeightedMean(results, r => r.WrongDirectionRate);
        checks.Add(new AcceptanceCheck("Wrong-direction classification rate", wrong, "< 0.025", wrong < 0.025, "all scenarios"));
        var mae = WeightedMean(results, r => r.GrowthMae);
        checks.Add(new AcceptanceCheck("Annualized growth MAE", mae, "reported", true, "all scenarios"));
        var seasonal = results.Where(r => r.Scenario.SeasonalAmplitude > 0).ToList();
        if (seasonal.Count > 0)
        {
            var smae = WeightedMean(seasonal, r => r.SeasonalityMae);
            checks.Add(new AcceptanceCheck("Seasonal effect MAE (log scale)", smae, "< 0.05", smae < 0.05,
                "seasonal scenarios"));
        }

        return checks;
    }

    private static double WeightedMean(IEnumerable<ScenarioMetrics> results, Func<ScenarioMetrics, double> f)
    {
        var list = results.Where(r => !double.IsNaN(f(r))).ToList();
        if (list.Count == 0) return double.NaN;
        return list.Sum(r => f(r) * r.Replications) / list.Sum(r => (double)r.Replications);
    }
}
