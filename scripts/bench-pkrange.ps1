# One-shot: build + run + report for the PKRange routing benchmark sweep.
#
# Wraps run-pkrange-soa-sweep.ps1 (3-pass alternating) and
# compute-soa-percentiles.ps1 (percentile aggregator) so a single invocation
# produces the markdown table you'd paste into the optimization report.
#
# Usage:
#   .\scripts\bench-pkrange.ps1                       # full sweep, both scenarios
#   .\scripts\bench-pkrange.ps1 -Quick                # 1 pass, container only
#   .\scripts\bench-pkrange.ps1 -SkipBuild -OutDir D:\bench
#
# Output: $OutDir\summary.txt and stdout markdown table.

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutDir   = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'bench-out\pkrange'),
    [switch]$SkipBuild,
    [switch]$Quick
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

$projectDir = Join-Path $RepoRoot 'Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests'
$dllPath    = Join-Path $projectDir 'bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll'

# 1. Build (Release) ---------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "==> Building Performance.Tests (Release)" -ForegroundColor Cyan
    & dotnet build $projectDir -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
if (-not (Test-Path $dllPath)) { throw "Benchmark DLL not found: $dllPath" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 2. Define sweep matrix -----------------------------------------------------
$variants = @(
    @{ Label = 'A'; Variant = 'string';        Bypass = 'false' },
    @{ Label = 'B'; Variant = 'uint128';       Bypass = 'false' },
    @{ Label = 'C'; Variant = 'bytespan-seq';  Bypass = 'false' },
    @{ Label = 'D'; Variant = 'bytespan-hand'; Bypass = 'false' },
    @{ Label = 'F'; Variant = 'bytespan-hand'; Bypass = 'true'  },
    @{ Label = 'G'; Variant = 'soa';           Bypass = 'true'  },
    @{ Label = 'H'; Variant = 'string-soa';    Bypass = 'false' }
)

$scenarios = if ($Quick) {
    @(@{ Name = 'container'; Filter = '*DirectModeRoutingBenchmark.ReadItemStream*' })
} else {
    @(
        @{ Name = 'rawdsr';    Filter = '*DirectModeRoutingRawDsrBenchmark*' },
        @{ Name = 'container'; Filter = '*DirectModeRoutingBenchmark.ReadItemStream*' }
    )
}
$passCount = if ($Quick) { 1 } else { 3 }

# 3. Run sweep ---------------------------------------------------------------
foreach ($scenario in $scenarios) {
    $scenarioOut = Join-Path $OutDir $scenario.Name
    New-Item -ItemType Directory -Force -Path $scenarioOut | Out-Null

    for ($pass = 1; $pass -le $passCount; $pass++) {
        foreach ($v in $variants) {
            $stamp = Get-Date -Format 'HH:mm:ss'
            Write-Host "[$stamp] scenario=$($scenario.Name) pass=$pass label=$($v.Label) variant=$($v.Variant) bypass=$($v.Bypass)" -ForegroundColor Yellow

            $env:COSMOS_PKRANGE_VARIANT           = $v.Variant
            $env:COSMOS_PKRANGE_BYPASS_STRING_EPK = $v.Bypass

            $artifactsDir = Join-Path $scenarioOut "p$pass-$($v.Label)-artifacts"
            New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
            $logFile = Join-Path $scenarioOut "p$pass-$($v.Label).log"

            & dotnet $dllPath --filter $scenario.Filter --artifacts $artifactsDir 2>&1 |
                Tee-Object -FilePath $logFile | Out-Null

            $csvHit = Get-ChildItem -Path $artifactsDir -Recurse -Filter '*-measurements.csv' `
                      -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($csvHit) {
                Copy-Item $csvHit.FullName -Destination (Join-Path $scenarioOut "p$pass-$($v.Label).csv") -Force
            } else {
                Write-Warning "No measurements CSV produced for $($scenario.Name) pass=$pass label=$($v.Label)"
            }
        }
    }
}

# 4. Aggregate + report ------------------------------------------------------
Write-Host "`n==> Aggregating percentiles" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'compute-soa-percentiles.ps1') -Root $OutDir

Write-Host "`nDone. Artifacts: $OutDir" -ForegroundColor Green
Write-Host "Summary table:   $(Join-Path $OutDir 'summary.txt')" -ForegroundColor Green
