# PKRange Lookup Optimization — DirectModeRoutingBenchmark Results

End-to-end Direct-mode point-read benchmark
(`DirectModeRoutingBenchmark.ReadItemStream`) captured locally and tracked
against issue
[kirankumarkolli/ThinClient#1](https://github.com/kirankumarkolli/ThinClient/issues/1).

## Run configuration

| Setting | Value |
|---|---|
| Harness | `DirectModeRoutingBenchmark` (Performance.Tests) |
| InvocationCount | 25994 (≈1.5× `PkRangeRoutingFactory.ExpectedRowCount` = 17329) |
| UnrollFactor | 1 (required for async benchmarks) |
| Iteration sampling | `Random.Shared.Next(pkPool.Length)` over 65536-PK pool (≥2× PKRange count) |
| PK byte length | 64 |
| Address-cache warmup | Delegated to BenchmarkDotNet warmup phase |
| Runtime | .NET 8.0.26 ARM64 RyuJIT AdvSIMD |
| BenchmarkDotNet | v0.13.5 |
| OS | Windows 11 (10.0.26200.8246) |
| SDK | .NET 10.0.202 |

## Experiments

| # | Name                  | Description |
|---|-----------------------|-------------|
| A | Baseline              | `master` — no routing optimizations (PR [#3](https://github.com/kirankumarkolli/ThinClient/pull/3) baseline) |
| B | Phase 1a only         | Zero-allocation string lookup applied; `hasNumericFastPath` forced `false` locally |
| C | Phase 1a + 2a         | Zero-alloc string lookup + UInt128 numeric fast-path (auto-enabled for 32-char hex EPKs) |

## Results — `master` codebase (feature/pkrange-lookup-optimization)

| Metric    |  A — Baseline |     B — Phase 1a only |    C — Phase 1a + 2a |
|-----------|--------------:|----------------------:|---------------------:|
| Mean      |      18.21 μs | 15.68 μs (**−13.9 %**) | 15.67 μs (**−14.0 %**) |
| Error     |      0.357 μs |   0.304 μs (−14.8 %)  |    0.260 μs (−27.2 %) |
| StdDev    |      0.704 μs |   0.299 μs (−57.5 %)  |    0.243 μs (−65.5 %) |
| P90       |      18.81 μs |   16.08 μs (−14.5 %)  |    15.97 μs (−15.1 %) |
| P95       |      19.16 μs |   16.13 μs (−15.8 %)  |    16.03 μs (−16.3 %) |
| P100      |      19.41 μs |   16.19 μs (−16.6 %)  |    16.13 μs (−16.9 %) |
| Gen0      |        6.5400 |     6.5015 (−0.6 %)   |      6.5015 (−0.6 %)  |
| Gen1      |             — |                    —  |               0.0385  |
| Allocated |      26.74 KB |   26.69 KB (−0.2 %)   |    26.70 KB (−0.1 %)  |

## Results — `msdata/direct` codebase (full Direct transport source)

Same harness, same run configuration, ported onto `msdata/direct` (synced from
upstream `Azure/azure-cosmos-dotnet-v3:msdata/direct` at
`72eabd937 2026-02-11`, last refreshed from `master 3.52.1 + Direct 3.39.1`).
On this branch the Direct transport (`src/direct/RNTBD/*`, `Channel`,
`Connection`, `TransportClient`) is compiled from source rather than consumed
through the `Microsoft.Azure.Cosmos.Direct` NuGet, which lets the JIT inline
across what is otherwise an assembly boundary.

Branch: `users/kirankk/msdata-direct-pkrange-fastpath` (local).

| Metric    |  A — Baseline |     B — Phase 1a only |    C — Phase 1a + 2a |
|-----------|--------------:|----------------------:|---------------------:|
| Mean      |      15.85 μs |   15.97 μs (+0.8 %)   |   15.57 μs (**−1.8 %**) |
| Error     |      0.314 μs |   0.318 μs            |    0.305 μs           |
| StdDev    |      0.558 μs |   0.685 μs            |    0.326 μs (−41.6 %) |
| P90       |      16.70 μs |   16.91 μs            |   15.97 μs (−4.4 %)   |
| P95       |      16.78 μs |   17.45 μs            |   16.09 μs (−4.1 %)   |
| P100      |      17.22 μs |   17.84 μs            |   16.16 μs (−6.2 %)   |
| Gen0      |        6.5015 |               6.5015  |               6.5015  |
| Gen1      |             — |                    —  |               0.0769  |
| Allocated |      26.66 KB |   26.62 KB            |   26.62 KB            |

## Notes

- On `master`, all of the visible end-to-end win comes from the Phase 1a
  zero-allocation string lookup (~14 % Mean improvement). The Phase 2a UInt128
  numeric fast-path is within noise — the routing-map lookup itself
  (sub-microsecond) is dwarfed by the rest of the Direct-mode point-read path
  (~15 μs of request/response handling, RNTBD framing, address-cache hits,
  etc.). The micro-benchmark in `CollectionRoutingMapBenchmark` still shows the
  ~30 % lookup-level gain from 2a; that gain just doesn't surface at the E2E
  level until more of the surrounding overhead is eliminated.
- On `msdata/direct`, the optimization yields only ~2 % at the Mean level and
  is otherwise within noise. The baseline (15.85 μs) is already on par with
  master's *optimized* result (15.67 μs), suggesting the JIT-inlinable Direct
  source on `msdata/direct` partly captures the same wins that Phase 1a/2a
  deliver on master's Direct-package consumption path.
- Allocation is unchanged because Phase 1a/2a target CPU on the routing-map
  lookup path; the per-call request/response object graph dominates the
  allocation budget and is unaffected.
- The remaining unrealized win — threading the pre-parsed `UInt128` from
  `MurmurHash3.Hash128()` directly into `AddressResolver` to skip per-call
  hex encoding — is not yet wired in. That work is tracked as the next step
  on issue #1.
