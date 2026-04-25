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

## Results

| Metric    |    A — Baseline |    B — Phase 1a only |   C — Phase 1a + 2a |
|-----------|----------------:|---------------------:|--------------------:|
| Mean      |        18.21 μs |             15.68 μs |            15.67 μs |
| Δ vs A    |               — |              −13.9 % |             −14.0 % |
| Error     |         0.357 μs |             0.304 μs |            0.260 μs |
| StdDev    |         0.704 μs |             0.299 μs |            0.243 μs |
| P90       |        18.81 μs |             16.08 μs |            15.97 μs |
| P95       |        19.16 μs |             16.13 μs |            16.03 μs |
| P100      |        19.41 μs |             16.19 μs |            16.13 μs |
| Gen0      |          6.5400 |               6.5015 |              6.5015 |
| Gen1      |               — |                    — |              0.0385 |
| Allocated |        26.74 KB |             26.69 KB |            26.70 KB |

## Notes

- All of the visible end-to-end win at this benchmark scope comes from the
  Phase 1a zero-allocation string lookup. The Phase 2a UInt128 numeric fast-path
  is within noise here — the routing-map lookup itself (sub-microsecond) is
  dwarfed by the rest of the Direct-mode point-read path (~15 μs of
  request/response handling, RNTBD framing, address-cache hits, etc.). The
  micro-benchmark in `CollectionRoutingMapBenchmark` still shows the ~30 %
  lookup-level gain from 2a; that gain just doesn't surface at the E2E level
  until more of the surrounding overhead is eliminated.
- Allocation is unchanged because Phase 1a/2a target CPU on the routing-map
  lookup path; the per-call request/response object graph dominates the
  allocation budget and is unaffected.
- The remaining unrealized win — threading the pre-parsed `UInt128` from
  `MurmurHash3.Hash128()` directly into `AddressResolver` to skip per-call
  hex encoding — is not yet wired in. That work is tracked as the next step
  on issue #1.
