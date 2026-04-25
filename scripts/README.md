# PKRange routing benchmark scripts

Tools for reproducing the PKRange-lookup fast-path optimization numbers reported in
`docs/pkrange-lookup-optimization-baseline-report.md`.

## TL;DR — one command

```powershell
.\scripts\bench-pkrange.ps1
```

Builds Performance.Tests in Release, runs all 7 fast-path variants × 2 scenarios
× 3 alternating passes, then prints a percentile table and writes
`bench-out\pkrange\summary.txt`.

## Files

| Script | Purpose |
| ------ | ------- |
| `bench-pkrange.ps1`           | One-shot wrapper: build + sweep + report. |
| `run-pkrange-soa-sweep.ps1`   | Lower-level: sweep only (used by the wrapper). |
| `compute-soa-percentiles.ps1` | Lower-level: aggregate `*-measurements.csv` into a P50/P90/P99/P99.9 table. |

## Prerequisites

- Windows PowerShell 5.1+ or PowerShell 7+
- .NET 8 SDK (the wrapper sets `DOTNET_ROLL_FORWARD=LatestMajor`)
- Repo cloned and on the routing branch
  (`feature/msdata-direct-pkrange-lookup-optimization` or descendant)

## Variants benchmarked

The wrapper sweeps the same labels used in PR #9 / PR #13:

| Label | `COSMOS_PKRANGE_VARIANT`  | `COSMOS_PKRANGE_BYPASS_STRING_EPK` | Notes |
| ----- | ------------------------- | ---------------------------------- | ----- |
| A     | `string`                  | false | baseline (legacy SortedList) |
| B     | `uint128`                 | false | UInt128 numeric fast-path |
| C     | `bytespan-seq`            | false | byte-span sequential |
| D     | `bytespan-hand`           | false | byte-span hand-unrolled |
| F     | `bytespan-hand`           | true  | D + bypass string→EPK conversion (PR #9 winner) |
| G     | `soa`                     | true  | struct-of-arrays + bypass |
| H     | `string-soa`              | false | string-keyed SoA (works on V1/hierarchical too) |

Each label spawns a fresh `dotnet` child process (BDN best practice for isolation
and to capture per-variant memory accurately).

## Scenarios

| Name        | BDN filter                                    | What it measures |
| ----------- | --------------------------------------------- | ---------------- |
| `container` | `*DirectModeRoutingBenchmark.ReadItemStream*` | Full Container.ReadItemStreamAsync path |
| `rawdsr`    | `*DirectModeRoutingRawDsrBenchmark*`          | Routing only, raw DSR (no Container plumbing) |

## Wrapper usage

```powershell
# Full sweep (~30–45 min): 7 variants × 2 scenarios × 3 passes
.\scripts\bench-pkrange.ps1

# Smoke run (~5 min): 7 variants × container only × 1 pass
.\scripts\bench-pkrange.ps1 -Quick

# Skip rebuild (artifacts already in bin\Release)
.\scripts\bench-pkrange.ps1 -SkipBuild

# Custom output location
.\scripts\bench-pkrange.ps1 -OutDir D:\bench-runs\2026-04-25
```

Parameters:

- `-RepoRoot <path>` — defaults to repo root (auto-detected from `$PSScriptRoot`).
- `-OutDir   <path>` — defaults to `<repo>\bench-out\pkrange`.
- `-SkipBuild`       — skip `dotnet build`; assumes DLL exists at
                       `Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests\bin\Release\net8.0\`.
- `-Quick`           — 1 pass, container scenario only (smoke test).

## Output layout

```
bench-out\pkrange\
├── summary.txt                          # final percentile table (also printed to stdout)
├── container\
│   ├── p1-A.csv                         # raw measurements for label A, pass 1
│   ├── p1-A.log                         # full BDN console output
│   ├── p1-A-artifacts\                  # full BDN artifact dir (markdown/html/json)
│   ├── p1-B.csv … p1-H.csv
│   ├── p2-A.csv … p2-H.csv
│   └── p3-A.csv … p3-H.csv
└── rawdsr\
    └── … (same structure)
```

The `*.csv` files are BDN's `CsvMeasurementsExporter` output (one row per
iteration per stage), pooled across the 3 passes by `compute-soa-percentiles.ps1`
to compute high-tail percentiles.

## Reading the report

The aggregator prints:

1. A per-(scenario, variant) table — `N`, `P50`, `P90`, `P95`, `P99`, `P99.9`,
   `P100` in microseconds.
2. `% delta vs A` — every variant against the legacy string baseline.
3. `G delta vs F` — SoA against the PR #9 winner.

Negative deltas = improvement. Positive deltas = regression.

## Why 3 alternating passes?

Single-shot benchmark runs hide ±2–5% across-process drift on Windows
(thermal throttling, scheduler noise, JIT tier-0 vs tier-1). Running each
variant 3× in alternating order across separate processes separates signal from
drift. This methodology is documented at the repo level in PR #9.

## Manual runs

If you want to skip the wrapper and just see BDN's built-in markdown table
for one variant:

```powershell
$env:COSMOS_PKRANGE_VARIANT = 'soa'
$env:COSMOS_PKRANGE_BYPASS_STRING_EPK = 'true'
dotnet Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests\bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll `
    --filter '*DirectModeRoutingBenchmark*'
```

The markdown table lands in `BenchmarkDotNet.Artifacts\results\*.md`.

## Parametric range-count sweep

`DirectModeRoutingBenchmark` uses production-shaped data
(`Data/shared_conversations_pkranges.tsv`, fixed range count). For per-N
microbenchmarks at 100 / 1k / 10k / 50k partitions use:

```powershell
dotnet Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests\bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll `
    --filter '*CollectionRoutingMapBenchmark*'
```

That benchmark already has `[Params(100, 1_000, 10_000, 50_000)]` and
`[MemoryDiagnoser]` enabled.
