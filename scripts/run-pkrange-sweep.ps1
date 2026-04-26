# Alternating-order routing-benchmark sweep.
#
# The benchmarks (DirectModeRoutingBenchmark, DirectModeRoutingRawDsrBenchmark)
# expose a `Profile` [Params] axis and apply each profile's (variant, bypass)
# tuple in GlobalSetup. BenchmarkDotNet handles per-profile process isolation
# and invocation ordering — this script just drives N passes per scenario.
#
# Output: $OutDir\<scenario>\p<pass>.csv  (one consolidated CSV per pass,
#         containing all profiles; the aggregator demultiplexes by Param_Profile.)

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutDir   = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'bench-out\pkrange'),
    [int]$Passes      = 3,
    [string[]]$Scenarios = @('rawdsr')
)

$ErrorActionPreference = 'Continue'
Set-Location $RepoRoot

$projectDir = Join-Path $RepoRoot 'Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests'
$dllPath    = Join-Path $projectDir 'bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll'
if (-not (Test-Path $dllPath)) { throw "Benchmark DLL not found: $dllPath (build with -c Release first)" }

$allScenarios = @(
    @{ Name = 'rawdsr';              Filter = '*DirectModeRoutingRawDsrBenchmark.ReadViaRawDsr*' },
    @{ Name = 'container';           Filter = '*DirectModeRoutingBenchmark.ReadItemStream*' },
    @{ Name = 'rawdsr-construction'; Filter = '*DirectModeRoutingRawDsrConstructionBenchmark*' }
)
$matchedScenarios = @($allScenarios | Where-Object { $Scenarios -contains $_.Name })
if (-not $matchedScenarios) { throw "No matching scenarios in: $($Scenarios -join ',')" }

$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

foreach ($scenario in $matchedScenarios) {
    $scenarioOut = Join-Path $OutDir $scenario.Name
    New-Item -ItemType Directory -Force -Path $scenarioOut | Out-Null

    for ($pass = 1; $pass -le $Passes; $pass++) {
        $stamp = Get-Date -Format 'HH:mm:ss'
        Write-Host "[$stamp] scenario=$($scenario.Name) pass=$pass" -ForegroundColor Yellow

        $artifactsDir = Join-Path $scenarioOut "p$pass-artifacts"
        New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
        $logFile = Join-Path $scenarioOut "p$pass.log"

        # BDN spawns one child process per [Params] value (each profile),
        # cycling them in alpha order — alternation is preserved across passes.
        & dotnet $dllPath --filter $($scenario.Filter) --artifacts $artifactsDir 2>&1 |
            Tee-Object -FilePath $logFile | Out-Null

        # Consolidated measurements CSV: one row per (profile, iteration).
        $csvHit = Get-ChildItem -Path $artifactsDir -Recurse -Filter '*-measurements.csv' `
                  -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($csvHit) {
            Copy-Item $csvHit.FullName -Destination (Join-Path $scenarioOut "p$pass.csv") -Force
        } else {
            Write-Warning "No measurements CSV produced for scenario=$($scenario.Name) pass=$pass"
        }
    }
}

Write-Host "Sweep complete. Output: $OutDir"
