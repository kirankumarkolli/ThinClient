# Compute high-tail percentiles per (scenario, variant) by pooling Workload+Actual
# rows across the 3 alternating passes. Mirrors the methodology used in PR #9.

[CmdletBinding()]
param(
    [string]$Root = "$env:USERPROFILE\.copilot\session-state\7ef9d8fb-011b-4dab-b259-eb24685997e2\files\measurements\soa"
)

function Get-Percentile {
    param([double[]]$sorted, [double]$p)
    if ($sorted.Length -eq 0) { return [double]::NaN }
    $rank = $p / 100.0 * ($sorted.Length - 1)
    $lo = [int][math]::Floor($rank)
    $hi = [int][math]::Ceiling($rank)
    if ($lo -eq $hi) { return $sorted[$lo] }
    $frac = $rank - $lo
    return $sorted[$lo] * (1 - $frac) + $sorted[$hi] * $frac
}

$variants = @('A','B','C','D','F','G','H')
$scenarios = @('rawdsr','container')
$results = @()

foreach ($scenario in $scenarios) {
    foreach ($v in $variants) {
        $samples = New-Object System.Collections.Generic.List[double]
        for ($pass = 1; $pass -le 3; $pass++) {
            $csv = Join-Path $Root "$scenario\p$pass-$v.csv"
            if (-not (Test-Path $csv)) { continue }
            $rows = Import-Csv $csv
            foreach ($row in $rows) {
                if ($row.Measurement_IterationStage -ne 'Result') { continue }
                if ($row.Measurement_IterationMode -ne 'Workload' -and $row.Measurement_IterationMode -ne 'Actual') { continue }
                $ns = [double]$row.Measurement_Nanoseconds
                $ops = [double]$row.Measurement_Operations
                if ($ops -gt 0) { $samples.Add($ns / $ops / 1000.0) } # microseconds
            }
        }
        $sorted = $samples.ToArray() | Sort-Object
        $results += [PSCustomObject]@{
            Scenario = $scenario
            V = $v
            N = $sorted.Length
            P50  = [math]::Round((Get-Percentile $sorted 50), 3)
            P90  = [math]::Round((Get-Percentile $sorted 90), 3)
            P95  = [math]::Round((Get-Percentile $sorted 95), 3)
            P99  = [math]::Round((Get-Percentile $sorted 99), 3)
            P999 = [math]::Round((Get-Percentile $sorted 99.9), 3)
            P100 = [math]::Round((Get-Percentile $sorted 100), 3)
        }
    }
}

$results | Format-Table -AutoSize | Out-String | Write-Host

# Compute % deltas vs baseline A and vs F (winner of PR #9)
$results | Format-Table -AutoSize | Out-File (Join-Path $Root 'summary.txt') -Encoding utf8

foreach ($scenario in $scenarios) {
    Write-Host "`n=== $scenario : delta vs A baseline ==="
    $baseA = $results | Where-Object { $_.Scenario -eq $scenario -and $_.V -eq 'A' }
    foreach ($v in $variants) {
        if ($v -eq 'A') { continue }
        $r = $results | Where-Object { $_.Scenario -eq $scenario -and $_.V -eq $v }
        $deltas = foreach ($p in 'P50','P90','P95','P99','P999','P100') {
            if ($baseA.$p -gt 0) {
                [math]::Round((($r.$p - $baseA.$p) / $baseA.$p * 100), 2)
            } else { 0 }
        }
        Write-Host ("  {0}: P50={1,7:F2}% P90={2,7:F2}% P95={3,7:F2}% P99={4,7:F2}% P99.9={5,7:F2}% P100={6,7:F2}%" -f $v, $deltas[0], $deltas[1], $deltas[2], $deltas[3], $deltas[4], $deltas[5])
    }

    Write-Host "`n=== $scenario : G delta vs F (PR #9 winner) ==="
    $baseF = $results | Where-Object { $_.Scenario -eq $scenario -and $_.V -eq 'F' }
    $r = $results | Where-Object { $_.Scenario -eq $scenario -and $_.V -eq 'G' }
    $deltas = foreach ($p in 'P50','P90','P95','P99','P999','P100') {
        if ($baseF.$p -gt 0) {
            [math]::Round((($r.$p - $baseF.$p) / $baseF.$p * 100), 2)
        } else { 0 }
    }
    Write-Host ("  G:    P50={0,7:F2}% P90={1,7:F2}% P95={2,7:F2}% P99={3,7:F2}% P99.9={4,7:F2}% P100={5,7:F2}%" -f $deltas[0], $deltas[1], $deltas[2], $deltas[3], $deltas[4], $deltas[5])
}
