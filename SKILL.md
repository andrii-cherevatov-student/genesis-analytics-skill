---
name: wikipedia-interest-analysis
description: Analyze interest in a topic across Wikipedia language editions (growth trend with 95% CI, seasonality, anomalies, robustness) from Wikimedia pageviews. Use for questions like "is interest in X growing in Polish vs Czech Wikipedia?", "how reliable is that growth?", or "which language audiences show stronger signals for X?". Produces analysis.json, pageviews.csv, trend.png and an optional one-page PDF.
---

# Wikipedia Interest Analysis

A deterministic CLI does **all** the statistics. Your job: pick the parameters, run one command, and explain `analysis.json`. **Never compute numbers yourself.**

## 1. Extract parameters

| Parameter | How |
|---|---|
| `--topic` | The concept in English (e.g. `"intermittent fasting"`), even when the user writes in another language. |
| `--languages` | Wikipedia codes: Polish `pl`, Czech `cs`, Ukrainian `uk`, German `de`, French `fr`, Spanish `es`, English `en`… (1–5). |
| Period | "last two years" → `--last-months 24`; explicit dates → `--from YYYY-MM-DD --to YYYY-MM-DD`. Default: `--last-months 24`. |
| `--report pdf` | Only when the user asks for a report/PDF. |
| `--exclude YYYY-MM` | Only when the user asks "what if we exclude the … spike". |
| `--article xx="Title"` | Only when the user names a specific article, or after the user picks one from the candidates shown by an `UnresolvableArticle` error. |

## 2. Run

`<skill-dir>` is the directory containing this SKILL.md. Always call the script by its **full path** and keep the output in the current working directory:

```bash
"<skill-dir>/scripts/wiki-interest.sh" analyze --topic "intermittent fasting" --languages pl cs --last-months 24 --output ./wiki-interest-output
```

In Windows cmd/PowerShell use `<skill-dir>\scripts\wiki-interest.cmd` with the same arguments (Git Bash can use the `.sh`). The first run may build the tool (≈30 s); later runs take a few seconds. Use a new `--output` folder only if the user asks to keep earlier results.

- Exit code 0 → results in `<output>/analysis.json` (plus `pageviews.csv`, `trend.png`, `trend_raw.png`, `report.pdf` if requested).
- Exit code 1 → stdout line `ERROR (<Code>): message`. Report it to the user; fix arguments only if the message says how.
- Warning `PdfMissingGlyphs` or `PdfGenerationFailed` → the analysis is valid; tell the user the PDF shows some characters blank (or was not produced), and point to `analysis.json`/`trend.png` instead.
- `status: "partial"` → some languages failed (`failures[]`). Report them. For `UnresolvableArticle`, show the user the candidate articles in the message and ask which (if any) is the same concept. **Never pick one yourself.**

## 3. Interpret (read `analysis.json`)

Quote the numbers exactly as they appear in `results[].trend`, `results[].seasonality`, `results[].robustness` and `comparison`. The `summary`, `trend.interpretation` and `robustness.statement` fields already contain correct wording.

| `trend.classification` | Meaning | Say |
|---|---|---|
| Increasing | whole 95% CI > +threshold | "growing by about X%/yr (95% CI a–b)" |
| Decreasing | whole 95% CI < −threshold | "declining by about X%/yr (95% CI a–b)" |
| Stable | whole CI inside ±threshold | "no meaningful long-term change" |
| Inconclusive | CI crosses the threshold | "the data cannot tell". **Never** call this stable, growing or declining, even if the point estimate is large. |

- **Trend ≠ seasonality.** A Stable or Inconclusive trend with Moderate/Strong seasonality still means traffic varies within the year (e.g. "September is typically +35%"). Mention both.
- **Reliability** = `robustness.statement` + `robustness.concerns`. Mention every concern with severity `Serious` or `Caution`.
- **Anomalies** are kept in the data. Mention the largest ones and offer the `--exclude` follow-up.
- **Article mapping:** state which article was used per language (`article.articleTitle`). Flag any confidence other than `High`.
- **Comparison:** use `comparison.statement` and `pairwiseDifferences[].distinguishable`. If two growth rates are not distinguishable, do not rank them as different.

## 4. Mandatory caveats (always include the first one)

1. Wikipedia pageviews measure informational attention, **not** market size, willingness to pay or demand. Never recommend launching a product on this basis; say which audiences "show stronger signals worth researching further".
2. Absolute views reflect the size of each language edition. Compare growth rates, not raw traffic.
3. Include any other entries of `caveats[]` that apply.

## 5. Follow-up requests

Re-run with the previous parameters (see `request.rerunCommand` in the last `analysis.json`), changing only what the user asked. Cached data is reused automatically.

- "Add Ukrainian" → same topic and period, `--languages pl cs uk`.
- "Last three years" → `--last-months 36`.
- "Exclude the April spike" → add `--exclude 2025-04` (use the month from `anomalies[]`).
- "Generate a PDF" → add `--report pdf` (optionally `--notes "<2–3 sentence interpretation>"`, printed as labelled analyst notes).

Methodology details are in `docs/METHODOLOGY.md`, for reference only. Do not reproduce the calculations.
