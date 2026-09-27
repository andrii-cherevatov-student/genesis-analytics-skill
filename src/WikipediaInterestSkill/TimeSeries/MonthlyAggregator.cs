using WikipediaInterestSkill.TimeSeries.Models;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.TimeSeries;

public static class MonthlyAggregator
{
    public const double MinimumDayCoverage = 0.5;

    public static IReadOnlyList<MonthlyObservation> Aggregate(
        IEnumerable<DailyPageview> daily,
        YearMonth first,
        YearMonth last)
    {
        if (last < first) throw new ArgumentException("Last month precedes first month.", nameof(last));

        var byMonth = new Dictionary<YearMonth, (long Sum, HashSet<DateOnly> Days)>();
        foreach (var d in daily)
        {
            if (d.Views < 0) throw new InvalidDataException($"Negative pageview count on {d.Date:yyyy-MM-dd}.");
            var ym = YearMonth.From(d.Date);
            if (ym < first || ym > last) continue;
            if (!byMonth.TryGetValue(ym, out var acc)) acc = (0, new HashSet<DateOnly>());
            if (!acc.Days.Add(d.Date))
                throw new InvalidDataException($"Duplicate daily observation for {d.Date:yyyy-MM-dd}.");
            byMonth[ym] = (acc.Sum + d.Views, acc.Days);
        }

        var result = new List<MonthlyObservation>();
        foreach (var month in YearMonth.Range(first, last))
        {
            var daysInMonth = month.DaysInMonth;
            if (!byMonth.TryGetValue(month, out var acc))
            {
                result.Add(new MonthlyObservation(month, 0, 0, daysInMonth, null, MonthStatus.Missing));
                continue;
            }

            var daysWithData = acc.Days.Count;
            var coverage = (double)daysWithData / daysInMonth;
            if (daysWithData == daysInMonth)
                result.Add(new MonthlyObservation(month, acc.Sum, daysWithData, daysInMonth, acc.Sum, MonthStatus.Complete));
            else if (coverage >= MinimumDayCoverage)
                result.Add(new MonthlyObservation(month, acc.Sum, daysWithData, daysInMonth,
                    acc.Sum * (double)daysInMonth / daysWithData, MonthStatus.Scaled));
            else
                result.Add(new MonthlyObservation(month, acc.Sum, daysWithData, daysInMonth, null, MonthStatus.Missing));
        }

        return result;
    }
}
