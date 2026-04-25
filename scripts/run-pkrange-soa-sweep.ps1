# 3-pass alternating-order routing sweep across all variants for both
# DirectModeRoutingBenchmark (Container path) and DirectModeRoutingRawDsrBenchmark
# (raw-DSR path). Captures raw per-iteration measurements via CsvMeasurementsExporter
# so high-tail percentiles can be computed offline.
#
# Output: $OutDir\<scenario>\p<pass>-<label>.csv

[CmdletBinding()]
param(
    [string]$OutDir = "$env:USERPROFILE\.copilot\session-state\7ef9d8fb-011b-4dab-b259-eb24685997e2\files\measurements\soa",
    [string]$RepoRoot = "C:\src\v31-msdata-port"
)

$ErrorActionPreference = 'Continue'
Set-Location $RepoRoot

$variants = @(
    @{ Label = 'A'; Variant = 'string';        Bypass = 'false' },
    @{ Label = 'B'; Variant = 'uint128';       Bypass = 'false' },
    @{ Label = 'C'; Variant = 'bytespan-seq';  Bypass = 'false' },
    @{ Label = 'D'; Variant = 'bytespan-hand'; Bypass = 'false' },
    @{ Label = 'F'; Variant = 'bytespan-hand'; Bypass = 'true'  },
    @{ Label = 'G'; Variant = 'soa';           Bypass = 'true'  }
)

$scenarios = @(
    @{ Name = 'rawdsr';    Filter = '*DirectModeRoutingRawDsrBenchmark*' },
    @{ Name = 'container'; Filter = '*DirectModeRoutingBenchmark.ReadItemStream*' }
)

$projectDir = Join-Path $RepoRoot 'Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests'
$dllPath    = Join-Path $projectDir 'bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll'

$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

foreach ($scenario in $scenarios) {
    $scenarioOut = Join-Path $OutDir $scenario.Name
    New-Item -ItemType Directory -Force -Path $scenarioOut | Out-Null

    for ($pass = 1; $pass -le 3; $pass++) {
        foreach ($v in $variants) {
            $label = $v.Label
            $stamp = Get-Date -Format 'HH:mm:ss'
            Write-Host "[$stamp] scenario=$($scenario.Name) pass=$pass label=$label variant=$($v.Variant) bypass=$($v.Bypass)"

            $env:COSMOS_PKRANGE_VARIANT          = $v.Variant
            $env:COSMOS_PKRANGE_BYPASS_STRING_EPK = $v.Bypass

            $artifactsDir = Join-Path $scenarioOut "p$pass-$label-artifacts"
            New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

            $logFile = Join-Path $scenarioOut "p$pass-$label.log"
            & dotnet $dllPath --filter $scenario.Filter --artifacts $artifactsDir 2>&1 | Tee-Object -FilePath $logFile | Out-Null

            $csvHit = Get-ChildItem -Path $artifactsDir -Recurse -Filter '*-measurements.csv' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($csvHit) {
                Copy-Item $csvHit.FullName -Destination (Join-Path $scenarioOut "p$pass-$label.csv") -Force
            } else {
                Write-Warning "No measurements CSV produced for scenario=$($scenario.Name) pass=$pass label=$label"
            }
        }
    }
}

Write-Host "Sweep complete. Output: $OutDir"
