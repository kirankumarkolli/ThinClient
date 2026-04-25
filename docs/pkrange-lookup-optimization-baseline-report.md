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

## Comparison

| Metric    | Baseline | Optimized | Delta |
|-----------|---------:|----------:|------:|
| Mean      | 18.21 μs | 15.67 μs  | **−14.0%** |
| StdDev    |  0.704 μs |  0.243 μs | −65.5% (tighter) |
| P90       | 18.81 μs | 15.97 μs  | −15.1% |
| P95       | 19.16 μs | 16.03 μs  | −16.3% |
| P100      | 19.41 μs | 16.13 μs  | −16.9% |
| Allocated | 26.74 KB | 26.70 KB  | ≈ flat |

## Notes

- Allocation is unchanged because Phase 1a/2a target CPU on the routing-map lookup
  path; the per-call request/response object graph dominates the allocation budget
  and is unaffected.
- The remaining unrealized win — threading the pre-parsed `UInt128` from
  `MurmurHash3.Hash128()` directly into `AddressResolver` to skip per-call
  hex encoding — is not yet wired in. That work is tracked as the next step
  on issue #1.
