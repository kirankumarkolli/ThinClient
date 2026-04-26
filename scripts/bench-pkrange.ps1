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
    [string]$OutDir   = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'bench-out\pkrange'),
    [switch]$SkipBuild,
    [switch]$Quick,
    [string[]]$Scenarios = @('rawdsr')
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

$projectDir = Join-Path $RepoRoot 'Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests'
$dllPath    = Join-Path $projectDir 'bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll'

if (-not $SkipBuild) {
    Write-Host "==> Building Performance.Tests (Release)" -ForegroundColor Cyan
    & dotnet build $projectDir -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
if (-not (Test-Path $dllPath)) { throw "Benchmark DLL not found: $dllPath" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$passes    = if ($Quick) { 1 } else { 3 }
$scenarios = $Scenarios

Write-Host "==> Running sweep ($passes pass(es), scenarios: $($scenarios -join ', '))" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'run-pkrange-sweep.ps1') -RepoRoot $RepoRoot -OutDir $OutDir -Passes $passes -Scenarios $scenarios

Write-Host "`n==> Aggregating per-pass percentiles (avg [min..max] per cell)" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'compute-pkrange-percentiles.ps1') -Root $OutDir -Scenarios $scenarios

Write-Host "`nDone. Artifacts: $OutDir" -ForegroundColor Green
