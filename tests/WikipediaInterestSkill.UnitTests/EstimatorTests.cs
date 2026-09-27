using WikipediaInterestSkill.Statistics;

namespace WikipediaInterestSkill.UnitTests;

public class MedianTests
{
    [Fact]
    public void Matches_sorting_for_random_arrays()
    {
        var rng = new Random(7);
        for (var trial = 0; trial < 200; trial++)
        {
            var n = rng.Next(1, 60);
            var values = Enumerable.Range(0, n).Select(_ => Math.Round(rng.NextDouble() * 10, 1)).ToArray();
            var sorted = values.OrderBy(v => v).ToArray();
            var expected = n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
            Median.OfInPlace((double[])values.Clone()).Should().Be(expected);
        }
    }

    [Fact]
    public void Quantile_type7_interpolates()
    {
        var sorted = new double[] { 1, 2, 3, 4, 5 };
        Median.QuantileSorted(sorted, 0.5).Should().Be(3);
        Median.QuantileSorted(sorted, 0.25).Should().Be(2);
        Median.QuantileSorted(sorted, 0.1).Should().BeApproximately(1.4, 1e-12);
        Median.QuantileSorted(sorted, 1).Should().Be(5);
    }

    [Fact]
    public void Mad_of_known_sample()
    {
        Median.AbsoluteDeviation(new double[] { 1, 1, 2, 2, 4, 6, 9 }, out var med).Should().Be(1);
        med.Should().Be(2);
    }
}

public class TheilSenTests
{
    [Fact]
    public void Recovers_exact_line()
    {
        var y = Enumerable.Range(0, 24).Select(t => 3.0 + 0.02 * t).ToArray();
        TheilSenEstimator.Slope(y).Should().BeApproximately(0.02, 1e-12);
    }

    [Fact]
    public void Is_robust_to_outliers()
    {
        var y = Enumerable.Range(0, 24).Select(t => 5.0 + 0.01 * t).ToArray();
        y[5] += 3;
        y[17] -= 4;
        y[20] += 10;
        TheilSenEstimator.Slope(y).Should().BeApproximately(0.01, 1e-12);
    }

    [Fact]
    public void Uses_median_of_pairwise_slopes()
    {
        TheilSenEstimator.Slope(new double[] { 0, 1, 4 }).Should().Be(2);
        TheilSenEstimator.Slope(new double[] { 0, 1, 4, 9 }).Should().Be(3);
    }


    [Fact]
    public void Annualizes_estimate()
    {
        var beta = Math.Log(1.2) / 12;
        var y = Enumerable.Range(0, 24).Select(t => 8 + beta * t).ToArray();
        TheilSenEstimator.Estimate(y).AnnualizedGrowth.Should().BeApproximately(0.2, 1e-12);
    }
}

public class EquivalenceClassifierTests
{
    [Theory]
    [InlineData(0.06, 0.30, TrendClassification.Increasing)]
    [InlineData(-0.30, -0.06, TrendClassification.Decreasing)]
    [InlineData(-0.04, 0.03, TrendClassification.Stable)]
    [InlineData(-0.05, 0.05, TrendClassification.Stable)]
    [InlineData(-0.05, 0.41, TrendClassification.Inconclusive)]
    [InlineData(0.02, 0.30, TrendClassification.Inconclusive)]
    [InlineData(-0.30, 0.30, TrendClassification.Inconclusive)]
    [InlineData(0.05, 0.10, TrendClassification.Inconclusive)]
    public void Classifies_by_interval_and_threshold(double lower, double upper, TrendClassification expected) =>
        EquivalenceClassifier.Classify(new ConfidenceInterval(lower, upper, 0.95)).Should().Be(expected);

    [Fact]
    public void Threshold_is_configurable()
    {
        var ci = new ConfidenceInterval(0.04, 0.09, 0.95);
        EquivalenceClassifier.Classify(ci, 0.05).Should().Be(TrendClassification.Inconclusive);
        EquivalenceClassifier.Classify(ci, 0.03).Should().Be(TrendClassification.Increasing);
        EquivalenceClassifier.Classify(ci, 0.10).Should().Be(TrendClassification.Stable);
    }

    [Fact]
    public void Nan_interval_is_inconclusive() =>
        EquivalenceClassifier.Classify(new ConfidenceInterval(double.NaN, 0.1, 0.95)).Should().Be(TrendClassification.Inconclusive);
}

public class MannKendallTests
{
    [Fact]
    public void Strictly_increasing_series_matches_hand_calculation()
    {
        var x = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();
        var r = MannKendallTest.Test(x, forceModified: false);
        r.S.Should().Be(45);
        r.Tau.Should().Be(1);
        r.Z.Should().BeApproximately(44 / Math.Sqrt(125), 1e-12);
        r.PValue.Should().BeApproximately(8.32e-5, 2e-6);
        r.Variant.Should().Be(MannKendallVariant.Standard);
    }

    [Fact]
    public void Ties_reduce_variance()
    {
        var r = MannKendallTest.Test(new double[] { 1, 1, 2, 2, 3, 3 }, forceModified: false);
        r.S.Should().Be(12);
        var expectedVar = (6 * 5 * 17 - 3 * (2 * 1 * 9)) / 18.0;
        r.Z.Should().BeApproximately(11 / Math.Sqrt(expectedVar), 1e-12);
    }

    [Fact]
    public void Constant_series_has_no_trend()
    {
        var r = MannKendallTest.Test(Enumerable.Repeat(3.0, 12).ToArray(), forceModified: false);
        r.S.Should().Be(0);
        r.PValue.Should().Be(1);
    }

    [Fact]
    public void Hamed_rao_factor_inflates_variance_for_positive_autocorrelation()
    {
        var rng = new Random(3);
        var e = 0.0;
        var x = Enumerable.Range(0, 48).Select(_ => e = 0.8 * e + rng.NextDouble() - 0.5).ToArray();
        MannKendallTest.HamedRaoFactor(x).Should().BeGreaterThan(1);
        var modified = MannKendallTest.Test(x, forceModified: true);
        var standard = MannKendallTest.Test(x, forceModified: false);
        modified.Variant.Should().Be(MannKendallVariant.HamedRaoModified);
        Math.Abs(modified.Z).Should().BeLessThanOrEqualTo(Math.Abs(standard.Z));
    }

}

public class AutocorrelationTests
{
    [Fact]
    public void Alternating_series_has_negative_lag1()
    {
        var x = Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? 1.0 : -1.0).ToArray();
        AutocorrelationAnalyzer.Acf(x, 2)[0].Should().BeApproximately(-0.95, 1e-12);
        AutocorrelationAnalyzer.Acf(x, 2)[1].Should().BeApproximately(0.9, 1e-12);
    }

    [Fact]
    public void White_noise_is_not_flagged_and_ar_process_is()
    {
        var rng = new Random(11);
        var noise = Enumerable.Range(0, 200).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var d1 = AutocorrelationAnalyzer.Analyze(noise);
        d1.MaterialLag1Dependence.Should().BeFalse();
        d1.LjungBoxPValue.Should().BeGreaterThan(0.01);

        var e = 0.0;
        var ar = Enumerable.Range(0, 200).Select(_ => e = 0.7 * e + rng.NextDouble() - 0.5).ToArray();
        var d2 = AutocorrelationAnalyzer.Analyze(ar);
        d2.MaterialLag1Dependence.Should().BeTrue();
        d2.Lag1.Should().BeInRange(0.55, 0.85);
        d2.LjungBoxPValue.Should().BeLessThan(0.001);
    }
}

public class BlockBootstrapTests
{
    [Fact]
    public void Output_is_made_of_contiguous_source_blocks()
    {
        var source = Enumerable.Range(0, 30).Select(i => (double)i).ToArray();
        var dest = new double[30];
        BlockBootstrap.Resample(source, 4, new Random(1), dest);
        for (var b = 0; b < 28; b += 4)
            for (var k = 1; k < 4 && b + k < 30; k++)
                dest[b + k].Should().Be(dest[b] + k);
        dest.Should().OnlyContain(v => v >= 0 && v < 30);
    }

    [Fact]
    public void Is_deterministic_for_a_seed_and_supports_longer_destinations()
    {
        var source = Enumerable.Range(0, 10).Select(i => i * 1.5).ToArray();
        var a = new double[25];
        var b = new double[25];
        BlockBootstrap.Resample(source, 3, new Random(42), a);
        BlockBootstrap.Resample(source, 3, new Random(42), b);
        a.Should().Equal(b);
    }

    [Fact]
    public void Stable_seed_is_process_independent()
    {
        BlockBootstrap.StableSeed("pl", "Astronomia", 3).Should().Be(BlockBootstrap.StableSeed("pl", "Astronomia", 3));
        BlockBootstrap.StableSeed("pl", "Astronomia").Should().NotBe(BlockBootstrap.StableSeed("cs", "Astronomia"));
        BlockBootstrap.StableSeed("a").Should().Be(BlockBootstrap.StableSeed("a"));
    }
}

public class LongRunVarianceCorrectionTests
{
    [Fact]
    public void No_dependence_means_no_dependence_scaling()
    {
        LongRunVarianceCorrection.DependenceScale(0, 3).Should().Be(1);
        LongRunVarianceCorrection.DependenceScale(-0.3, 3).Should().Be(1);
    }

    [Fact]
    public void Scaling_grows_with_autocorrelation_and_shrinks_with_block_length()
    {
        var a = LongRunVarianceCorrection.DependenceScale(0.3, 3);
        var b = LongRunVarianceCorrection.DependenceScale(0.6, 3);
        var c = LongRunVarianceCorrection.DependenceScale(0.6, 8);
        a.Should().BeGreaterThan(1);
        b.Should().BeGreaterThan(a);
        c.Should().BeLessThan(b);
        LongRunVarianceCorrection.DependenceScale(0.5, 3).Should().BeApproximately(Math.Sqrt(3 / 1.8333333333333333), 1e-12);
    }

    [Fact]
    public void Student_t_widening_decreases_with_degrees_of_freedom()
    {
        var small = LongRunVarianceCorrection.StudentTWidening(5, 0.95);
        var large = LongRunVarianceCorrection.StudentTWidening(100, 0.95);
        small.Should().BeApproximately(2.5706 / 1.95996, 1e-3);
        large.Should().BeGreaterThan(1).And.BeLessThan(small);
    }
}
