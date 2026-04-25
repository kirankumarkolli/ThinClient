# 3-pass alternating-order routing sweep across all variants for both
# DirectModeRoutingBenchmark (Container path) and DirectModeRoutingRawDsrBenchmark
# (raw-DSR path). Captures raw per-iteration measurements via CsvMeasurementsExporter
# so high-tail percentiles can be computed offline.
#
# Output: $OutDir\<scenario>\p<pass>-<label>.csv

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutDir   = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'bench-out\pkrange'),
    [int]$Passes      = 3,
    [string[]]$Scenarios = @('rawdsr', 'container')
)

$ErrorActionPreference = 'Continue'
Set-Location $RepoRoot

# Variant token = COSMOS_PKRANGE_VARIANT value; output files named after Label.
# Labels avoid characters that would be awkward in file names.
$variants = @(
    @{ Label = 'string';               Variant = 'string';        Bypass = 'false' },
    @{ Label = 'uint128';              Variant = 'uint128';       Bypass = 'false' },
    @{ Label = 'bytespan-seq';         Variant = 'bytespan-seq';  Bypass = 'false' },
    @{ Label = 'bytespan-hand';        Variant = 'bytespan-hand'; Bypass = 'false' },
    @{ Label = 'bytespan-hand-bypass'; Variant = 'bytespan-hand'; Bypass = 'true'  },
    @{ Label = 'radix1';               Variant = 'radix1';        Bypass = 'true'  },
    @{ Label = 'radix2';               Variant = 'radix2';        Bypass = 'true'  },
    @{ Label = 'soa';                  Variant = 'soa';           Bypass = 'true'  },
    @{ Label = 'string-soa';           Variant = 'string-soa';    Bypass = 'false' },
    @{ Label = 'cache-last';           Variant = 'cache-last';    Bypass = 'true'  }
)

$allScenarios = @(
    @{ Name = 'rawdsr';    Filter = '*DirectModeRoutingRawDsrBenchmark*' },
    @{ Name = 'container'; Filter = '*DirectModeRoutingBenchmark.ReadItemStream*' }
)
$matchedScenarios = @($allScenarios | Where-Object { $Scenarios -contains $_.Name })
if (-not $matchedScenarios) { throw "No matching scenarios in: $($Scenarios -join ',')" }

$projectDir = Join-Path $RepoRoot 'Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests'
$dllPath    = Join-Path $projectDir 'bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll'

$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

foreach ($scenario in $matchedScenarios) {
    $scenarioOut = Join-Path $OutDir $scenario.Name
    New-Item -ItemType Directory -Force -Path $scenarioOut | Out-Null

    for ($pass = 1; $pass -le $Passes; $pass++) {
        foreach ($v in $variants) {
            $label = $v.Label
            $stamp = Get-Date -Format 'HH:mm:ss'
            Write-Host "[$stamp] scenario=$($scenario.Name) pass=$pass label=$label variant=$($v.Variant) bypass=$($v.Bypass)"

            $env:COSMOS_PKRANGE_VARIANT          = $v.Variant
            $env:COSMOS_PKRANGE_BYPASS_STRING_EPK = $v.Bypass

            $artifactsDir = Join-Path $scenarioOut "p$pass-$label-artifacts"
            New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

            $logFile = Join-Path $scenarioOut "p$pass-$label.log"
            & dotnet $dllPath --filter $($scenario.Filter) --artifacts $artifactsDir 2>&1 | Tee-Object -FilePath $logFile | Out-Null

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
