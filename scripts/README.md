# PKRange routing benchmark scripts

Tools for reproducing the PKRange-lookup fast-path optimization numbers reported in
[`docs/pkrange-lookup-optimization-baseline-report.md`](../docs/pkrange-lookup-optimization-baseline-report.md).

## TL;DR — one command

```powershell
.\scripts\bench-pkrange.ps1
```

Builds Performance.Tests in Release, runs all 10 fast-path variants × 2 scenarios
× 3 alternating passes, then prints a percentile table where each cell shows
`avg [min..max]` across the 3 passes.

## Files

| Script | Purpose |
| ------ | ------- |
| `bench-pkrange.ps1`              | One-shot wrapper: build + sweep + report. |
| `run-pkrange-sweep.ps1`          | Sweep only: produces per-(pass, variant) raw measurement CSVs. |
| `compute-pkrange-percentiles.ps1`| Aggregator: computes Avg/Min/Max + percentiles per pass, then renders `avg [min..max]` across passes. |

## Prerequisites

- Windows PowerShell 5.1+ or PowerShell 7+
- .NET 8 SDK (the wrapper sets `DOTNET_ROLL_FORWARD=LatestMajor`)
- Repo cloned and on `feature/msdata-direct-pkrange-lookup-optimization` (or descendant)

## Variants benchmarked

The variant token is set via `COSMOS_PKRANGE_VARIANT` (consumed by
`FastPathVariantSelector`). The bypass flag is set via
`COSMOS_PKRANGE_BYPASS_STRING_EPK` (consumed by `AddressResolver` to skip the
upstream `string` allocation when feeding the EPK to the routing map).

| Label                  | Variant         | Bypass | Notes |
| ---------------------- | --------------- | ------ | ----- |
| `string`               | `string`        | false  | baseline (legacy `SortedList`) |
| `uint128`              | `uint128`       | false  | UInt128 numeric fast-path (V2 hash only) |
| `bytespan-seq`         | `bytespan-seq`  | false  | byte-span sequential compare |
| `bytespan-hand`        | `bytespan-hand` | false  | byte-span hand-unrolled compare |
| `bytespan-hand-bypass` | `bytespan-hand` | true   | hand-unrolled + skip string→EPK conversion (PR #9 winner) |
| `radix1`               | `radix1`        | true   | 1-byte radix index |
| `radix2`               | `radix2`        | true   | 2-byte radix index |
| `soa`                  | `soa`           | true   | struct-of-arrays + bypass |
| `string-soa`           | `string-soa`    | false  | string-keyed SoA (works on V1 / hierarchical too) |
| `cache-last`           | `cache-last`    | true   | wraps an inner strategy with a single-slot last-hit cache |

> Numeric strategies (everything except `string` and `string-soa`) require V2-hash
> 32-char hex range boundaries. The factory falls back to `StringStrategy` on
> non-V2 topologies. `string-soa` is exempted so it can run on any topology.

Each label spawns a fresh `dotnet` child process — BDN best practice for variant
isolation, accurate per-strategy memory accounting, and to defeat tiered-JIT
cross-contamination.

## Scenarios

| Name        | BDN filter                                    | What it measures |
| ----------- | --------------------------------------------- | ---------------- |
| `container` | `*DirectModeRoutingBenchmark.ReadItemStream*` | Full `Container.ReadItemStreamAsync` path |
| `rawdsr`    | `*DirectModeRoutingRawDsrBenchmark*`          | Routing only, raw DSR (no Container plumbing) |

## Wrapper usage

```powershell
# Full sweep (~45–60 min): 10 variants × 2 scenarios × 3 passes
.\scripts\bench-pkrange.ps1

# Smoke run (~5–8 min): 10 variants × container only × 1 pass
.\scripts\bench-pkrange.ps1 -Quick

# Skip rebuild (artifacts already in bin\Release)
.\scripts\bench-pkrange.ps1 -SkipBuild

# Custom output location
.\scripts\bench-pkrange.ps1 -OutDir D:\bench-runs\2026-04-25
```

Parameters:

- `-RepoRoot <path>` — auto-detected from `$PSScriptRoot`.
- `-OutDir <path>`   — defaults to `<repo>\bench-out\pkrange`.
- `-SkipBuild`       — skip `dotnet build`; assumes DLL exists.
- `-Quick`           — 1 pass, container scenario only.

## Output layout

```
bench-out\pkrange\
├── summary.txt                          # final per-pass aggregate table
├── summary.csv                          # same, machine-readable
├── container\
│   ├── p1-string.csv                    # raw measurements, label=string, pass 1
│   ├── p1-string.log                    # full BDN console output
│   ├── p1-string-artifacts\             # full BDN artifact dir
│   ├── p1-uint128.csv … p1-cache-last.csv
│   ├── p2-…
│   └── p3-…
└── rawdsr\
    └── … (same structure)
```

## Reading the report

The aggregator computes each metric **per pass** (3 numbers — one per CSV) and
formats the cell as **`avg [min..max]`** in microseconds. Example row:

```
Scenario  Variant      Passes N      Avg                      P50                      P99
--------  -----------  ------ ----   ----                     ---                      ---
container bytespan-hand     3 4530   1.214 [1.198..1.232]     1.187 [1.171..1.203]     1.498 [1.461..1.538]
container soa               3 4530   1.103 [1.094..1.115]     1.078 [1.072..1.087]     1.342 [1.318..1.371]
```

The `[min..max]` shows pass-to-pass drift — if it spans more than ~3% of the
average, treat the result as noisy and re-run.

After the table the script prints **`Avg delta vs '<baseline>' baseline`** per
scenario (default baseline = `string`). Negative percentages = improvement.

To compare against a different baseline (e.g., the previous PR winner):

```powershell
.\scripts\compute-pkrange-percentiles.ps1 -Root .\bench-out\pkrange `
    -Baseline 'bytespan-hand-bypass'
```

## Why 3 alternating passes?

Single-shot benchmark runs hide ±2–5% across-process drift on Windows
(thermal, scheduler, JIT tier-0 → tier-1, OS noise). Running each variant 3×
in alternating order across separate processes separates signal from drift.

The inner loop is `variant`, the outer loop is `pass`, so the timeline is
`v1 v2 … v10 v1 v2 … v10 v1 v2 … v10` — each variant's three runs are
separated by all the others (no back-to-back same-variant runs).

## Manual single-variant run

If you want BDN's built-in markdown table for one variant:

```powershell
$env:COSMOS_PKRANGE_VARIANT           = 'soa'
$env:COSMOS_PKRANGE_BYPASS_STRING_EPK = 'true'
dotnet Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests\bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll `
    --filter '*DirectModeRoutingBenchmark*'
```

The markdown table lands in `BenchmarkDotNet.Artifacts\results\*.md`.

## Parametric range-count microbenchmark

`DirectModeRoutingBenchmark` uses production-shaped data
(`Data/shared_conversations_pkranges.tsv`, fixed range count). For per-N
microbenchmarks at 100 / 1k / 10k / 50k partitions:

```powershell
dotnet Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests\bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll `
    --filter '*CollectionRoutingMapBenchmark*'
```

That benchmark already has `[Params(100, 1_000, 10_000, 50_000)]` and
`[MemoryDiagnoser]`.
