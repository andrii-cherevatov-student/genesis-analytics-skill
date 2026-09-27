namespace WikipediaInterestSkill.TimeSeries;

public static class LogTransformer
{
    public static double Forward(double views)
    {
        if (views < 0 || double.IsNaN(views)) throw new ArgumentOutOfRangeException(nameof(views));
        return Math.Log(1.0 + views);
    }

    public static double Inverse(double logValue) => Math.Exp(logValue) - 1.0;

    public static double AnnualizeMonthlyLogSlope(double monthlyLogSlope) => Math.Exp(12.0 * monthlyLogSlope) - 1.0;

    public static double MonthlyLogSlopeFromAnnualGrowth(double annualGrowth) => Math.Log(1.0 + annualGrowth) / 12.0;
}
