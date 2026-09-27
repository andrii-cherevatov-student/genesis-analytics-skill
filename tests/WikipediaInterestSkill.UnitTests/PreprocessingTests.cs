using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.TimeSeries;
using WikipediaInterestSkill.TimeSeries.Models;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.UnitTests;

public class YearMonthTests
{
    [Fact]
    public void Parses_and_formats()
    {
        var m = YearMonth.Parse("2025-04");
        m.Year.Should().Be(2025);
        m.Month.Should().Be(4);
        m.ToString().Should().Be("2025-04");
        m.DaysInMonth.Should().Be(30);
        YearMonth.Parse("2024-02").DaysInMonth.Should().Be(29);
    }

    [Fact]
    public void Arithmetic_crosses_year_boundaries()
    {
        var m = new YearMonth(2024, 11);
        m.AddMonths(3).Should().Be(new YearMonth(2025, 2));
        m.AddMonths(-11).Should().Be(new YearMonth(2023, 12));
        new YearMonth(2026, 8).MonthsSince(new YearMonth(2024, 9)).Should().Be(23);
        YearMonth.Range(new YearMonth(2024, 11), new YearMonth(2025, 2)).Should().HaveCount(4);
    }

    [Theory]
    [InlineData("2025-13")]
    [InlineData("April 2025")]
    [InlineData("")]
    public void Rejects_invalid_months(string text) => YearMonth.TryParse(text, out _).Should().BeFalse();
}

public class MonthlyAggregatorTests
{
    private static IEnumerable<DailyPageview> Days(DateOnly from, int count, Func<int, long> views) =>
        Enumerable.Range(0, count).Select(i => new DailyPageview(from.AddDays(i), views(i)));

    [Fact]
    public void Sums_complete_months()
    {
        var daily = Days(new DateOnly(2025, 1, 1), 59, _ => 10);
        var months = MonthlyAggregator.Aggregate(daily, new YearMonth(2025, 1), new YearMonth(2025, 2));
        months.Should().HaveCount(2);
        months[0].ObservedViews.Should().Be(310);
        months[0].Status.Should().Be(MonthStatus.Complete);
        months[0].AdjustedViews.Should().Be(310);
        months[1].ObservedViews.Should().Be(280);
    }

    [Fact]
    public void Scales_partially_reported_months_and_never_assumes_zero()
    {
        var daily = Days(new DateOnly(2025, 4, 1), 20, _ => 10);
        var m = MonthlyAggregator.Aggregate(daily, new YearMonth(2025, 4), new YearMonth(2025, 4)).Single();
        m.Status.Should().Be(MonthStatus.Scaled);
        m.ObservedViews.Should().Be(200);
        m.DaysWithData.Should().Be(20);
        m.AdjustedViews.Should().BeApproximately(300, 1e-9);
    }

    [Fact]
    public void Treats_months_with_too_few_days_as_missing()
    {
        var daily = Days(new DateOnly(2025, 4, 1), 10, _ => 10);
        var months = MonthlyAggregator.Aggregate(daily, new YearMonth(2025, 3), new YearMonth(2025, 4));
        months[0].Status.Should().Be(MonthStatus.Missing);
        months[0].AdjustedViews.Should().BeNull();
        months[1].Status.Should().Be(MonthStatus.Missing);
        months[1].AdjustedViews.Should().BeNull();
    }

    [Fact]
    public void Rejects_duplicate_and_negative_observations()
    {
        var d = new DateOnly(2025, 1, 1);
        var act1 = () => MonthlyAggregator.Aggregate(new[] { new DailyPageview(d, 1), new DailyPageview(d, 2) },
            new YearMonth(2025, 1), new YearMonth(2025, 1));
        act1.Should().Throw<InvalidDataException>();
        var act2 = () => MonthlyAggregator.Aggregate(new[] { new DailyPageview(d, -1) }, new YearMonth(2025, 1), new YearMonth(2025, 1));
        act2.Should().Throw<InvalidDataException>();
    }
}

public class LogTransformerTests
{
    [Fact]
    public void Forward_and_inverse_round_trip()
    {
        LogTransformer.Forward(0).Should().Be(0);
        LogTransformer.Inverse(LogTransformer.Forward(12345)).Should().BeApproximately(12345, 1e-6);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(-0.25)]
    [InlineData(0.0)]
    [InlineData(1.5)]
    public void Annualization_inverts_monthly_slope(double growth)
    {
        var beta = LogTransformer.MonthlyLogSlopeFromAnnualGrowth(growth);
        LogTransformer.AnnualizeMonthlyLogSlope(beta).Should().BeApproximately(growth, 1e-12);
    }

    [Fact]
    public void Annualization_uses_exp_of_twelve_slopes()
    {
        LogTransformer.AnnualizeMonthlyLogSlope(0.01).Should().BeApproximately(Math.Exp(0.12) - 1, 1e-15);
    }

    [Fact]
    public void Rejects_negative_views()
    {
        var act = () => LogTransformer.Forward(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class ReportingWindowTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    [Fact]
    public void Uses_complete_months_only()
    {
        var w = ReportingWindow.FromDates(new DateOnly(2024, 9, 1), new DateOnly(2026, 9, 1), Today);
        w.First.Should().Be(new YearMonth(2024, 9));
        w.Last.Should().Be(new YearMonth(2026, 8));
        w.Months.Should().Be(24);
    }

    [Fact]
    public void Skips_partial_first_month()
    {
        var w = ReportingWindow.FromDates(new DateOnly(2024, 9, 15), new DateOnly(2026, 8, 31), Today);
        w.First.Should().Be(new YearMonth(2024, 10));
        w.Notes.Should().NotBeEmpty();
    }

    [Fact]
    public void Caps_at_last_published_month()
    {
        var w = ReportingWindow.FromDates(new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31), Today);
        w.Last.Should().Be(new YearMonth(2026, 8));
    }

    [Fact]
    public void Rejects_short_and_inverted_periods()
    {
        var shortPeriod = () => ReportingWindow.FromDates(new DateOnly(2026, 1, 1), new DateOnly(2026, 8, 31), Today);
        shortPeriod.Should().Throw<SkillException>().Which.Code.Should().Be(ErrorCodes.InvalidDateRange);
        var inverted = () => ReportingWindow.FromDates(new DateOnly(2026, 1, 1), new DateOnly(2025, 1, 1), Today);
        inverted.Should().Throw<SkillException>().Which.Code.Should().Be(ErrorCodes.InvalidDateRange);
    }
}
