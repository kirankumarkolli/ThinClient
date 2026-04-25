# Aggregate routing-benchmark sweep output into a per-(scenario, profile) table.
#
# Layout produced by run-pkrange-sweep.ps1:
#   $Root\<scenario>\p<N>.csv      # one CSV per pass; rows tagged with Param_Profile
#
# Each metric (Avg / Min / Max / P50 / P90 / P95 / P99 / P99.9) is computed
# per pass and then aggregated across passes; cells render as "avg [min..max]"
# in microseconds, exposing pass-to-pass drift inline.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'bench-out\pkrange'),
    [string[]]$Scenarios = @('rawdsr', 'container'),
    [string]$Baseline = 'string'
)

function Get-Percentile {
    param([double[]]$sorted, [double]$p)
    if ($sorted.Length -eq 0) { return [double]::NaN }
    $rank = $p / 100.0 * ($sorted.Length - 1)
    $lo = [int][math]::Floor($rank); $hi = [int][math]::Ceiling($rank)
    if ($lo -eq $hi) { return $sorted[$lo] }
    $frac = $rank - $lo
    return $sorted[$lo] * (1 - $frac) + $sorted[$hi] * $frac
}

function Format-AggCell {
    param([double[]]$values, [int]$digits = 3)
    if (-not $values -or $values.Length -eq 0) { return '-' }
    $avg = ($values | Measure-Object -Average).Average
    $mn  = ($values | Measure-Object -Minimum).Minimum
    $mx  = ($values | Measure-Object -Maximum).Maximum
    if ($values.Length -eq 1) { return ('{0:F{1}}' -f $avg, $digits) }
    return ('{0:F{2}} [{1:F{2}}..{3:F{2}}]' -f $avg, $mn, $digits, $mx)
}

# Collect samples per (scenario, profile, pass) ------------------------------
# $bag[$scenario][$profile] = array of arrays — one inner array of sorted samples per pass.
$bag = @{}
foreach ($scenario in $Scenarios) {
    $bag[$scenario] = @{}
    $scenarioDir = Join-Path $Root $scenario
    if (-not (Test-Path $scenarioDir)) { Write-Warning "missing: $scenarioDir"; continue }

    $passFiles = Get-ChildItem -Path $scenarioDir -Filter 'p*.csv' -ErrorAction SilentlyContinue |
                 Where-Object { $_.BaseName -match '^p\d+$' } |
                 Sort-Object { [int]($_.BaseName.Substring(1)) }

    foreach ($file in $passFiles) {
        # Group rows by profile within this pass.
        $perProfileSamples = @{}
        foreach ($row in (Import-Csv $file.FullName)) {
            if ($row.Measurement_IterationStage -ne 'Result') { continue }
            if ($row.Measurement_IterationMode -ne 'Workload' -and
                $row.Measurement_IterationMode -ne 'Actual') { continue }
            $profile = $row.Param_Profile
            if (-not $profile) { continue }
            $ns  = [double]$row.Measurement_Nanoseconds
            $ops = [double]$row.Measurement_Operations
            if ($ops -le 0) { continue }
            if (-not $perProfileSamples.ContainsKey($profile)) {
                $perProfileSamples[$profile] = New-Object System.Collections.Generic.List[double]
            }
            $perProfileSamples[$profile].Add($ns / $ops / 1000.0) # microseconds
        }
        foreach ($profile in $perProfileSamples.Keys) {
            if (-not $bag[$scenario].ContainsKey($profile)) {
                $bag[$scenario][$profile] = @()
            }
            $bag[$scenario][$profile] += ,($perProfileSamples[$profile].ToArray() | Sort-Object)
        }
    }
}

# Compute per-pass metrics and aggregate ------------------------------------
$metrics = [ordered]@{
    Avg  = { param($s) ($s | Measure-Object -Average).Average }
    Min  = { param($s) $s[0] }
    Max  = { param($s) $s[-1] }
    P50  = { param($s) Get-Percentile $s 50 }
    P90  = { param($s) Get-Percentile $s 90 }
    P95  = { param($s) Get-Percentile $s 95 }
    P99  = { param($s) Get-Percentile $s 99 }
    P999 = { param($s) Get-Percentile $s 99.9 }
}

$rows = @()
foreach ($scenario in $Scenarios) {
    if (-not $bag.ContainsKey($scenario)) { continue }
    $profiles = $bag[$scenario].Keys | Sort-Object
    foreach ($p in $profiles) {
        $passes = $bag[$scenario][$p]
        if (-not $passes -or $passes.Count -eq 0) { continue }
        $row = [ordered]@{
            Scenario = $scenario
            Profile  = $p
            Passes   = $passes.Count
            N        = ($passes | ForEach-Object { $_.Length } | Measure-Object -Sum).Sum
        }
        foreach ($mName in $metrics.Keys) {
            $perPassValues = foreach ($s in $passes) { & $metrics[$mName] $s }
            $row[$mName] = Format-AggCell -values @($perPassValues) -digits 3
        }
        $rows += [PSCustomObject]$row
    }
}

if ($rows.Count -eq 0) { Write-Warning "No data found under $Root"; return }

$rows | Format-Table -AutoSize | Out-String | Write-Host
$rows | Format-Table -AutoSize | Out-File (Join-Path $Root 'summary.txt') -Encoding utf8
$rows | Export-Csv -NoTypeInformation -Path (Join-Path $Root 'summary.csv')

# Delta vs baseline ----------------------------------------------------------
function Get-AvgFromCell {
    param([string]$cell)
    if ([string]::IsNullOrWhiteSpace($cell) -or $cell -eq '-') { return $null }
    if ($cell -match '^([\d\.]+)') { return [double]$matches[1] }
    return $null
}

foreach ($scenario in $Scenarios) {
    $base = $rows | Where-Object { $_.Scenario -eq $scenario -and $_.Profile -eq $Baseline }
    if (-not $base) { continue }
    Write-Host "`n=== $scenario : Avg delta vs '$Baseline' baseline (negative = faster) ===" -ForegroundColor Cyan
    foreach ($r in ($rows | Where-Object { $_.Scenario -eq $scenario })) {
        if ($r.Profile -eq $Baseline) { continue }
        $deltas = foreach ($m in 'Avg','P50','P90','P99','P999') {
            $b = Get-AvgFromCell $base.$m
            $c = Get-AvgFromCell $r.$m
            if ($b -and $b -gt 0 -and $c) { '{0,7:F2}%' -f ((($c - $b) / $b) * 100) } else { '   n/a ' }
        }
        Write-Host ("  {0,-22}  Avg={1}  P50={2}  P90={3}  P99={4}  P99.9={5}" -f $r.Profile, $deltas[0], $deltas[1], $deltas[2], $deltas[3], $deltas[4])
    }
}

Write-Host "`nSummary written to: $(Join-Path $Root 'summary.txt') (and summary.csv)" -ForegroundColor Green
