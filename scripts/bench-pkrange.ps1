# One-shot: build + sweep + report for the PKRange routing benchmark.
#
# Wraps run-pkrange-sweep.ps1 (alternating-order sweep) and
# compute-pkrange-percentiles.ps1 (per-pass aggregator with avg/min/max cells)
# so a single command produces the markdown table for the optimization report.
#
# Usage:
#   .\scripts\bench-pkrange.ps1                                 # full sweep (rawdsr only, 3 passes)
#   .\scripts\bench-pkrange.ps1 -Quick                          # 1 pass, rawdsr only
#   .\scripts\bench-pkrange.ps1 -Scenarios rawdsr,container     # include container scenario
#   .\scripts\bench-pkrange.ps1 -SkipBuild -OutDir D:\bench

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutDir,
    [switch]$SkipBuild,
    [switch]$Quick,
    [string[]]$Scenarios = @('rawdsr'),
    # Smoke mode: drives BDN with a tiny config (1000 invocations × 3 iterations)
    # so the full 11-profile sweep finishes in ~2 minutes. Implies -Quick (1 pass)
    # and skips percentile aggregation since the per-pass CSVs are too noisy to
    # compare meaningfully. Use to validate the script flow, NOT for perf claims.
    [switch]$Smoke
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

# Each run gets its own timestamped folder so successive runs don't overwrite.
# Override with -OutDir to pin a specific location (useful for the aggregator).
if (-not $OutDir) {
    $stamp  = Get-Date -Format 'yyyy-MM-dd_HHmmss'
    $suffix = if ($Smoke) { "smoke-$stamp" } else { "run-$stamp" }
    $OutDir = Join-Path $RepoRoot (Join-Path 'bench-out\pkrange' $suffix)
}

$projectDir = Join-Path $RepoRoot 'Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests'
$dllPath    = Join-Path $projectDir 'bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll'

if (-not $SkipBuild) {
    Write-Host "==> Building Performance.Tests (Release)" -ForegroundColor Cyan
    & dotnet build $projectDir -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
if (-not (Test-Path $dllPath)) { throw "Benchmark DLL not found: $dllPath" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$passes    = if ($Quick -or $Smoke) { 1 } else { 3 }
$scenarios = $Scenarios

$mode = if ($Smoke) { 'smoke' } else { 'production' }
Write-Host "==> Running sweep ($mode, $passes pass(es), scenarios: $($scenarios -join ', '))" -ForegroundColor Cyan
$sweepArgs = @{
    RepoRoot  = $RepoRoot
    OutDir    = $OutDir
    Passes    = $passes
    Scenarios = $scenarios
}
if ($Smoke) { $sweepArgs['Smoke'] = $true }
& (Join-Path $PSScriptRoot 'run-pkrange-sweep.ps1') @sweepArgs

if ($Smoke) {
    Write-Host "`nSmoke run complete (no aggregation - results too noisy to percentile)." -ForegroundColor Yellow
    foreach ($s in $scenarios) {
        $reportDir = Join-Path $OutDir (Join-Path $s 'p1-artifacts\results')
        $report = Get-ChildItem -Path $reportDir -Filter '*-report-github.md' -ErrorAction SilentlyContinue | Select-Object -First 1
        Write-Host "`n=== Scenario: $s ===" -ForegroundColor Cyan
        if ($report) {
            Get-Content $report.FullName | Write-Host
            Write-Host "`n  Source: $($report.FullName)" -ForegroundColor DarkGray
        } else {
            Write-Warning "No report-github.md found under $reportDir"
        }
    }
}else {
    Write-Host "`n==> Aggregating per-pass percentiles (avg [min..max] per cell)" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'compute-pkrange-percentiles.ps1') -Root $OutDir -Scenarios $scenarios
}

Write-Host "`nDone. Artifacts: $OutDir" -ForegroundColor Green
