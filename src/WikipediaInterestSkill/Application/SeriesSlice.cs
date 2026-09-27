using WikipediaInterestSkill.Statistics;
using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.Application;

internal sealed class SeriesSlice
{
    private SeriesSlice(
        IReadOnlyList<MonthlyObservation> observations,
        int windowStart,
        int windowEnd,
        IReadOnlySet<int> excluded,
        YearMonth requestedWindowStart)
    {
        Observations = observations;
        WindowStart = windowStart;
        WindowEnd = windowEnd;
        Excluded = excluded;
        RequestedWindowStart = requestedWindowStart;
        Months = observations.Select(o => o.Period).ToList();
        AdjustedViews = observations.Select(o => o.AdjustedViews).ToList();
        ObservedViews = observations.Select(o => o.Status == MonthStatus.Missing ? (long?)null : o.ObservedViews).ToList();
    }

    public IReadOnlyList<MonthlyObservation> Observations { get; }
    public IReadOnlyList<YearMonth> Months { get; }
    public IReadOnlyList<double?> AdjustedViews { get; }
    public IReadOnlyList<long?> ObservedViews { get; }
    public int WindowStart { get; }
    public int WindowEnd { get; }
    public IReadOnlySet<int> Excluded { get; }
    public YearMonth RequestedWindowStart { get; }
    public YearMonth FirstWindowMonth => Months[WindowStart];
    public YearMonth LastWindowMonth => Months[WindowEnd];
    public int WindowLength => WindowEnd - WindowStart + 1;
    public bool WindowStartShifted => FirstWindowMonth != RequestedWindowStart;

    public static SeriesSlice Create(
        IReadOnlyList<MonthlyObservation> all,
        YearMonth windowStart,
        YearMonth windowEnd,
        int historyMonths,
        IReadOnlyCollection<YearMonth> excludedMonths)
    {
        if (windowEnd < windowStart) throw new ArgumentException("Window end precedes window start.");
        var firstWithData = all.FirstOrDefault(o => o.Status != MonthStatus.Missing)?.Period;
        if (firstWithData is null || firstWithData > windowEnd)
            throw new InsufficientDataException("No pageview data is available for the reporting period.");

        var effectiveWindowStart = firstWithData.Value > windowStart ? firstWithData.Value : windowStart;
        var sliceStart = windowStart.AddMonths(-historyMonths);
        if (sliceStart < firstWithData.Value) sliceStart = firstWithData.Value;

        var slice = all.Where(o => o.Period >= sliceStart && o.Period <= windowEnd).OrderBy(o => o.Period).ToList();
        if (slice.Count == 0 || slice[0].Period != sliceStart || slice[^1].Period != windowEnd)
            throw new InvalidOperationException("Monthly observations do not cover the requested span.");

        var ws = effectiveWindowStart.MonthsSince(sliceStart);
        var we = windowEnd.MonthsSince(sliceStart);
        var excluded = new HashSet<int>();
        foreach (var m in excludedMonths)
        {
            var idx = m.MonthsSince(sliceStart);
            if (idx >= 0 && idx < slice.Count) excluded.Add(idx);
        }

        return new SeriesSlice(slice, ws, we, excluded, windowStart);
    }
}
