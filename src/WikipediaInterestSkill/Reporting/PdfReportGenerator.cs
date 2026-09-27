using System.Text;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Seasonality;
using WikipediaInterestSkill.Statistics;

namespace WikipediaInterestSkill.Reporting;

public static class PdfReportGenerator
{
    private const string Accent = "#1e3a8a";
    private const string Muted = "#6b7280";

    public static readonly string[] FontFamilies = { "Lato", "Noto Sans Arabic" };

    private static readonly (int First, int Last)[] LatoCoverage =
    {
        (0x0000, 0x036F), (0x0370, 0x03FF), (0x0400, 0x052F), (0x1E00, 0x1EFF), (0x2000, 0x206F), (0x20A0, 0x20CF),
        (0x2100, 0x214F), (0x2190, 0x21FF), (0x2200, 0x22FF),
    };

    private static readonly SkiaSharp.SKTypeface[] EmbeddedTypefaces;

    public sealed record PdfOutcome(bool MissingGlyphs, string? Detail);

    static PdfReportGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        var assembly = typeof(PdfReportGenerator).Assembly;
        var typefaces = new List<SkiaSharp.SKTypeface>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var resource = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            FontManager.RegisterFontFromStream(new MemoryStream(bytes));
            typefaces.Add(SkiaSharp.SKTypeface.FromData(SkiaSharp.SKData.CreateCopy(bytes)));
        }

        EmbeddedTypefaces = typefaces.ToArray();
    }

    public static void Initialize() =>
        Document.Create(c => c.Page(p => p.Content().Text("warm-up"))).GeneratePdf();

    public static PdfOutcome Write(string path, AnalysisResult result, byte[] chartPng, string? agentInterpretation)
    {
        Render(path, result, chartPng, agentInterpretation);
        var missing = MissingCharacters(JsonResultWriter.Serialize(result) + agentInterpretation);
        return missing.Count == 0
            ? new PdfOutcome(false, null)
            : new PdfOutcome(true, $"Characters without a font: {string.Join(" ", missing.Take(10))}.");
    }

    internal static IReadOnlyList<string> MissingCharacters(string text) =>
        text.EnumerateRunes()
            .Where(r => !Rune.IsControl(r) && !IsCovered(r.Value))
            .Select(r => r.ToString())
            .Distinct()
            .ToList();

    private static bool IsCovered(int codepoint) =>
        LatoCoverage.Any(range => codepoint >= range.First && codepoint <= range.Last) ||
        EmbeddedTypefaces.Any(t => t.GetGlyph(codepoint) != 0);

    private static void Render(string path, AnalysisResult result, byte[] chartPng, string? agentInterpretation)
    {
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.1f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontFamily(FontFamilies).FontSize(8.2f).FontColor("#111827"));

            page.Header().Column(col =>
            {
                col.Item().Text($"Wikipedia interest analysis: {result.Topic}").FontSize(15).Bold().FontColor(Accent);
                col.Item().Text(t =>
                {
                    t.Span($"Period {result.ReportingPeriod.FirstMonth}–{result.ReportingPeriod.LastMonth} " +
                           $"({result.ReportingPeriod.Months} complete months) · Languages: {string.Join(", ", result.Request.Languages)} · " +
                           $"Practical threshold ±{Format.Threshold(result.Request.PracticalTrendThreshold)}/yr").FontColor(Muted);
                });
            });

            page.Content().PaddingTop(4).ScaleToFit().Column(col =>
            {
                col.Spacing(5);
                col.Item().Element(c => ArticleMapping(c, result));
                col.Item().Image(chartPng).FitWidth();
                col.Item().Element(c => ComparisonTable(c, result));
                col.Item().Element(c => Section(c, "Trend findings",
                    result.Results.Select(r => $"[{r.Language}] {r.Trend.Interpretation}")));
                col.Item().Element(c => Section(c, "Seasonality findings",
                    result.Results.Select(r => $"[{r.Language}] {r.Seasonality.Interpretation}")));
                var anomalyLines = result.Results.Where(r => r.Anomalies.Count > 0).Select(r =>
                    $"[{r.Language}] " + string.Join("; ", r.Anomalies.Take(3).Select(a =>
                        $"{a.Period}: {a.ObservedViews:N0} views vs ~{a.EstimatedBaselineViews:N0} expected (z = {a.RobustZScore:0.0})")) +
                    (r.Anomalies.Count > 3 ? $"; +{r.Anomalies.Count - 3} more" : "") + ". Kept in data; down-weighted in the trend.").ToList();
                if (anomalyLines.Count > 0) col.Item().Element(c => Section(c, "Anomalies", anomalyLines));
                col.Item().Element(c => Section(c, "Robustness",
                    result.Results.Select(r => $"[{r.Language}] {r.Robustness.Statement}")));
                if (result.Comparison is { } comparison)
                    col.Item().Element(c => Section(c, "Comparison", new[] { comparison.Statement, comparison.RankingRule }));
                if (result.Failures.Count > 0)
                    col.Item().Element(c => Section(c, "Not analysed",
                        result.Failures.Select(f => $"[{f.Language}] {f.ErrorCode}: {f.Message}")));
                if (!string.IsNullOrWhiteSpace(agentInterpretation))
                    col.Item().Background("#f3f4f6").Padding(5).Column(c =>
                    {
                        c.Item().Text("Analyst notes (written by the AI agent from analysis.json; not computed by the engine)")
                            .Bold().FontSize(8).FontColor(Muted);
                        c.Item().Text(agentInterpretation.Trim());
                    });
                col.Item().Element(c => Section(c, "Limitations", result.Caveats, Muted));
            });

            page.Footer().AlignCenter().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(6.5f).FontColor(Muted));
                t.Span($"Generated by {result.Tool.Name} {result.Tool.Version} on {result.GeneratedAt:yyyy-MM-dd HH:mm} UTC from analysis.json · " +
                       "Data: Wikimedia REST pageviews API (user agents) · Robust STL + Theil-Sen + moving-block bootstrap");
            });
        })).GeneratePdf(path);
    }

    private static void ArticleMapping(IContainer container, AnalysisResult result)
    {
        container.Column(col =>
        {
            col.Item().Text("Article mapping").Bold().FontColor(Accent);
            col.Item().Text(t =>
            {
                foreach (var r in result.Results)
                {
                    t.Span($"{r.Language}: ").Bold();
                    t.Span($"{r.Article.ArticleTitle} ({r.Article.Method}, {r.Article.Confidence} confidence)   ");
                }
            });
        });
    }

    private static void ComparisonTable(IContainer container, AnalysisResult result)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(0.7f);
                c.RelativeColumn(1.2f);
                c.RelativeColumn(1f);
                c.RelativeColumn(1.6f);
                c.RelativeColumn(1.1f);
                c.RelativeColumn(1.7f);
                c.RelativeColumn(1.2f);
            });
            table.Header(h =>
            {
                foreach (var title in new[] { "Lang", "Views (last 12 mo)", "Growth / yr", "95% CI", "Trend", "Seasonality (peak)", "Robustness" })
                    h.Cell().Background("#e5e7eb").Padding(3).Text(title).Bold();
            });
            foreach (var r in result.Results)
            {
                Cell(table, r.Language);
                Cell(table, r.Volume.Last12MonthsViews is { } v ? v.ToString("N0") : $"{r.Volume.TotalViews:N0} total");
                Cell(table, Format.Pct(r.Trend.AnnualizedGrowth));
                Cell(table, $"{Format.Pct(r.Trend.ConfidenceInterval95.Lower)} to {Format.Pct(r.Trend.ConfidenceInterval95.Upper)}");
                Cell(table, r.Trend.Classification.ToString(), ClassColor(r.Trend.Classification));
                Cell(table, ReportText.Seasonality(r.Seasonality, includeLow: false));
                Cell(table, ReportText.Robustness(r.Robustness));
            }
        });
    }

    private static void Cell(TableDescriptor table, string text, string? color = null)
    {
        var t = table.Cell().BorderBottom(0.5f).BorderColor("#e5e7eb").Padding(3).Text(text);
        if (color is not null) t.FontColor(color).Bold();
    }

    private static string ClassColor(TrendClassification c) => c switch
    {
        TrendClassification.Increasing => "#047857",
        TrendClassification.Decreasing => "#b91c1c",
        TrendClassification.Stable => "#1d4ed8",
        _ => "#92400e",
    };

    private static void Section(IContainer container, string title, IEnumerable<string> lines, string? color = null)
    {
        container.Column(col =>
        {
            col.Item().Text(title).Bold().FontColor(Accent);
            foreach (var line in lines)
                col.Item().PaddingLeft(6).Text(t =>
                {
                    t.Span("• ");
                    var span = t.Span(line);
                    if (color is not null) span.FontColor(color);
                });
        });
    }
}
