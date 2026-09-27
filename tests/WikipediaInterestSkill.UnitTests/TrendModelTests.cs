using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries.Models;
using WikipediaInterestSkill.Validation;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.UnitTests;

public class TrendFitterTests
{
    private static readonly AnalysisSettings Fast = new() { BootstrapIterations = 400 };

    private static List<double?> Series(double growth, double noise, int seed, int n = 48, double seasonal = 0.3)
    {
        var s = SyntheticSeriesGenerator.Generate(new SyntheticScenario
        {
            AnnualGrowth = growth, NoiseSd = noise, SeasonalAmplitude = seasonal, ReportingMonths = n - 24,
        }, new Random(seed));
        return s.Views.Select(v => (double?)v).ToList();
    }

    [Fact]
    public void Strong_growth_is_classified_increasing_with_interval_around_truth()
    {
        var fit = TrendFitter.Fit(Series(0.30, 0.03, 1), new HashSet<int>(), 24, 47, Fast, 1);
        fit.Classification.Should().Be(TrendClassification.Increasing);
        fit.Estimate.AnnualizedGrowth.Should().BeApproximately(0.30, 0.06);
        fit.GrowthInterval.Contains(0.30).Should().BeTrue();
        fit.GrowthInterval.Lower.Should().BeLessThan(fit.Estimate.AnnualizedGrowth);
        fit.GrowthInterval.Upper.Should().BeGreaterThan(fit.Estimate.AnnualizedGrowth);
    }

    [Fact]
    public void Flat_precise_series_is_stable_and_decline_is_decreasing()
    {
        TrendFitter.Fit(Series(0.0, 0.01, 2), new HashSet<int>(), 24, 47, Fast, 2).Classification
            .Should().Be(TrendClassification.Stable);
        TrendFitter.Fit(Series(-0.3, 0.03, 3), new HashSet<int>(), 24, 47, Fast, 3).Classification
            .Should().Be(TrendClassification.Decreasing);
    }

    [Fact]
    public void Noisy_flat_series_is_not_called_stable_or_directional()
    {
        var fit = TrendFitter.Fit(Series(0.0, 0.35, 4, n: 36), new HashSet<int>(), 24, 35, Fast, 4);
        fit.Classification.Should().Be(TrendClassification.Inconclusive);
    }

    [Fact]
    public void Is_deterministic_for_identical_inputs()
    {
        var views = Series(0.1, 0.1, 5);
        var a = TrendFitter.Fit(views, new HashSet<int>(), 24, 47, Fast, 99);
        var b = TrendFitter.Fit(views, new HashSet<int>(), 24, 47, Fast, 99);
        a.GrowthInterval.Should().Be(b.GrowthInterval);
        a.ReplicateGrowth.Should().Equal(b.ReplicateGrowth);
    }

    [Fact]
    public void Excluded_and_missing_months_are_imputed_not_zeroed()
    {
        var views = Series(0.2, 0.02, 6);
        views[30] = null;
        var fit = TrendFitter.Fit(views, new HashSet<int> { 35 }, 24, 47, Fast, 6);
        fit.Imputed[30].Should().BeTrue();
        fit.Imputed[35].Should().BeTrue();
        fit.LogValues[30].Should().BeGreaterThan(5);
        fit.Estimate.AnnualizedGrowth.Should().BeApproximately(0.2, 0.06);
    }

    [Fact]
    public void Rejects_series_that_are_too_short_or_mostly_missing()
    {
        var shortSeries = () => TrendFitter.Fit(Series(0, 0.1, 7).Take(20).ToList(), new HashSet<int>(), 8, 19, Fast, 1);
        shortSeries.Should().Throw<InsufficientDataException>();
        var views = Series(0, 0.1, 8);
        for (var i = 24; i < 40; i++) views[i] = null;
        var mostlyMissing = () => TrendFitter.Fit(views, new HashSet<int>(), 24, 47, Fast, 1);
        mostlyMissing.Should().Throw<InsufficientDataException>();
    }

    [Fact]
    public void Reuses_bootstrap_deviations_for_shifted_estimates()
    {
        var fit = TrendFitter.Fit(Series(0.1, 0.05, 9), new HashSet<int>(), 24, 47, Fast, 9);
        var (ci, cls) = TrendFitter.ClassifyWithDeviations(fit.Estimate.MonthlyLogSlope, fit.SortedSlopeDeviations, Fast);
        ci.Lower.Should().BeApproximately(fit.GrowthInterval.Lower, 1e-12);
        ci.Upper.Should().BeApproximately(fit.GrowthInterval.Upper, 1e-12);
        cls.Should().Be(fit.Classification);
    }
}

public class InterpretationTests
{
    [Fact]
    public void Inconclusive_text_says_it_is_not_evidence_of_stability()
    {
        var ci = new ConfidenceInterval(-0.05, 0.41, 0.95);
        SeriesAnalyzer.CompatibleWith(ci, 0.05).Should().Equal("stability", "growth");
        SeriesAnalyzer.CompatibleWith(new ConfidenceInterval(-0.3, 0.3, 0.95), 0.05).Should().Equal("decline", "stability", "growth");
        SeriesAnalyzer.CompatibleWith(new ConfidenceInterval(0.1, 0.3, 0.95), 0.05).Should().Equal("growth");
    }

    [Fact]
    public void Percent_formatting_is_signed_and_invariant()
    {
        Format.Pct(0.174).Should().Be("+17.4%");
        Format.Pct(-0.05).Should().Be("−5.0%");
        Format.Pct0(0.346).Should().Be("+35%");
        Format.Pct0(-0.14).Should().Be("−14%");
    }
}

public class ComparisonBuilderTests
{
    private static LanguageAnalysis Analysis(string lang, double growth, int seed)
    {
        var s = SyntheticSeriesGenerator.Generate(new SyntheticScenario { AnnualGrowth = growth, NoiseSd = 0.03 }, new Random(seed));
        var months = Enumerable.Range(0, s.Views.Length).Select(i => new YearMonth(2022, 9).AddMonths(i)).ToList();
        var obs = months.Select((m, i) => new MonthlyObservation(m, (long)s.Views[i], m.DaysInMonth, m.DaysInMonth, s.Views[i],
            MonthStatus.Complete)).ToList();
        var article = new ResolvedArticle(lang, "topic", $"Title {lang}", ResolvedArticle.UrlFor(lang, "T"),
            ResolutionMethod.ExplicitTitle, ResolutionConfidence.High);
        return SeriesAnalyzer.Analyze(article, obs, months[24], months[^1], Array.Empty<YearMonth>(),
            new AnalysisSettings { BootstrapIterations = 400 });
    }

    [Fact]
    public void Ranks_by_lower_bound_and_detects_distinguishable_differences()
    {
        var a = Analysis("pl", 0.40, 1);
        var b = Analysis("cs", -0.20, 2);
        var c = Analysis("uk", 0.40, 3);
        var result = ComparisonBuilder.Build(new[] { b, a, c }, 0.05)!;
        result.RankingByEvidenceOfGrowth[0].Language.Should().BeOneOf("pl", "uk");
        result.RankingByEvidenceOfGrowth[^1].Language.Should().Be("cs");
        result.StrongestEvidenceOfGrowth.Should().BeOneOf("pl", "uk");
        var plCs = result.PairwiseDifferences.Single(p => p.LanguageA == "cs" && p.LanguageB == "pl");
        plCs.Distinguishable.Should().BeTrue();
        plCs.GrowthDifference.Should().BeLessThan(0);
        var plUk = result.PairwiseDifferences.Single(p => p.LanguageA == "pl" && p.LanguageB == "uk");
        plUk.Distinguishable.Should().BeFalse();
        result.Statement.Should().Contain("NOT statistically distinguishable");
    }

    [Fact]
    public void No_strongest_language_when_nothing_is_increasing()
    {
        var result = ComparisonBuilder.Build(new[] { Analysis("pl", -0.3, 4), Analysis("cs", -0.25, 5) }, 0.05)!;
        result.StrongestEvidenceOfGrowth.Should().BeNull();
        result.Statement.Should().Contain("No language shows");
    }
}

public class CliParsingTests
{
    [Fact]
    public void Parses_article_overrides()
    {
        var map = Cli.ParseArticles(new[] { "pl=Astronomia", "cs=\"Přerušovaný půst\"", "UK=https://uk.wikipedia.org/wiki/X" });
        map["pl"].Should().Be("Astronomia");
        map["cs"].Should().Be("Přerušovaný půst");
        map["uk"].Should().Be("https://uk.wikipedia.org/wiki/X");
        var bad = () => Cli.ParseArticles(new[] { "Astronomia" });
        bad.Should().Throw<SkillException>().Which.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public void Resolves_dates_from_month_shorthand()
    {
        var (from, to) = Cli.ResolveDates("2024-09", "2026-08", null);
        from.Should().Be(new DateOnly(2024, 9, 1));
        to.Should().Be(new DateOnly(2026, 8, 31));
        var both = () => Cli.ResolveDates("2024-09-01", null, 24);
        both.Should().Throw<SkillException>();
        var (f2, t2) = Cli.ResolveDates(null, null, 36);
        (YearMonth.From(t2).MonthsSince(YearMonth.From(f2)) + 1).Should().Be(36);
    }

    [Fact]
    public void Validates_requests()
    {
        var req = new AnalysisRequest("x", new[] { "pl,cs", "PL" }, new DateOnly(2024, 1, 1), new DateOnly(2025, 12, 31));
        AnalysisPipeline.ValidateRequest(req).Should().Equal("pl", "cs");
        var bad = () => AnalysisPipeline.ValidateRequest(req with { Languages = new[] { "p l" } });
        bad.Should().Throw<SkillException>().Which.Code.Should().Be(ErrorCodes.UnknownLanguage);
        var noTopic = () => AnalysisPipeline.ValidateRequest(req with { Topic = "" });
        noTopic.Should().Throw<SkillException>().Which.Code.Should().Be(ErrorCodes.InvalidArgument);
        var threshold = () => AnalysisPipeline.ValidateRequest(req with { PracticalTrendThreshold = -1 });
        threshold.Should().Throw<SkillException>();
    }

    [Fact]
    public void Rejects_output_path_that_is_a_file()
    {
        var file = Path.GetTempFileName();
        try
        {
            var act = () => AnalysisPipeline.PrepareOutputDirectory(file);
            act.Should().Throw<SkillException>().Which.Code.Should().Be(ErrorCodes.InvalidOutputDirectory);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
