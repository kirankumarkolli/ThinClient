# PKRange Lookup Optimization — DirectModeRoutingBenchmark Results

End-to-end Direct-mode point-read benchmark (`DirectModeRoutingBenchmark.ReadItemStream`)
captured locally and tracked against issue
[kirankumarkolli/ThinClient#1](https://github.com/kirankumarkolli/ThinClient/issues/1).

## Run configuration

| Setting | Value |
|---|---|
| Harness | `DirectModeRoutingBenchmark` (Performance.Tests) |
| InvocationCount | 25994 (≈1.5× `PkRangeRoutingFactory.ExpectedRowCount` = 17329) |
| UnrollFactor | 1 (required for async benchmarks) |
| Iteration sampling | `Random.Shared.Next(pkPool.Length)` over 65536 PK pool (≥2× PKRange count) |
| PK byte length | 64 |
| Address-cache warmup | Delegated to BenchmarkDotNet warmup phase |
| Runtime | .NET 8.0.26 ARM64 RyuJIT AdvSIMD |
| BenchmarkDotNet | v0.13.5 |
| OS | Windows 11 (10.0.26200.8246) |
| SDK | .NET 10.0.202 |

## Baseline — `master` (no routing optimizations)

Branch: `users/kirankk/bench-direct-pointread` (PR
[#3](https://github.com/kirankumarkolli/ThinClient/pull/3), merged into
`feature/pkrange-lookup-optimization`).

```
|         Method |     Mean |    Error |   StdDev |      P90 |      P95 |     P100 |   Gen0 | Allocated |
|--------------- |---------:|---------:|---------:|---------:|---------:|---------:|-------:|----------:|
| ReadItemStream | 18.21 us | 0.357 us | 0.704 us | 18.81 us | 19.16 us | 19.41 us | 6.5400 |  26.74 KB |
```

## Optimized — Phase 1a + 2a applied

Local merge of `feature/pkrange-lookup-optimization` (with the harness) +
`users/kirankk/optimize-pkrange-lookup` commits:

- `10779d286` Routing: zero-allocation point lookup for `PartitionKeyRange`
- `8e9b45143` Routing: UInt128 numeric fast-path for partition key range lookup

Captured locally; not yet published.

```
|         Method |     Mean |    Error |   StdDev |      P90 |      P95 |     P100 |   Gen0 |   Gen1 | Allocated |
|--------------- |---------:|---------:|---------:|---------:|---------:|---------:|-------:|-------:|----------:|
| ReadItemStream | 15.67 us | 0.260 us | 0.243 us | 15.97 us | 16.03 us | 16.13 us | 6.5015 | 0.0385 |   26.7 KB |
```

## Optimized — Phase 1a only (numeric fast-path forced off)

Same merged branch, with `CollectionRoutingMap.hasNumericFastPath` forced to
`false` locally to isolate the contribution of the zero-allocation string path
from the UInt128 fast path. Local-only edit, not committed.

```
|         Method |     Mean |    Error |   StdDev |      P90 |      P95 |     P100 |   Gen0 | Allocated |
|--------------- |---------:|---------:|---------:|---------:|---------:|---------:|-------:|----------:|
| ReadItemStream | 15.68 us | 0.304 us | 0.299 us | 16.08 us | 16.13 us | 16.19 us | 6.5015 |  26.69 KB |
```

## Comparison

| Metric    | Baseline | Optimized (1a + 2a) | Phase 1a only (2a off) |
|-----------|---------:|--------------------:|-----------------------:|
| Mean      | 18.21 μs | 15.67 μs (−14.0%)   | 15.68 μs (−13.9%)      |
| StdDev    |  0.704 μs |  0.243 μs           |  0.299 μs              |
| P90       | 18.81 μs | 15.97 μs (−15.1%)   | 16.08 μs (−14.5%)      |
| P95       | 19.16 μs | 16.03 μs (−16.3%)   | 16.13 μs (−15.8%)      |
| P100      | 19.41 μs | 16.13 μs (−16.9%)   | 16.19 μs (−16.6%)      |
| Allocated | 26.74 KB | 26.70 KB            | 26.69 KB               |

## Notes

- All of the visible end-to-end win at this benchmark scope comes from the
  Phase 1a zero-allocation string lookup. The Phase 2a UInt128 numeric fast-path
  is within noise at this granularity — the routing-map lookup itself
  (sub-microsecond) is dwarfed by the rest of the Direct-mode point-read path
  (~15 μs of request/response handling, RNTBD framing, address-cache hits,
  etc.). The micro-benchmark in `CollectionRoutingMapBenchmark` still shows the
  ~30% lookup-level gain from 2a; that gain just doesn't surface at the E2E
  level until more of the surrounding overhead is eliminated.
- Allocation is unchanged because Phase 1a/2a target CPU on the routing-map
  lookup path; the per-call request/response object graph dominates the
  allocation budget and is unaffected.
- The remaining unrealized win — threading the pre-parsed `UInt128` from
  `MurmurHash3.Hash128()` directly into `AddressResolver` to skip per-call
  hex encoding — is not yet wired in. That work is tracked as the next step
  on issue #1.
