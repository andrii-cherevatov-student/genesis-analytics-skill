namespace WikipediaInterestSkill.Statistics;

public static class EquivalenceClassifier
{
    public const double DefaultThreshold = 0.05;

    public static TrendClassification Classify(ConfidenceInterval annualizedGrowthCi, double threshold = DefaultThreshold)
    {
        if (threshold < 0 || double.IsNaN(threshold)) throw new ArgumentOutOfRangeException(nameof(threshold));
        var (lower, upper) = (annualizedGrowthCi.Lower, annualizedGrowthCi.Upper);
        if (double.IsNaN(lower) || double.IsNaN(upper)) return TrendClassification.Inconclusive;
        if (lower > threshold) return TrendClassification.Increasing;
        if (upper < -threshold) return TrendClassification.Decreasing;
        if (lower >= -threshold && upper <= threshold) return TrendClassification.Stable;
        return TrendClassification.Inconclusive;
    }
}
