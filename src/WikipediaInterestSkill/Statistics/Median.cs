namespace WikipediaInterestSkill.Statistics;

public static class Median
{
    public static double OfInPlace(double[] values)
    {
        var n = values.Length;
        if (n == 0) throw new ArgumentException("Median of an empty sequence.");
        var hi = Select(values, 0, n - 1, n / 2);
        if (n % 2 == 1) return hi;
        var lo = double.NegativeInfinity;
        for (var i = 0; i < n / 2; i++) if (values[i] > lo) lo = values[i];
        return 0.5 * (lo + hi);
    }

    public static double Of(IEnumerable<double> values) => OfInPlace(values.ToArray());

    public static double AbsoluteDeviation(IReadOnlyList<double> values, out double median)
    {
        median = Of(values);
        var m = median;
        return Of(values.Select(v => Math.Abs(v - m)));
    }

    public static double QuantileSorted(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) throw new ArgumentException("Quantile of an empty sequence.");
        if (sorted.Count == 1) return sorted[0];
        var h = (sorted.Count - 1) * p;
        var lo = (int)Math.Floor(h);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
    }

    private static double Select(double[] a, int left, int right, int k)
    {
        while (true)
        {
            if (left == right) return a[left];
            var pivotIndex = left + (right - left) / 2;
            var pivot = a[pivotIndex];
            (a[pivotIndex], a[right]) = (a[right], a[pivotIndex]);
            var store = left;
            for (var i = left; i < right; i++)
            {
                if (a[i] < pivot)
                {
                    (a[store], a[i]) = (a[i], a[store]);
                    store++;
                }
            }

            (a[right], a[store]) = (a[store], a[right]);
            if (k == store) return a[store];
            if (k < store) right = store - 1;
            else left = store + 1;
        }
    }
}
