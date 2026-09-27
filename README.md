# Wikipedia Interest Analysis Skill

This is a self-contained agent skill with a deterministic .NET 8 CLI (`wiki-interest`). It measures interest in a topic across Wikipedia language editions and reports:

- the trend, with a 95% CI and a classification
- seasonality
- anomalies
- robustness diagnostics
- a cross-language comparison

Agents should follow [`SKILL.md`](SKILL.md). The statistics are described in [`docs/METHODOLOGY.md`](docs/METHODOLOGY.md).

## Quick start

```bash
scripts/wiki-interest.sh analyze --topic "astronomy" --languages uk pl cs --last-months 24 --report pdf --output ./out
# Windows: scripts\wiki-interest.cmd analyze ...
```

This writes `out/analysis.json` (the source of truth), `pageviews.csv`, `trend.png`, `trend_raw.png`, `report.pdf` and `run.log.jsonl` (structured logs).

| Option | Meaning |
|---|---|
| `--topic`, `--languages` | Concept (in English by default) and Wikipedia language codes |
| `--from/--to` or `--last-months N` | Reporting period; only complete months are analysed |
| `--report pdf`, `--notes "..."` | One-page PDF, with optional labelled agent notes |
| `--trend-threshold 0.05` | Practical threshold δ for Stable/Increasing/Decreasing |
| `--exclude 2025-04` | Sensitivity analysis without a month (repeatable) |
| `--article pl="Title"` | Explicit article, as a title or URL (repeatable) |
| `--allow-search-fallback` | Analyse the best search hit when no interlanguage link exists (Low confidence) |
| `-v` | Logs to stderr |

Exit codes:

- `0`: success, including partial results (`status: "partial"`, with per-language `failures`).
- `1`: error. Stdout prints `ERROR (<Code>): …` and `error.json` is written. Codes: `UnknownLanguage`, `UnresolvableArticle`, `InsufficientData`, `InvalidDateRange`, `WikimediaApiFailure`, `InvalidOutputDirectory`, `InvalidArgument`.

Development diagnostics:

- `wiki-interest simulate [--grid quick|full] [-n 200]`: Monte Carlo validation.
- `wiki-interest validate`: real-data robustness suite.

## Layout

```
SKILL.md                      agent instructions
scripts/                      build-on-demand wrappers (sh, ps1, cmd) → .build/
src/WikipediaInterestSkill/   CLI: Application, Wikipedia, TimeSeries (STL), Statistics, Seasonality,
                              Anomalies, Robustness, Reporting (JSON/CSV/ScottPlot/QuestPDF), Caching (SQLite), Validation
tests/                        UnitTests (83 tests); IntegrationTests / SimulationTests are scaffolded but empty
validation/                   real-data cases and generated validation reports
evals/                        agent-evals.jsonl, fixtures/, run-agent-evals.ps1 (headless Claude Code runner)
docs/METHODOLOGY.md           methods, validation results, performance
```

Requirements: .NET 8 SDK and network access to `*.wikipedia.org` and `wikimedia.org`. Set `WIKI_INTEREST_CONTACT` to add contact details to the User-Agent, as Wikimedia's API policy recommends.

QuestPDF is used under its Community licence. PDF fonts are bundled, because QuestPDF ≥ 2026.9 ignores system fonts: Lato covers Latin and Cyrillic, and the embedded Noto Sans Arabic (SIL OFL, `src/WikipediaInterestSkill/Fonts/`) covers Arabic script. Other scripts (e.g. CJK, Hebrew, Devanagari) are left blank in the PDF with a `PdfMissingGlyphs` warning. To support one, add its Noto `.ttf` to `Fonts/` and its family name to `PdfReportGenerator.FontFamilies`.

## Agent evaluation

The skill is installed for Claude Code through a junction: `~/.claude/skills/wikipedia-interest-analysis` → this folder. There are two ways to test it.

**Interactively:** start `claude --model haiku` in any empty folder and ask, for example, "Is interest in astronomy growing in Ukrainian Wikipedia, and how much can we trust that?"

**Automated:** run the suite, which covers the nine cases in `evals/agent-evals.jsonl`. They are the PRD scenarios: comparison, reliability, overstatement, follow-ups (add a language, exclude a spike, PDF), no launch advice, and two fixture-based interpretation cases.

```
powershell -ExecutionPolicy Bypass -File evals\run-agent-evals.ps1              # all cases, Haiku
powershell -ExecutionPolicy Bypass -File evals\run-agent-evals.ps1 -Filter pdf  # subset by id
powershell -ExecutionPolicy Bypass -File evals\run-agent-evals.ps1 -Model sonnet
```

Each case runs headless Claude Code in its own folder under `evals/runs/<timestamp>/`. Grading checks three things:

- the parameters the agent actually passed, read back from the `analysis.json` it produced;
- that the growth numbers it quotes match `analysis.json`;
- required and forbidden phrasing in the final answer.

Regex checks only screen the answers; read `results.md` for the full text. A full run costs about $0.50 with Haiku.

## Status against the PRDs

Not yet done: the integration and simulation test suites. Their projects are scaffolded but contain no tests; the simulation *metrics* are available through `wiki-interest simulate`.
