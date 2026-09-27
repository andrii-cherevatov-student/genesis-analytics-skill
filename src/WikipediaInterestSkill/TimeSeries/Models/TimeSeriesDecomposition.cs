namespace WikipediaInterestSkill.TimeSeries.Models;

public sealed record TimeSeriesDecomposition(
    IReadOnlyList<double> Observed,
    IReadOnlyList<double> Trend,
    IReadOnlyList<double> Seasonal,
    IReadOnlyList<double> Remainder,
    IReadOnlyList<double> RobustnessWeights);
