# Methodology (reference, not agent instructions)

All computation happens in the C# engine. This document explains what it does.

## Pipeline

1. **Resolution.** An explicit `--article` URL or title comes first. Otherwise the engine looks up the English (or `--source-language`) title, following redirects, or falls back to the first non-disambiguation search hit. It then follows the interlanguage (Wikidata) link to each target language. If no link exists, the language fails with `UnresolvableArticle` and lists unverified search candidates, unless `--allow-search-fallback` is given (Low confidence).
2. **Data.** Daily `agent=user` pageviews come from the Wikimedia REST API and are cached in SQLite (`.cache/`): raw daily rows, fetched date ranges and resolutions. Only unfetched ranges are requested, and the last 3 days are always re-fetched.
3. **Monthly aggregation.** The engine computes V_m = Σ V_d. A month with some missing days is scaled by days/available days. A month with less than 50% of its days is treated as missing (imputed for the decomposition only). Missing days are never read as zero.
4. **Transform.** Y = log(1 + V).
5. **Decomposition.** Robust STL (Cleveland et al. 1990, ported from the reference algorithm) runs with period 12, seasonal window 13 (degree 0), trend window 21 and low-pass window 13, using 1 inner and 5 robustness passes. It covers the reporting period plus up to 24 earlier months, which stabilises seasonality and the trend at the start of the period.
6. **Trend.** β̂ is the Theil-Sen slope of the STL trend over the reporting months, and g = exp(12β̂) − 1.
7. **95% CI.** The engine uses a residual moving-block bootstrap: 2000 replicates, block length 3, with a full STL re-decomposition of each replicate Y* = T̂ + Ŝ + e*.
   - Residuals are taken from the deseasonalised series around a Theil-Sen line over the window ± half the trend span. They are inflated by √(n / (n − df_S − 2)).
   - The interval is the basic (pivotal) interval.
   - Two small-sample corrections are applied, and both were chosen by Monte Carlo:
     - AR(1) long-run-variance scaling: √(κ(ρ̃)/κ_l(ρ̃)).
     - Student-t widening: t₀.₉₇₅,df / z₀.₉₇₅, with df = number of blocks − 1.
8. **Classification.** CI lower > +δ gives Increasing. CI upper < −δ gives Decreasing. A CI entirely within ±δ gives Stable. Anything else is Inconclusive (δ = 5% by default).
9. **Supporting test.** Mann-Kendall runs on seasonally adjusted values, with the Hamed-Rao correction when lag-1 autocorrelation is material. It never affects the classification.
10. **Seasonality.** Strength is F_S = max(0, 1 − Var(R)/Var(S+R)). Categories: < 0.2 negligible, < 0.4 weak, < 0.64 moderate, otherwise strong. Monthly uplift is exp(S̄_m) − 1.
11. **Anomalies.** A month is flagged when |robust z| > 3, where z = (R − median R) / (1.4826 · MAD). Flagged months are reported, not removed. `--exclude` treats them as missing for a sensitivity re-run.
12. **Robustness.** Four checks, each reported as numbers plus explicit concerns rather than a single score:
    - Leave-one-out: every month removed in turn. The interval width is reused from the primary bootstrap.
    - Window shifts: start ±3 months and end −3 months, 500 replicates each.
    - Alternative estimators: OLS on deseasonalised logs, year-over-year aggregate growth, and median monthly year-over-year growth.
    - Ljung-Box test on the residuals.
13. **Comparison.** Languages are ranked by the CI lower bound. Pairwise growth differences use replicate-by-replicate differences of the independent bootstraps.

## Validation results (2026-09-27)

`wiki-interest simulate --grid quick -n 200` (2000 replicates) passes every acceptance target (`validation/simulation-report.md`):

| Metric | Value | Target |
|---|---|---|
| 95% CI coverage | 0.934 | 0.92–0.97 |
| Flat-series directional false positives | 0.000 | < 0.10 |
| Strong-trend direction accuracy | 1.000 (point), 0.963 (classification) | > 0.90 |
| Wrong-direction classifications | 0.000 | < 0.025 |
| Seasonal effect MAE (log) | 0.038 | < 0.05 |

The block-length study (block 3, 500 replications per cell, seasonal amplitude 0.3, noise sd 0.1) gave coverage of 0.93–0.95 for AR(1) φ ≤ 0.5. **Known limitation:** with very persistent noise (φ = 0.7), coverage drops to about 0.90.

Before the corrections were added, a naive residual bootstrap (fixed seasonal component, STL remainder) covered only about 74%. That result motivated the full-STL replicates and the two corrections.

`wiki-interest validate` runs the real-data suite (`validation/real-data-cases.json`, report in `validation/real-data-report.md`). No ground truth is assigned to real series.

## Performance (12-core Windows machine)

| Run | Measured | Target |
|---|---|---|
| 5 languages, uncached, with PDF | ≈ 4.1 s | < 5 s |
| 5 languages, cached | ≈ 1.8 s | < 2 s |
| 2 languages, uncached | ≈ 2.9 s | < 3 s |

The .NET process start adds about 0.3 s on top of these figures.
