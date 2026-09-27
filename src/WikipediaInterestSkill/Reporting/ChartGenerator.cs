using ScottPlot;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.TimeSeries;

namespace WikipediaInterestSkill.Reporting;

public static class ChartGenerator
{
    public const int Width = 1400;
    public const int Height = 700;

    private static readonly string[] Palette =
        { "#2563eb", "#dc2626", "#059669", "#d97706", "#7c3aed", "#0891b2", "#db2777", "#65a30d", "#475569", "#b45309" };

    internal static byte[] TrendChart(string topic, IReadOnlyList<LanguageAnalysis> analyses)
    {
        if (analyses.Count == 1) return RawChart(topic, analyses);

        var plt = NewPlot();
        AddSeries(plt, analyses, (a, logValue) => a.Fit.TrendIndex(logValue),
            a => $"{a.Result.Language}: {a.Result.Article.ArticleTitle} ({a.Result.Trend.Classification}, " +
                 $"{Format.Pct(a.Result.Trend.AnnualizedGrowth)}/yr)");
        var baseline = plt.Add.HorizontalLine(100);
        baseline.Color = Colors.Gray.WithAlpha(0.6);
        baseline.LinePattern = LinePattern.Dashed;
        baseline.LineWidth = 1;
        plt.Title($"Wikipedia interest in \"{topic}\": normalized index (underlying trend in first month = 100)");
        plt.YLabel("Index (thin: observed monthly views, thick: underlying trend)");
        return Render(plt);
    }

    internal static byte[] RawChart(string topic, IReadOnlyList<LanguageAnalysis> analyses)
    {
        var logScale = analyses.Count > 1;
        var plt = NewPlot();
        AddSeries(plt, analyses, (_, logValue) => Transform(LogTransformer.Inverse(logValue), logScale),
            a => $"{a.Result.Language}: {a.Result.Article.ArticleTitle} — underlying trend");
        plt.Axes.Left.TickGenerator = logScale
            ? new ScottPlot.TickGenerators.NumericAutomatic
            {
                MinorTickGenerator = new ScottPlot.TickGenerators.LogMinorTickGenerator(),
                IntegerTicksOnly = true,
                LabelFormatter = y => $"{Math.Pow(10, y):N0}",
            }
            : new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = y => $"{y:N0}" };
        plt.Title($"Wikipedia monthly views: \"{topic}\"" + (logScale ? " (log scale)" : ""));
        plt.YLabel("Monthly views (points: observed, line: underlying trend)");
        return Render(plt);
    }

    private static void AddSeries(
        Plot plt,
        IReadOnlyList<LanguageAnalysis> analyses,
        Func<LanguageAnalysis, double, double> transform,
        Func<LanguageAnalysis, string> legend)
    {
        for (var k = 0; k < analyses.Count; k++)
        {
            var a = analyses[k];
            var color = Color.FromHex(Palette[k % Palette.Length]);
            var (xs, observed, trend) = Series(a);
            var obs = observed.Select(v => double.IsNaN(v) ? double.NaN : transform(a, v)).ToArray();
            AddObserved(plt, xs, obs, color);
            var line = plt.Add.ScatterLine(xs, trend.Select(v => transform(a, v)).ToArray());
            line.Color = color;
            line.LineWidth = 3;
            line.LegendText = legend(a);
            AddAnomalies(plt, a, xs, obs, color);
        }
    }

    private static Plot NewPlot()
    {
        var plt = new Plot();
        plt.FigureBackground.Color = Colors.White;
        plt.DataBackground.Color = Colors.White;
        plt.Grid.MajorLineColor = Colors.Black.WithAlpha(0.08);
        return plt;
    }

    private static byte[] Render(Plot plt)
    {
        var dateAxis = plt.Axes.DateTimeTicksBottom();
        if (dateAxis.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic auto)
            auto.LabelFormatter = dt => dt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        plt.Legend.Alignment = Alignment.UpperLeft;
        plt.Legend.FontSize = 13;
        plt.ShowLegend();
        plt.Font.Automatic();
        plt.Axes.AutoScale();
        return plt.GetImage(Width, Height).GetImageBytes(ImageFormat.Png, 100);
    }

    private static double Transform(double v, bool log) => log ? Math.Log10(Math.Max(1, v)) : v;

    private static (double[] Xs, double[] Observed, double[] Trend) Series(LanguageAnalysis a)
    {
        var s = a.Slice;
        var n = s.WindowLength;
        var xs = new double[n];
        var observed = new double[n];
        var trend = new double[n];
        for (var k = 0; k < n; k++)
        {
            var i = s.WindowStart + k;
            xs[k] = s.Months[i].FirstDay.ToDateTime(TimeOnly.MinValue).AddDays(14).ToOADate();
            observed[k] = a.Fit.Imputed[i] && !s.Excluded.Contains(i) ? double.NaN : ObservedLog(a, i);
            trend[k] = a.Fit.Decomposition.Trend[i];
        }

        return (xs, observed, trend);
    }

    private static double ObservedLog(LanguageAnalysis a, int i) =>
        a.Slice.AdjustedViews[i] is { } v ? LogTransformer.Forward(v) : double.NaN;

    private static void AddObserved(Plot plt, double[] xs, double[] ys, Color color)
    {
        var pts = Enumerable.Range(0, xs.Length).Where(i => !double.IsNaN(ys[i])).ToArray();
        if (pts.Length == 0) return;
        var sc = plt.Add.Scatter(pts.Select(i => xs[i]).ToArray(), pts.Select(i => ys[i]).ToArray());
        sc.Color = color.WithAlpha(0.55);
        sc.LineWidth = 1;
        sc.MarkerSize = 5;
    }

    private static void AddAnomalies(Plot plt, LanguageAnalysis a, double[] xs, double[] ys, Color color)
    {
        foreach (var anomaly in a.Result.Anomalies)
        {
            var k = anomaly.Period.MonthsSince(a.Slice.FirstWindowMonth);
            if (k < 0 || k >= xs.Length || double.IsNaN(ys[k])) continue;
            var m = plt.Add.Marker(xs[k], ys[k]);
            m.Shape = MarkerShape.OpenCircle;
            m.Size = 18;
            m.Color = color;
            m.LineWidth = 2;
        }
    }
}
