using System.Globalization;
using System.Text;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.TimeSeries;

namespace WikipediaInterestSkill.Reporting;

public static class CsvWriter
{
    public const string Header =
        "language,article,month,in_reporting_period,status,excluded,observed_views,days_with_data,days_in_month," +
        "adjusted_views,log_value,trend_views,seasonal_effect,remainder,expected_views,trend_index,observed_index";

    internal static void Write(string path, IReadOnlyList<LanguageAnalysis> analyses)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var a in analyses)
        {
            var s = a.Slice;
            var d = a.Fit.Decomposition;
            for (var i = 0; i < s.Months.Count; i++)
            {
                var o = s.Observations[i];
                var inWindow = i >= s.WindowStart && i <= s.WindowEnd;
                var hasValue = !a.Fit.Imputed[i];
                sb.AppendJoin(',',
                    a.Result.Language,
                    Escape(a.Result.Article.ArticleTitle),
                    s.Months[i].ToString(),
                    inWindow ? "true" : "false",
                    o.Status.ToString(),
                    s.Excluded.Contains(i) ? "true" : "false",
                    o.ObservedViews.ToString(CultureInfo.InvariantCulture),
                    o.DaysWithData.ToString(CultureInfo.InvariantCulture),
                    o.DaysInMonth.ToString(CultureInfo.InvariantCulture),
                    o.AdjustedViews is { } adj ? F(adj, "0.##") : "",
                    hasValue ? F(a.Fit.LogValues[i], "0.######") : "",
                    F(LogTransformer.Inverse(d.Trend[i]), "0.#"),
                    F(Math.Exp(d.Seasonal[i]) - 1, "0.####"),
                    hasValue ? F(d.Remainder[i], "0.######") : "",
                    F(LogTransformer.Inverse(d.Trend[i] + d.Seasonal[i]), "0.#"),
                    F(a.Fit.TrendIndex(d.Trend[i]), "0.##"),
                    hasValue ? F(a.Fit.TrendIndex(a.Fit.LogValues[i]), "0.##") : "");
                sb.AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string F(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
