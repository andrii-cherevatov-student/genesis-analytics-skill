using WikipediaInterestSkill.Statistics;

namespace WikipediaInterestSkill.Application;

internal static class ComparisonBuilder
{
    public const string RankingRule =
        "Ranked by the lower bound of the 95% confidence interval of annualized growth (most conservative plausible growth).";

    public const string IndexBase =
        "Normalized index: underlying trend level in the first reporting month = 100 (per language).";

    public static ComparisonResult Build(IReadOnlyList<LanguageAnalysis> analyses, double threshold)
    {
        var ranked = analyses
            .OrderByDescending(a => a.Fit.GrowthInterval.Lower)
            .ThenByDescending(a => a.Fit.Estimate.AnnualizedGrowth)
            .Select((a, i) => new RankedLanguage(
                i + 1,
                a.Result.Language,
                a.Fit.Classification,
                a.Fit.Estimate.AnnualizedGrowth,
                a.Fit.GrowthInterval.Lower,
                a.Fit.GrowthInterval.Upper,
                a.Result.Volume.Last12MonthsViews,
                a.Result.Seasonality.Category.ToString()))
            .ToList();

        var pairs = new List<PairwiseDifference>();
        for (var i = 0; i < analyses.Count; i++)
        for (var j = i + 1; j < analyses.Count; j++)
            pairs.Add(Difference(analyses[i], analyses[j]));

        var top = ranked[0];
        string? strongest = top.Classification == TrendClassification.Increasing ? top.Language : null;
        return new ComparisonResult(ranked, pairs, strongest, Statement(ranked, pairs, strongest, threshold), RankingRule,
            IndexBase);
    }

    internal static PairwiseDifference Difference(LanguageAnalysis a, LanguageAnalysis b)
    {
        var ra = a.Fit.ReplicateGrowth;
        var rb = b.Fit.ReplicateGrowth;
        var n = Math.Min(ra.Length, rb.Length);
        var diffs = new double[n];
        for (var k = 0; k < n; k++) diffs[k] = ra[k] - rb[k];
        Array.Sort(diffs);
        const double alpha = 1.0 - AnalysisSettings.ConfidenceLevel;
        var ci = new ConfidenceInterval(Median.QuantileSorted(diffs, alpha / 2), Median.QuantileSorted(diffs, 1 - alpha / 2),
            AnalysisSettings.ConfidenceLevel);
        var point = a.Fit.Estimate.AnnualizedGrowth - b.Fit.Estimate.AnnualizedGrowth;
        return new PairwiseDifference(a.Result.Language, b.Result.Language, point, ci, ci.Lower > 0 || ci.Upper < 0);
    }

    private static string Statement(
        IReadOnlyList<RankedLanguage> ranked, IReadOnlyList<PairwiseDifference> pairs, string? strongest, double threshold)
    {
        var parts = new List<string>();
        var increasing = ranked.Where(r => r.Classification == TrendClassification.Increasing).Select(r => r.Language).ToList();
        if (strongest is null)
        {
            parts.Add($"No language shows statistically and practically meaningful growth (lower CI bound above +{Format.Threshold(threshold)}/yr).");
        }
        else
        {
            var top = ranked[0];
            parts.Add($"{top.Language} shows the strongest evidence of sustained growth ({Format.Pct(top.AnnualizedGrowth)}/yr, " +
                      $"95% CI {Format.Pct(top.CiLower)} to {Format.Pct(top.CiUpper)}).");
            if (increasing.Count > 1)
                parts.Add($"Also classified Increasing: {string.Join(", ", increasing.Where(l => l != top.Language))}.");

            var vsOthers = pairs.Where(p => p.LanguageA == top.Language || p.LanguageB == top.Language).ToList();
            var distinguishable = vsOthers.Where(p => p.Distinguishable).Select(p => p.LanguageA == top.Language ? p.LanguageB : p.LanguageA).ToList();
            var notDistinguishable = vsOthers.Where(p => !p.Distinguishable).Select(p => p.LanguageA == top.Language ? p.LanguageB : p.LanguageA).ToList();
            if (distinguishable.Count > 0)
                parts.Add($"Its growth rate is statistically distinguishable from: {string.Join(", ", distinguishable)}.");
            if (notDistinguishable.Count > 0)
                parts.Add($"Its growth rate is NOT statistically distinguishable from: {string.Join(", ", notDistinguishable)}.");
        }

        var inconclusive = ranked.Where(r => r.Classification == TrendClassification.Inconclusive).Select(r => r.Language).ToList();
        if (inconclusive.Count > 0)
            parts.Add($"Inconclusive (insufficient evidence either way): {string.Join(", ", inconclusive)}.");
        parts.Add("Absolute volumes reflect the size of each Wikipedia edition, not market size; compare growth rates, not raw traffic.");
        return string.Join(" ", parts);
    }
}
