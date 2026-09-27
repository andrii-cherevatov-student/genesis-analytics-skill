namespace WikipediaInterestSkill.Validation;

public sealed record SyntheticScenario
{
    public string Name { get; init; } = "scenario";

    public double AnnualGrowth { get; init; }

    public double NoiseSd { get; init; } = 0.1;

    public double ArPhi { get; init; }

    public double SeasonalAmplitude { get; init; }

    public int Spikes { get; init; }

    public double SpikeSize { get; init; } = 1.0;

    public int ReportingMonths { get; init; } = 24;

    public int HistoryMonths { get; init; } = 24;

    public double BaseMonthlyViews { get; init; } = 20000;
}

public sealed record SyntheticSeries(
    double[] Views,
    double[] TrueSeasonal,
    int WindowStart,
    int WindowEnd,
    int FirstCalendarMonth,
    double TrueMonthlySlope);

public static class SyntheticSeriesGenerator
{
    public static SyntheticSeries Generate(SyntheticScenario s, Random rng)
    {
        var n = s.ReportingMonths + s.HistoryMonths;
        var beta = TimeSeries.LogTransformer.MonthlyLogSlopeFromAnnualGrowth(s.AnnualGrowth);
        var alpha = Math.Log(s.BaseMonthlyViews);
        var firstMonth = rng.Next(1, 13);

        var phase1 = rng.NextDouble() * 2 * Math.PI;
        var phase2 = rng.NextDouble() * 2 * Math.PI;
        var shape = new double[12];
        for (var m = 0; m < 12; m++)
            shape[m] = Math.Sin(2 * Math.PI * m / 12 + phase1) + 0.4 * Math.Sin(4 * Math.PI * m / 12 + phase2);
        var shapeMean = shape.Average();
        for (var m = 0; m < 12; m++) shape[m] -= shapeMean;
        var shapeMax = shape.Max(Math.Abs);
        for (var m = 0; m < 12; m++) shape[m] = shapeMax > 0 ? shape[m] / shapeMax * s.SeasonalAmplitude : 0;

        var innovationSd = s.NoiseSd * Math.Sqrt(1 - s.ArPhi * s.ArPhi);
        var eps = s.NoiseSd * Gaussian(rng);
        var views = new double[n];
        var seasonal = new double[n];
        var windowStart = s.HistoryMonths;
        var spikes = Enumerable.Range(windowStart, s.ReportingMonths).OrderBy(_ => rng.Next()).Take(s.Spikes).ToArray();
        for (var t = 0; t < n; t++)
        {
            if (t > 0) eps = s.ArPhi * eps + innovationSd * Gaussian(rng);
            seasonal[t] = shape[(firstMonth - 1 + t) % 12];
            var log = alpha + beta * t + seasonal[t] + eps + (spikes.Contains(t) ? s.SpikeSize : 0);
            views[t] = Math.Round(Math.Exp(log));
        }

        return new SyntheticSeries(views, seasonal, windowStart, n - 1, firstMonth, beta);
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
