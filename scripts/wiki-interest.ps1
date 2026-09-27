$Root = Split-Path -Parent $PSScriptRoot
$Dll = Join-Path $Root ".build\wiki-interest.dll"
$Src = Join-Path $Root "src"

function Test-NewerSource([string]$Dir, [datetime]$Since) {
    foreach ($file in [IO.Directory]::EnumerateFiles($Dir)) {
        if ([IO.File]::GetLastWriteTimeUtc($file) -gt $Since) { return $true }
    }
    foreach ($sub in [IO.Directory]::EnumerateDirectories($Dir)) {
        $name = [IO.Path]::GetFileName($sub)
        if ($name -eq "bin" -or $name -eq "obj") { continue }
        if (Test-NewerSource $sub $Since) { return $true }
    }
    return $false
}

$stale = -not (Test-Path $Dll)
if (-not $stale) { $stale = Test-NewerSource $Src ([IO.File]::GetLastWriteTimeUtc($Dll)) }
if ($stale) {
    dotnet build (Join-Path $Src "WikipediaInterestSkill\WikipediaInterestSkill.csproj") -c Release -o (Join-Path $Root ".build") --nologo -v q | ForEach-Object { [Console]::Error.WriteLine($_) }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& dotnet $Dll @args
exit $LASTEXITCODE
