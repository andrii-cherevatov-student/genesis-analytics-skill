using System.Text.Json.Serialization;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries;
using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.Anomalies;

[JsonConverter(typeof(JsonStringEnumConverter<AnomalyDirection>))]
public enum AnomalyDirection
{
    Spike,
    Dip,
}

public sealed record Anomaly(
    YearMonth Period,
    double RobustZScore,
    long ObservedViews,
    long EstimatedBaselineViews,
    double RelativeDeviation,
    AnomalyDirection Direction,
    bool ExcludedFromTrend,
    double? TrendShiftIfExcluded = null);

public static class ResidualAnomalyDetector
{
    public const double MadToSigma = 1.4826;

    public static IReadOnlyList<Anomaly> Detect(
        TimeSeriesDecomposition decomposition,
        IReadOnlyList<YearMonth> months,
        IReadOnlyList<bool> imputed,
        IReadOnlyList<long?> observedViews,
        IReadOnlyList<double?> adjustedViews,
        IReadOnlySet<int> excluded,
        int windowStart,
        int windowEnd,
        double threshold = 3.0)
    {
        var residuals = Enumerable.Range(0, months.Count).Where(i => !imputed[i])
            .Select(i => decomposition.Remainder[i]).ToList();
        if (residuals.Count < 6) return [];

        var mad = Median.AbsoluteDeviation(residuals, out var median);
        var scale = MadToSigma * mad;
        if (scale <= 1e-12)
        {
            var meanAbs = residuals.Average(r => Math.Abs(r - median));
            scale = 1.2533 * meanAbs;
            if (scale <= 1e-12) return [];
        }

        var result = new List<Anomaly>();
        for (var i = windowStart; i <= windowEnd; i++)
        {
            var isExcluded = excluded.Contains(i);
            if (adjustedViews[i] is not { } adjusted) continue;
            if (imputed[i] && !isExcluded) continue;

            var fitted = decomposition.Trend[i] + decomposition.Seasonal[i];
            var residual = isExcluded ? LogTransformer.Forward(adjusted) - fitted : decomposition.Remainder[i];
            var z = (residual - median) / scale;
            if (Math.Abs(z) <= threshold) continue;

            var baseline = LogTransformer.Inverse(fitted);
            var observed = observedViews[i] ?? (long)Math.Round(adjusted);
            result.Add(new Anomaly(
                months[i],
                Math.Round(z, 2),
                observed,
                (long)Math.Round(baseline),
                baseline > 0 ? adjusted / baseline - 1 : double.NaN,
                z > 0 ? AnomalyDirection.Spike : AnomalyDirection.Dip,
                isExcluded));
        }

        return result.OrderByDescending(a => Math.Abs(a.RobustZScore)).ToList();
    }
}
