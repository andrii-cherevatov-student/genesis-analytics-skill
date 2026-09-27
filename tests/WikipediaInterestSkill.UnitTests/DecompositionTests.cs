using WikipediaInterestSkill.Anomalies;
using WikipediaInterestSkill.Seasonality;
using WikipediaInterestSkill.TimeSeries;
using WikipediaInterestSkill.TimeSeries.Models;

namespace WikipediaInterestSkill.UnitTests;

public class StlDecomposerTests
{
    private static double[] Pattern() => Enumerable.Range(0, 12).Select(m => 0.3 * Math.Sin(2 * Math.PI * m / 12)).ToArray();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recovers_linear_trend_and_periodic_seasonality(bool robust)
    {
        var s = Pattern();
        var y = Enumerable.Range(0, 60).Select(t => 9 + 0.01 * t + s[t % 12]).ToArray();
        var d = StlDecomposer.Decompose(y, new StlOptions { Robust = robust, InnerIterations = robust ? null : 10 });
        for (var t = 0; t < y.Length; t++)
        {
            d.Trend[t].Should().BeApproximately(9 + 0.01 * t, 1e-4);
            d.Seasonal[t].Should().BeApproximately(s[t % 12], 1e-4);
            (d.Trend[t] + d.Seasonal[t] + d.Remainder[t]).Should().BeApproximately(y[t], 1e-12);
        }
    }

    [Fact]
    public void Returns_all_components_with_series_length()
    {
        var y = Enumerable.Range(0, 30).Select(t => Math.Sin(t) + t * 0.1).ToArray();
        var d = StlDecomposer.Decompose(y);
        d.Observed.Should().HaveCount(30);
        d.Trend.Should().HaveCount(30);
        d.Seasonal.Should().HaveCount(30);
        d.Remainder.Should().HaveCount(30);
        d.RobustnessWeights.Should().HaveCount(30);
    }

    [Fact]
    public void Requires_two_full_periods()
    {
        var act = () => StlDecomposer.Decompose(new double[23]);
        act.Should().Throw<ArgumentException>();
        var nan = () => StlDecomposer.Decompose(Enumerable.Repeat(double.NaN, 30).ToArray());
        nan.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Robust_fit_downweights_a_spike_and_keeps_the_trend()
    {
        var s = Pattern();
        var rng = new Random(5);
        var y = Enumerable.Range(0, 48).Select(t => 9 + 0.01 * t + s[t % 12] + 0.02 * (rng.NextDouble() - 0.5)).ToArray();
        y[30] += 1.5;
        var robust = StlDecomposer.Decompose(y, new StlOptions { Robust = true });
        var plain = StlDecomposer.Decompose(y, new StlOptions { Robust = false });
        robust.RobustnessWeights[30].Should().BeLessThan(0.05);
        Math.Abs(robust.Trend[30] - (9 + 0.3)).Should().BeLessThan(Math.Abs(plain.Trend[30] - (9 + 0.3)));
        robust.Remainder[30].Should().BeGreaterThan(1.3);
    }

    [Fact]
    public void Default_windows_follow_cleveland_rules()
    {
        var o = new StlOptions();
        o.ResolvedTrendWindow.Should().Be(21);
        o.ResolvedLowPassWindow.Should().Be(13);
    }

    [Fact]
    public void Seasonal_degrees_of_freedom_are_close_to_period_minus_one()
    {
        StlDecomposer.SeasonalDegreesOfFreedom(48, new StlOptions()).Should().BeInRange(10, 14);
    }
}

public class LoessTests
{

    [Fact]
    public void Degree_one_loess_reproduces_lines()
    {
        var y = Enumerable.Range(0, 20).Select(t => 2 - 0.5 * t).ToArray();
        var ys = new double[20];
        Loess.Smooth(y, 20, 7, 1, false, new double[20], ys, new double[20]);
        for (var i = 0; i < 20; i++) ys[i].Should().BeApproximately(y[i], 1e-10);
    }
}

public class SeasonalityAnalyzerTests
{
    private static List<YearMonth> Months(int n) => Enumerable.Range(0, n).Select(i => new YearMonth(2022, 1).AddMonths(i)).ToList();

    [Fact]
    public void Strength_is_one_without_remainder_and_zero_without_seasonality()
    {
        var s = Enumerable.Range(0, 36).Select(t => Math.Sin(t)).ToList();
        SeasonalityAnalyzer.Strength(s, Enumerable.Repeat(0.0, 36).ToList()).Should().Be(1);
        SeasonalityAnalyzer.Strength(Enumerable.Repeat(0.0, 36).ToList(), s).Should().Be(0);
    }

    [Fact]
    public void Reports_peak_and_low_months_with_relative_uplift()
    {
        var effects = new double[12];
        effects[8] = Math.Log(1.35);
        effects[6] = Math.Log(0.8);
        var mean = effects.Average();
        var seasonal = Enumerable.Range(0, 36).Select(t => effects[t % 12] - mean).ToList();
        var remainder = Enumerable.Range(0, 36).Select(t => 0.01 * Math.Sin(7 * t)).ToList();
        var d = new TimeSeriesDecomposition(new double[36], new double[36], seasonal, remainder, new double[36]);
        var r = SeasonalityAnalyzer.Analyze(d, Months(36), 12, 35);
        r.Category.Should().Be(SeasonalityCategory.Strong);
        r.PeakMonth.Should().Be(9);
        r.LowMonth.Should().Be(7);
        r.MonthlyEffects.Sum(e => e.LogEffect).Should().BeApproximately(0, 1e-12);
        r.PeakUplift.Should().BeApproximately(Math.Exp(Math.Log(1.35) - mean) - 1, 1e-9);
        r.Interpretation.Should().Contain("September");
    }

    [Fact]
    public void Negligible_seasonality_has_no_peak_month()
    {
        var rng = new Random(1);
        var seasonal = Enumerable.Range(0, 36).Select(t => 0.001 * Math.Sin(2 * Math.PI * t / 12)).ToList();
        var remainder = Enumerable.Range(0, 36).Select(_ => rng.NextDouble() - 0.5).ToList();
        var d = new TimeSeriesDecomposition(new double[36], new double[36], seasonal, remainder, new double[36]);
        var r = SeasonalityAnalyzer.Analyze(d, Months(36), 12, 35);
        r.Category.Should().Be(SeasonalityCategory.Negligible);
        r.PeakMonth.Should().BeNull();
    }
}

public class ResidualAnomalyDetectorTests
{
    [Fact]
    public void Flags_spikes_with_robust_z_and_keeps_observed_values()
    {
        var n = 36;
        var rng = new Random(2);
        var remainder = Enumerable.Range(0, n).Select(_ => 0.05 * (rng.NextDouble() - 0.5)).ToArray();
        remainder[20] = 0.9;
        remainder[25] = -0.8;
        var trend = Enumerable.Repeat(Math.Log(1001), n).ToArray();
        var d = new TimeSeriesDecomposition(new double[n], trend, new double[n], remainder, new double[n]);
        var months = Enumerable.Range(0, n).Select(i => new YearMonth(2023, 1).AddMonths(i)).ToList();
        var observed = Enumerable.Range(0, n).Select(i => (long?)Math.Round(Math.Exp(trend[i] + remainder[i]) - 1)).ToList();
        var adjusted = observed.Select(v => (double?)v).ToList();
        var anomalies = ResidualAnomalyDetector.Detect(d, months, new bool[n], observed, adjusted, new HashSet<int>(), 12, 35);

        anomalies.Should().HaveCount(2);
        var spike = anomalies.Single(a => a.Period == months[20]);
        spike.Direction.Should().Be(AnomalyDirection.Spike);
        spike.RobustZScore.Should().BeGreaterThan(3);
        spike.ObservedViews.Should().Be(observed[20]);
        spike.EstimatedBaselineViews.Should().Be(1000);
        anomalies.Single(a => a.Period == months[25]).Direction.Should().Be(AnomalyDirection.Dip);
    }


    [Fact]
    public void Degenerate_mad_falls_back_to_mean_absolute_deviation()
    {
        var n = 30;
        var remainder = new double[n];
        remainder[20] = 1.0;
        var d = new TimeSeriesDecomposition(new double[n], Enumerable.Repeat(5.0, n).ToArray(), new double[n], remainder, new double[n]);
        var months = Enumerable.Range(0, n).Select(i => new YearMonth(2023, 1).AddMonths(i)).ToList();
        var views = Enumerable.Range(0, n).Select(_ => (long?)100).ToList();
        var anomalies = ResidualAnomalyDetector.Detect(d, months, new bool[n], views, views.Select(v => (double?)v).ToList(),
            new HashSet<int>(), 0, n - 1);
        anomalies.Should().ContainSingle(a => a.Period == months[20]);
    }
}
