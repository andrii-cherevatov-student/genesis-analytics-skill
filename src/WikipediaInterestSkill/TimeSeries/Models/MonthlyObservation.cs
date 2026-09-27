using System.Text.Json.Serialization;

namespace WikipediaInterestSkill.TimeSeries.Models;

[JsonConverter(typeof(JsonStringEnumConverter<MonthStatus>))]
public enum MonthStatus
{
    Complete,
    Scaled,
    Missing,
}

public sealed record MonthlyObservation(
    YearMonth Period,
    long ObservedViews,
    int DaysWithData,
    int DaysInMonth,
    double? AdjustedViews,
    MonthStatus Status);
