using System.Globalization;
using System.Text;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Seasonality;
using WikipediaInterestSkill.Validation;

namespace WikipediaInterestSkill.Reporting;

internal static class ReportText
{
    public static string Seasonality(SeasonalityResult s, bool includeLow)
    {
        if (s.Category == SeasonalityCategory.Negligible) return $"{s.Category} ({s.Strength:0.00})";
        var peak = $"peak {s.PeakMonthName} {Format.Pct0(s.PeakUplift ?? 0)}";
        return includeLow
            ? $"{s.Category} ({peak}, low {s.LowMonthName} {Format.Pct0(s.LowDrop ?? 0)})"
            : $"{s.Category} ({s.PeakMonthName} {Format.Pct0(s.PeakUplift ?? 0)})";
    }

    public static string Robustness(RobustnessResult r)
    {
        var material = r.Concerns.Material().Count();
        return material == 0 ? "No concerns" : $"{material} concern(s)";
    }
}

public static class ConsoleSummary
{
    public static string Render(AnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"wiki-interest: analysis complete (status: {r.Status})");
        sb.AppendLine($"Topic: {r.Topic} | Period analysed: {r.ReportingPeriod.FirstMonth}..{r.ReportingPeriod.LastMonth} " +
                      $"({r.ReportingPeriod.Months} complete months) | threshold ±{Format.Threshold(r.Request.PracticalTrendThreshold)}/yr");
        foreach (var x in r.Results)
        {
            var t = x.Trend;
            sb.AppendLine($"[{x.Language}] {x.Article.ArticleTitle} ({x.Article.Method}, {x.Article.Confidence}): {t.Classification} " +
                          $"{Format.Pct(t.AnnualizedGrowth)}/yr (95% CI {Format.Pct(t.ConfidenceInterval95.Lower)} to " +
                          $"{Format.Pct(t.ConfidenceInterval95.Upper)}) | last 12 months {x.Volume.Last12MonthsViews?.ToString("N0") ?? "n/a"} views | " +
                          $"seasonality {ReportText.Seasonality(x.Seasonality, includeLow: true)} | {x.Anomalies.Count} anomalies | " +
                          $"robustness: {ReportText.Robustness(x.Robustness).ToLowerInvariant()}");
        }

        foreach (var f in r.Failures) sb.AppendLine($"[{f.Language}] NOT ANALYSED ({f.ErrorCode}): {f.Message}");
        if (r.Comparison is { } c) sb.AppendLine($"Comparison: {c.Statement}");
        foreach (var w in r.Warnings.Where(w => w.Severity != WarningSeverity.Info && w.Code != WarningCodes.LanguageFailed))
            sb.AppendLine($"Warning ({w.Code}): {w.Message}");
        sb.AppendLine($"Artifacts: {r.Artifacts.AnalysisJson}");
        sb.AppendLine($"           {r.Artifacts.PageviewsCsv}");
        sb.AppendLine($"           {r.Artifacts.TrendChart}");
        if (r.Artifacts.Pdf is { } pdf) sb.AppendLine($"           {pdf}");
        sb.Append("Use analysis.json as the source of truth: quote its numbers and caveats; do not recompute.");
        return sb.ToString();
    }
}

public static class SimulationMarkdown
{
    public static string RenderAcceptance(SimulationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Metric | Value | Target | Result | Scope |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var a in report.Acceptance)
            sb.AppendLine($"| {a.Metric} | {a.Value.ToString("0.000", CultureInfo.InvariantCulture)} | {a.Target} | {(a.Passed ? "PASS" : "FAIL")} | {a.Scope} |");
        return sb.ToString();
    }

    public static string Render(SimulationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Monte Carlo validation");
        sb.AppendLine();
        sb.AppendLine($"Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC · {report.ReplicationsPerScenario} replications per scenario · " +
                      $"{report.BootstrapIterations} bootstrap replicates · block length {report.BlockLength} · δ = {report.PracticalThreshold} · seed {report.Seed}");
        sb.AppendLine();
        sb.AppendLine("Model: log V_t = α + β t + S_t + ε_t, ε AR(1); optional one-month spikes (+1.0 log ≈ ×2.7). " +
                      "Reporting window = last N months; 24 months of history before it.");
        sb.AppendLine();
        sb.AppendLine("## Acceptance");
        sb.AppendLine();
        sb.Append(RenderAcceptance(report));
        sb.AppendLine();
        sb.AppendLine("## Scenarios");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Truth | Growth MAE | Bias | CI coverage | Mean CI width | Accuracy | Directional FP | Wrong dir. | Inconclusive | Seasonal MAE |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in report.Scenarios)
            sb.AppendLine($"| {s.Scenario.Name} | {s.TrueClassification} | {F(s.GrowthMae)} | {F(s.GrowthBias)} | {F(s.CiCoverage)} | {F(s.MeanCiWidth)} | " +
                          $"{F(s.ClassificationAccuracy)} | {F(s.DirectionalFalsePositiveRate)} | {F(s.WrongDirectionRate)} | {F(s.InconclusiveRate)} | {F(s.SeasonalityMae)} |");
        return sb.ToString();
    }

    private static string F(double v) => double.IsNaN(v) ? "–" : v.ToString("0.000", CultureInfo.InvariantCulture);
}

public static class RealDataMarkdown
{
    public static string Render(RealDataReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Real-data robustness validation");
        sb.AppendLine();
        sb.AppendLine($"Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC. {report.Note}");
        sb.AppendLine();
        sb.AppendLine("| Case | Pattern | Article | Class | Growth/yr | 95% CI | F_S | Anom. | LOO max shift | LOO class agr. | Window dir. agr. | Window class agr. | Estimator agr. | Resid. r1 | Ljung-Box p | Rolling dir. agr. | Rolling class agr. | Rolling spread |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var c in report.Cases)
        {
            if (c.Error is not null)
            {
                sb.AppendLine($"| {c.Case.Id} | {c.Case.Category} | {c.Case.Language}:{c.Case.Article} | ERROR: {c.Error} |||||||||||||||");
                continue;
            }

            sb.AppendLine($"| {c.Case.Id} | {c.Case.Category} | {c.Case.Language}:{c.Case.Article} | {c.Classification} | {P(c.AnnualizedGrowth)} | " +
                          $"{P(c.CiLower)} … {P(c.CiUpper)} | {F(c.SeasonalityStrength)} | {c.Anomalies} | {P(c.LeaveOneOutMaxShift)} | " +
                          $"{F(c.LeaveOneOutClassificationAgreement)} | {F(c.WindowDirectionAgreement)} | {F(c.WindowClassificationAgreement)} | " +
                          $"{F(c.EstimatorDirectionAgreement)} | {F(c.ResidualLag1)} | {F(c.LjungBoxPValue)} | {F(c.RollingOriginDirectionAgreement)} | " +
                          $"{F(c.RollingOriginClassificationAgreement)} | {P(c.RollingOriginGrowthSpread)} |");
        }

        return sb.ToString();
    }

    private static string F(double? v) => v is { } x ? x.ToString("0.00", CultureInfo.InvariantCulture) : "–";
    private static string P(double? v) => v is { } x ? Format.Pct(x) : "–";
}
