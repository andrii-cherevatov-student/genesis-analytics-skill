using MathNet.Numerics.Distributions;
using WikipediaInterestSkill.Application;

namespace WikipediaInterestSkill.Statistics;

public sealed record LongRunVarianceCorrection(double Lag1Autocorrelation, double DependenceFactor, double StudentTFactor)
{
    public const double MaxRho = 0.9;

    public double ScaleFactor => DependenceFactor * StudentTFactor;

    public static LongRunVarianceCorrection None { get; } = new(0, 1, 1);

    public static LongRunVarianceCorrection Compute(IReadOnlyList<double> residuals, int blockLength)
    {
        var m = residuals.Count;
        if (m < 6) return None;
        var rho = AutocorrelationAnalyzer.Acf(residuals, 1)[0];
        var corrected = Math.Clamp(rho + (1 + 4 * rho) / m, 0, MaxRho);
        var blocks = Math.Max(2, m / Math.Max(1, blockLength));
        return new LongRunVarianceCorrection(rho, DependenceScale(corrected, blockLength),
            StudentTWidening(blocks - 1, AnalysisSettings.ConfidenceLevel));
    }

    public static double DependenceScale(double rho, int blockLength)
    {
        if (rho <= 0) return 1.0;
        var full = (1 + rho) / (1 - rho);
        var captured = 1.0;
        for (var k = 1; k < blockLength; k++) captured += 2 * (1 - k / (double)blockLength) * Math.Pow(rho, k);
        return Math.Sqrt(Math.Max(1.0, full / captured));
    }

    public static double StudentTWidening(int degreesOfFreedom, double confidenceLevel)
    {
        var p = 1 - (1 - confidenceLevel) / 2;
        return StudentT.InvCDF(0, 1, Math.Max(1, degreesOfFreedom), p) / Normal.InvCDF(0, 1, p);
    }
}
