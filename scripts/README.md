# PKRange routing benchmark scripts

Tools for reproducing the PKRange-lookup fast-path optimization numbers reported in
[`docs/pkrange-lookup-optimization-baseline-report.md`](../docs/pkrange-lookup-optimization-baseline-report.md).

## TL;DR — one command

```powershell
.\scripts\bench-pkrange.ps1
```

Builds Performance.Tests in Release, runs all 9 fast-path variants × the
`rawdsr` scenario × 3 alternating passes, then prints a percentile table where
each cell shows `avg [min..max]` across the 3 passes.

To include the `container` scenario as well, pass `-Scenarios`:

```powershell
.\scripts\bench-pkrange.ps1 -Scenarios rawdsr,container
```

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

> Numeric strategies require V2-hash 32-char hex range boundaries. The factory
> falls back to `StringStrategy` on non-V2 topologies. `StringSoa` is exempted.

## Scenarios

| Name                  | BDN filter                                              | What it measures |
| --------------------- | ------------------------------------------------------- | ---------------- |
| `container`           | `*DirectModeRoutingBenchmark.ReadItemStream*`           | Full `Container.ReadItemStreamAsync` path (lookup latency, alloc-free hot path) |
| `rawdsr`              | `*DirectModeRoutingRawDsrBenchmark.ReadViaRawDsr*`      | Routing only, raw DSR (no Container plumbing) — lookup latency |
| `rawdsr-construction` | `*DirectModeRoutingRawDsrConstructionBenchmark*`        | `CollectionRoutingMap` build cost at 17K ranges — **memory** via `[MemoryDiagnoser]` |

The construction scenario is **opt-in**: not run by `bench-pkrange.ps1` default (it's
the only place memory savings show up; the lookup scenarios are alloc-free for every
profile). Run it explicitly via `run-pkrange-sweep.ps1 -Scenarios rawdsr-construction`
and read the BDN summary report (`p<N>-artifacts\…\*-report.md`) for the
**`Allocated`** column — that is the headline number for "string vs UInt128" memory
savings (expect ~80% reduction for numeric variants at 17K ranges).

## Wrapper usage

```powershell
# Default sweep: 9 variants × rawdsr × 3 passes
.\scripts\bench-pkrange.ps1

# Smoke run: 9 variants × rawdsr × 1 pass
.\scripts\bench-pkrange.ps1 -Quick

# Smoke mode: ~2-minute script-validation run (1000 invocations × 3 iterations,
# 1 pass, no aggregation). NOT for honest perf comparisons.
.\scripts\bench-pkrange.ps1 -Smoke

# Include container scenario alongside rawdsr
.\scripts\bench-pkrange.ps1 -Scenarios rawdsr,container

# Container only
.\scripts\bench-pkrange.ps1 -Scenarios container
# Memory benchmark (RawDSR routing-map construction @ 17K ranges, 9 profiles)
# Use BDN's MemoryDiagnoser column in the *-report.md artifact for the headline
# "Allocated bytes per build" number.
.\scripts\run-pkrange-sweep.ps1 -Scenarios rawdsr-construction -Passes 1

# Skip rebuild (artifacts already in bin\Release)
.\scripts\bench-pkrange.ps1 -SkipBuild

# Custom output location
.\scripts\bench-pkrange.ps1 -OutDir D:\bench-runs\2026-04-25
```

Parameters:

- `-RepoRoot <path>`     — auto-detected from `$PSScriptRoot`.
- `-OutDir <path>`       — defaults to `<repo>\bench-out\pkrange\<run-|smoke->yyyy-MM-dd_HHmmss\` (each invocation gets its own timestamped folder so successive runs don't overwrite). Pin a specific path when feeding the aggregator manually.
- `-SkipBuild`           — skip `dotnet build`; assumes DLL exists.
- `-Quick`               — 1 pass instead of 3 (uses the same `-Scenarios` set).
- `-Smoke`               — fast script-validation run: passes `--invocationCount 1000 --warmupCount 2 --iterationCount 3` to BDN, forces 1 pass, and skips percentile aggregation. Whole sweep finishes in ~2 minutes. Results have very wide error bars; use for plumbing validation only.
- `-Scenarios <names>`   — scenarios to run; defaults to `@('rawdsr')`. Valid: `rawdsr`, `container`, `rawdsr-construction`.

`run-pkrange-sweep.ps1` accepts the same `-Scenarios` parameter directly when
you don't need the build/aggregation wrapper.

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
