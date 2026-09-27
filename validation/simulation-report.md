# Monte Carlo validation

Generated 2026-09-27 18:07 UTC · 200 replications per scenario · 2000 bootstrap replicates · block length 3 · δ = 0.05 · seed 12345

Model: log V_t = α + β t + S_t + ε_t, ε AR(1); optional one-month spikes (+1.0 log ≈ ×2.7). Reporting window = last N months; 24 months of history before it.

## Acceptance

| Metric | Value | Target | Result | Scope |
|---|---|---|---|---|
| 95% CI empirical coverage | 0.934 | 0.92–0.97 | PASS | all scenarios |
| Flat-series directional false-positive rate | 0.000 | < 0.10 | PASS | true growth = 0 |
| Strong-trend direction accuracy (point estimate) | 1.000 | > 0.90 | PASS | |true growth| ≥ 20% |
| Strong-trend direction accuracy (classification) | 0.963 | reported | PASS | |true growth| ≥ 20% |
| Wrong-direction classification rate | 0.000 | < 0.025 | PASS | all scenarios |
| Annualized growth MAE | 0.034 | reported | PASS | all scenarios |
| Seasonal effect MAE (log scale) | 0.038 | < 0.05 | PASS | seasonal scenarios |

## Scenarios

| Scenario | Truth | Growth MAE | Bias | CI coverage | Mean CI width | Accuracy | Directional FP | Wrong dir. | Inconclusive | Seasonal MAE |
|---|---|---|---|---|---|---|---|---|---|---|
| flat, seasonal, AR(0.5) | Stable | 0.040 | -0.002 | 0.955 | 0.218 | 0.005 | 0.000 | 0.000 | 0.995 | 0.040 |
| flat, white noise | Stable | 0.031 | -0.002 | 0.915 | 0.151 | 0.020 | 0.000 | 0.000 | 0.980 | 0.043 |
| +30%, seasonal, spikes | Increasing | 0.048 | 0.001 | 0.945 | 0.247 | 0.965 | – | 0.000 | 0.035 | 0.045 |
| -25%, AR(0.5) | Decreasing | 0.031 | -0.002 | 0.915 | 0.159 | 0.960 | – | 0.000 | 0.040 | 0.039 |
| +10%, 36 months | Increasing | 0.018 | 0.000 | 0.940 | 0.096 | 0.600 | – | 0.000 | 0.400 | 0.029 |
