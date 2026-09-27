using System.Globalization;
using System.Text.Json.Serialization;
using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.Seasonality;

[JsonConverter(typeof(JsonStringEnumConverter<SeasonalityCategory>))]
public enum SeasonalityCategory
{
    Negligible,
    Weak,
    Moderate,
    Strong,
}

public sealed record MonthlySeasonalEffect(int Month, string MonthName, double LogEffect, double RelativeEffect);

public sealed record SeasonalityResult(
    double Strength,
    SeasonalityCategory Category,
    IReadOnlyList<MonthlySeasonalEffect> MonthlyEffects,
    int? PeakMonth,
    string? PeakMonthName,
    double? PeakUplift,
    int? LowMonth,
    string? LowMonthName,
    double? LowDrop,
    double CyclesObserved,
    string Interpretation);

public static class SeasonalityAnalyzer
{
    public static SeasonalityResult Analyze(
        TimeSeriesDecomposition decomposition,
        IReadOnlyList<YearMonth> months,
        int windowStart,
        int windowEnd,
        IReadOnlyList<bool>? imputed = null)
    {
        var s = decomposition.Seasonal;
        var r = decomposition.Remainder;
        var idx = Enumerable.Range(0, s.Count).Where(i => imputed is null || !imputed[i]).ToList();
        var strength = Strength(idx.Select(i => s[i]).ToList(), idx.Select(i => r[i]).ToList());
        var category = strength switch
        {
            < 0.2 => SeasonalityCategory.Negligible,
            < 0.4 => SeasonalityCategory.Weak,
            < 0.64 => SeasonalityCategory.Moderate,
            _ => SeasonalityCategory.Strong,
        };

        var effects = MonthlyLogEffects(s, months, windowStart, windowEnd);
        var monthly = Enumerable.Range(0, 12)
            .Select(m => new MonthlySeasonalEffect(m + 1, MonthName(m + 1), effects[m], Math.Exp(effects[m]) - 1))
            .ToList();

        int? peak = null, low = null;
        double? uplift = null, drop = null;
        if (category != SeasonalityCategory.Negligible)
        {
            var p = monthly.MaxBy(e => e.LogEffect)!;
            var l = monthly.MinBy(e => e.LogEffect)!;
            (peak, uplift, low, drop) = (p.Month, p.RelativeEffect, l.Month, l.RelativeEffect);
        }

        var cycles = s.Count / 12.0;
        return new SeasonalityResult(strength, category, monthly, peak, peak is { } pm ? MonthName(pm) : null, uplift,
            low, low is { } lm ? MonthName(lm) : null, drop, Math.Round(cycles, 1),
            Interpret(strength, category, peak, uplift, low, drop));
    }

    public static double Strength(IReadOnlyList<double> seasonal, IReadOnlyList<double> remainder)
    {
        var sr = seasonal.Zip(remainder, (a, b) => a + b).ToList();
        var varSr = Descriptive.SampleVariance(sr);
        if (varSr <= 0) return 0;
        return Math.Max(0, 1 - Descriptive.SampleVariance(remainder) / varSr);
    }

    public static double[] MonthlyLogEffects(IReadOnlyList<double> seasonal, IReadOnlyList<YearMonth> months, int windowStart, int windowEnd)
    {
        var sum = new double[12];
        var count = new int[12];
        for (var i = windowStart; i <= windowEnd; i++)
        {
            sum[months[i].Month - 1] += seasonal[i];
            count[months[i].Month - 1]++;
        }

        for (var m = 0; m < 12; m++)
        {
            if (count[m] > 0) continue;
            for (var i = 0; i < seasonal.Count; i++)
            {
                if (months[i].Month != m + 1) continue;
                sum[m] += seasonal[i];
                count[m]++;
            }
        }

        var effects = Enumerable.Range(0, 12).Select(m => count[m] > 0 ? sum[m] / count[m] : 0).ToArray();
        var mean = effects.Average();
        for (var m = 0; m < 12; m++) effects[m] -= mean;
        return effects;
    }

    public static string MonthName(int month) =>
        CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);

    private static string Interpret(double strength, SeasonalityCategory category, int? peak, double? uplift, int? low, double? drop)
    {
        if (category == SeasonalityCategory.Negligible || peak is null || low is null)
            return $"No meaningful recurring seasonal pattern (seasonality strength {strength:0.00}).";
        return $"{category} seasonality (strength {strength:0.00}): {MonthName(peak.Value)} is typically " +
               $"{Format.Pct0(uplift!.Value)} versus the yearly baseline and {MonthName(low.Value)} {Format.Pct0(drop!.Value)}. " +
               "This recurring pattern is separate from the long-term trend.";
    }
}
