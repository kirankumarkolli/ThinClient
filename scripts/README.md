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
| `run-pkrange-sweep.ps1`          | Sweep only: invokes BDN once per (scenario, pass); BDN spawns one child process per `Profile` value. |
| `compute-pkrange-percentiles.ps1`| Aggregator: demultiplexes per-pass CSV by `Param_Profile`, computes per-pass Avg/Min/Max/P50…P99.9, renders `avg [min..max]` cells. |

## Prerequisites

- Windows PowerShell 5.1+ or PowerShell 7+
- .NET 8 SDK (the wrapper sets `DOTNET_ROLL_FORWARD=LatestMajor`)
- Repo cloned on `feature/msdata-direct-pkrange-lookup-optimization` (or descendant)

## Profiles benchmarked

The benchmark `Profile` axis (`[Params]` on `DirectModeRoutingBenchmark` and
`DirectModeRoutingRawDsrBenchmark`) names a `(variant, bypass)` tuple that
`RoutingBenchmarkProfiles.Apply` translates to:

- `FastPathVariantSelector.ActiveVariant` (which fast-path strategy)
- `AddressResolver.UseStringEpkBypass` (skip producer-side string allocation)

| Profile                | Variant         | Bypass | Notes |
| ---------------------- | --------------- | ------ | ----- |
| `string`               | `String`        | false  | baseline (legacy `SortedList`) |
| `uint128`              | `UInt128`       | false  | UInt128 numeric fast-path (V2 hash only) |
| `bytespan-seq`         | `BytespanSeq`   | false  | byte-span sequential compare |
| `bytespan-hand`        | `BytespanHand`  | false  | byte-span hand-unrolled compare |
| `bytespan-hand-bypass` | `BytespanHand`  | true   | hand-unrolled + skip string→EPK conversion (PR #9 winner) |
| `radix1`               | `Radix1`        | true   | 1-byte radix index |
| `radix2`               | `Radix2`        | true   | 2-byte radix index |
| `soa`                  | `Soa`           | true   | struct-of-arrays + bypass |
| `string-soa`           | `StringSoa`     | false  | string-keyed SoA (works on V1/hierarchical too) |
| `cache-last`           | `CacheLast`     | true   | wraps an inner strategy with a single-slot last-hit cache |

> Numeric strategies require V2-hash 32-char hex range boundaries. The factory
> falls back to `StringStrategy` on non-V2 topologies. `StringSoa` is exempted.

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
├── summary.txt                     # final per-pass aggregate table
├── summary.csv                     # same, machine-readable
├── container\
│   ├── p1.csv                      # all-profiles measurements, pass 1
│   ├── p1.log                      # full BDN console output
│   ├── p1-artifacts\               # full BDN artifact dir
│   ├── p2.csv  p2.log  p2-artifacts\
│   └── p3.csv  p3.log  p3-artifacts\
└── rawdsr\
    └── … (same structure)
```

The aggregator demultiplexes each pass CSV by the `Param_Profile` column.

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

BDN's `[Params]` mechanism cycles profiles in alpha order within each pass,
spawning one child process per profile value. Running 3 passes therefore
produces three interleaved measurements per profile across separate processes.

## Manual single-profile run

```powershell
# Pin to one Profile via BDN's --filter glob (matches param values)
dotnet Microsoft.Azure.Cosmos\tests\Microsoft.Azure.Cosmos.Performance.Tests\bin\Release\net8.0\Microsoft.Azure.Cosmos.Performance.Tests.dll `
    --filter '*DirectModeRoutingBenchmark*' --runtimes net8.0
```

(omit `--filter` to run both benchmarks; BDN will spawn one child process per
`Profile` value automatically.)

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
