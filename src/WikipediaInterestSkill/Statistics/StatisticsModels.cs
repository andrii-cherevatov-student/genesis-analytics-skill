using System.Text.Json.Serialization;

namespace WikipediaInterestSkill.Statistics;

[JsonConverter(typeof(JsonStringEnumConverter<TrendClassification>))]
public enum TrendClassification
{
    Increasing,
    Decreasing,
    Stable,
    Inconclusive,
}

[JsonConverter(typeof(JsonStringEnumConverter<MannKendallVariant>))]
public enum MannKendallVariant
{
    Standard,
    HamedRaoModified,
}

public sealed record TrendEstimate(double MonthlyLogSlope, double AnnualizedGrowth);

public sealed record ConfidenceInterval(double Lower, double Upper, double ConfidenceLevel)
{
    public double Width => Upper - Lower;
    public bool Contains(double value) => value >= Lower && value <= Upper;
}

public sealed record MannKendallResult(
    double Tau,
    double PValue,
    MannKendallVariant Variant,
    double S,
    double Z,
    double VarianceCorrectionFactor,
    int SampleSize);
