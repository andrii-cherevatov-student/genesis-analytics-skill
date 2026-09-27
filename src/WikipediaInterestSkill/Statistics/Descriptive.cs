namespace WikipediaInterestSkill.Statistics;

public static class Descriptive
{
    public static double SampleVariance(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
    }

    public static double SampleStandardDeviation(IReadOnlyList<double> values) => Math.Sqrt(SampleVariance(values));

    public static double Fraction<T>(IReadOnlyCollection<T> items, Func<T, bool> predicate, double whenEmpty = 1.0) =>
        items.Count == 0 ? whenEmpty : items.Count(predicate) / (double)items.Count;
}
