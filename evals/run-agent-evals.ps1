param(
    [string]$Model = "haiku",
    [string]$Filter = "",
    [int]$MaxBudgetTurns = 30
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8

$EvalDir = $PSScriptRoot
$SkillDir = Split-Path -Parent $EvalDir
$Fixtures = Join-Path $EvalDir "fixtures"
$RunRoot = Join-Path $EvalDir ("runs\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force $RunRoot | Out-Null

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { throw "Claude Code CLI ('claude') not found on PATH." }

& (Join-Path $SkillDir "scripts\wiki-interest.ps1") --help | Out-Null

function Invoke-Turn([string]$Prompt, [string]$WorkDir, [string]$SessionId) {
    $cliArgs = @("-p", "--model", $Model, "--output-format", "json", "--max-turns", "$MaxBudgetTurns",
                 "--allowedTools", "Skill", "Bash", "PowerShell", "Read", "Glob", "Grep",
                 "--add-dir", $SkillDir, "--add-dir", $Fixtures)
    if ($SessionId) { $cliArgs += @("--resume", $SessionId) }
    Push-Location $WorkDir
    try {
        $raw = $Prompt | & claude @cliArgs 2>$null | Out-String
    } finally { Pop-Location }
    try { return $raw | ConvertFrom-Json } catch { return [pscustomobject]@{ result = $raw; session_id = $SessionId; total_cost_usd = 0; is_error = $true } }
}

function Format-GrowthCandidates([double]$g) {
    $p = [math]::Abs($g * 100)
    $ci = [Globalization.CultureInfo]::InvariantCulture
    return @($p.ToString("0.0", $ci), [math]::Round($p, [MidpointRounding]::AwayFromZero).ToString("0", $ci)) | Select-Object -Unique
}

$cases = Get-Content (Join-Path $EvalDir "agent-evals.jsonl") -Encoding UTF8 | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json }
if ($Filter) { $cases = $cases | Where-Object { $_.id -match $Filter } }

$results = @()
function Add-Check([string]$Name, [bool]$Pass, [string]$Detail = "") { $script:checks += [pscustomobject]@{ name = $Name; pass = $Pass; detail = $Detail } }

foreach ($case in $cases) {
    Write-Host "== $($case.id)" -ForegroundColor Cyan
    $work = Join-Path $RunRoot $case.id
    New-Item -ItemType Directory -Force $work | Out-Null
    $session = $null
    $answers = @()
    $cost = 0.0
    foreach ($turn in $case.turns) {
        $prompt = $turn.Replace("{fixtures}", $Fixtures.Replace("\", "/"))
        $r = Invoke-Turn $prompt $work $session
        $session = $r.session_id
        $answers += [string]$r.result
        if ($r.total_cost_usd) { $cost += [double]$r.total_cost_usd }
    }
    $final = $answers[-1]
    $checks = @()

    $produced = Get-ChildItem $work -Recurse -Filter analysis.json -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $analysis = if ($produced) { Get-Content $produced.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $e = $case.expect

    if ($e.noRun) {
        Add-Check "did not run a new analysis" (-not $produced)
        $analysis = Get-Content (Join-Path $Fixtures "$($case.fixture)\analysis.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    } else {
        Add-Check "produced analysis.json" ([bool]$produced)
        if ($analysis) {
            $req = $analysis.request
            if ($e.topicPattern) { Add-Check "topic" ($req.topic -match $e.topicPattern) $req.topic }
            if ($e.languages) {
                $got = @($req.languages | Sort-Object) -join ","
                $want = @($e.languages | Sort-Object) -join ","
                Add-Check "languages" ($got -eq $want) "got [$got], want [$want]"
            }
            if ($e.months) { Add-Check "period months" ($analysis.reportingPeriod.months -eq $e.months) "got $($analysis.reportingPeriod.months), want $($e.months)" }
            if ($e.status) { Add-Check "status" ($analysis.status -eq $e.status) "got $($analysis.status)" }
            if ($e.failedLanguages) {
                $failed = @($analysis.failures | ForEach-Object { $_.language } | Sort-Object) -join ","
                Add-Check "failed languages" ($failed -eq (@($e.failedLanguages | Sort-Object) -join ",")) "got [$failed]"
            }
            if ($e.excludedAny) { Add-Check "excluded a month" (@($req.excludedMonths).Count -gt 0) (@($req.excludedMonths) -join ",") }
            if ($e.pdf) { Add-Check "generated PDF" ([bool]$req.generatePdf -and (Test-Path (Join-Path (Split-Path $produced.FullName) "report.pdf"))) }
        }
    }

    if ($analysis) {
        foreach ($lang in @($case.quoteGrowthFor)) {
            $res = $analysis.results | Where-Object { $_.language -eq $lang } | Select-Object -First 1
            if (-not $res) { Add-Check "quotes growth for $lang" $false "language missing from analysis.json"; continue }
            $cands = Format-GrowthCandidates ([double]$res.trend.annualizedGrowth)
            $hit = $cands | Where-Object { $final.Contains($_) }
            Add-Check "quotes growth for $lang from analysis.json" ([bool]$hit) ("expects one of: " + ($cands -join " / "))
        }
    }

    foreach ($pattern in @($case.mustInclude)) { if ($pattern) { Add-Check "includes /$pattern/" ([regex]::IsMatch($final, $pattern, "Multiline")) } }
    foreach ($pattern in @($case.mustNotInclude)) { if ($pattern) { Add-Check "avoids /$pattern/" (-not [regex]::IsMatch($final, $pattern, "Multiline")) } }

    $passed = -not ($checks | Where-Object { -not $_.pass })
    $color = if ($passed) { "Green" } else { "Red" }
    foreach ($c in $checks) { Write-Host ("  [{0}] {1} {2}" -f ($(if ($c.pass) { "ok" } else { "FAIL" })), $c.name, $c.detail) }
    Write-Host ("  => {0} (cost `${1:0.000})" -f ($(if ($passed) { "PASS" } else { "FAIL" })), $cost) -ForegroundColor $color
    $results += [pscustomobject]@{ id = $case.id; category = $case.category; passed = $passed; cost = $cost; checks = $checks; answers = $answers; notes = $case.notes; workDir = $work }
}

$results | ConvertTo-Json -Depth 6 | Out-File (Join-Path $RunRoot "results.json") -Encoding utf8
$md = @("# Agent eval results ($Model, $(Get-Date -Format 'yyyy-MM-dd HH:mm'))", "", "| Case | Result | Failed checks |", "|---|---|---|")
foreach ($r in $results) {
    $failedNames = ($r.checks | Where-Object { -not $_.pass } | ForEach-Object { $_.name }) -join "; "
    $md += "| $($r.id) | $(if ($r.passed) { 'PASS' } else { 'FAIL' }) | $failedNames |"
}
foreach ($r in $results) {
    $md += @("", "## $($r.id)", "", "_$($r.notes)_", "")
    for ($i = 0; $i -lt $r.answers.Count; $i++) { $md += @("**Turn $($i + 1) answer:**", "", $r.answers[$i], "") }
}
$md | Out-File (Join-Path $RunRoot "results.md") -Encoding utf8

$passCount = @($results | Where-Object { $_.passed }).Count
Write-Host ""
Write-Host "$passCount / $($results.Count) cases passed. Total cost `$$('{0:0.00}' -f ($results | Measure-Object cost -Sum).Sum). Details: $RunRoot\results.md"
if ($passCount -ne $results.Count) { exit 1 }
