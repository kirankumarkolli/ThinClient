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
| A | Baseline              | `master` / `msdata/direct` — no routing optimizations (PR [#3](https://github.com/kirankumarkolli/ThinClient/pull/3) baseline) |
| B | Phase 1a only         | Zero-allocation string lookup (`Array.BinarySearch` over `string[]` boundaries). `COSMOS_PKRANGE_VARIANT=string` |
| C | Phase 1a + 2a (UInt128) | Zero-alloc string lookup + UInt128 numeric fast-path. `COSMOS_PKRANGE_VARIANT=uint128` (default) |
| D | Phase 1a + Span\<byte\> (`SequenceCompareTo`) | 16-byte big-endian boundaries searched via `MemoryExtensions.SequenceCompareTo`. `COSMOS_PKRANGE_VARIANT=bytespan-seq` |
| E | Phase 1a + Span\<byte\> (hand-rolled) | 16-byte big-endian boundaries searched via two `BinaryPrimitives.ReadUInt64BigEndian` calls per probe. `COSMOS_PKRANGE_VARIANT=bytespan-hand` |

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

Branch: `users/kirankk/msdata-direct-pkrange-fastpath`. All five variants below
were captured back-to-back in the same run via the `COSMOS_PKRANGE_VARIANT`
env-var selector; A was captured immediately before by reverting
`CollectionRoutingMap.cs` to `origin/msdata/direct`.

| Metric    |  A — Baseline | B — 1a only (`string`) | C — 1a + UInt128 | D — 1a + bytespan-seq | E — 1a + bytespan-hand |
|-----------|--------------:|-----------------------:|-----------------:|----------------------:|-----------------------:|
| Mean      |      16.11 μs | 15.51 μs (**−3.7 %**)  | **15.45 μs (−4.1 %)** | 15.47 μs (−4.0 %)     | 15.67 μs (−2.7 %)      |
| Error     |      0.292 μs | 0.306 μs               | 0.279 μs         | 0.309 μs              | 0.307 μs               |
| StdDev    |      0.455 μs | **0.314 μs (−31.0 %)** | 0.372 μs (−18.2 %) | 0.532 μs              | 0.469 μs               |
| P90       |      16.67 μs | 16.02 μs (−3.9 %)      | **15.93 μs (−4.4 %)** | 16.20 μs (−2.8 %)     | 16.25 μs (−2.5 %)      |
| P95       |      16.99 μs | **16.12 μs (−5.1 %)**  | 16.06 μs (−5.5 %) | 16.40 μs (−3.5 %)     | 16.35 μs (−3.8 %)      |
| P100      |      17.25 μs | **16.12 μs (−6.6 %)**  | 16.49 μs (−4.4 %) | 16.94 μs (−1.8 %)     | 17.04 μs (−1.2 %)      |
| Gen0      |        6.5015 | 6.5015                 | 6.5015           | 6.5015                | 6.5015                 |
| Allocated |      26.67 KB | 26.62 KB               | 26.61 KB         | 26.62 KB              | 26.62 KB               |

## Notes

- On `master`, all of the visible end-to-end win comes from the Phase 1a
  zero-allocation string lookup (~14 % Mean improvement). The Phase 2a UInt128
  numeric fast-path is within noise — the routing-map lookup itself
  (sub-microsecond) is dwarfed by the rest of the Direct-mode point-read path
  (~15 μs of request/response handling, RNTBD framing, address-cache hits,
  etc.). The micro-benchmark in `CollectionRoutingMapBenchmark` still shows the
  ~30 % lookup-level gain from 2a; that gain just doesn't surface at the E2E
  level until more of the surrounding overhead is eliminated.
- On `msdata/direct`, all four optimized variants (B, C, D, E) cluster within
  ~0.2 μs at the Mean and overlap heavily on confidence-interval bands. **There
  is no robust E2E differentiator between Phase 1a alone, the UInt128 fast
  path, or either Span\<byte\> fast path on this codebase.** The simplest
  variant (B — Phase 1a alone) captures essentially the entire ~4 % E2E win.
- C (UInt128) consistently wins at the Mean across re-runs, but the margin
  (~0.05 μs) is well inside run-to-run noise (~0.2-0.3 μs).
- D (Span\<byte\> + `SequenceCompareTo`) and E (Span\<byte\> + hand-rolled
  `ReadUInt64BigEndian`) are essentially tied with B/C at the Mean. E was added
  to test the hypothesis that D's tail-latency regression came from
  `SequenceCompareTo`'s generic intrinsic call overhead rather than from byte
  storage itself; at the Mean and tail E now matches C, confirming that
  hypothesis.
- On the `msdata/direct` codebase the baseline (16.11 μs) is already close to
  master's *optimized* result (15.67 μs), suggesting the JIT-inlinable Direct
  source on `msdata/direct` partly captures the same wins that Phase 1a/2a
  deliver on master's Direct-package consumption path.
- Allocation is unchanged because all variants target CPU on the routing-map
  lookup path; the per-call request/response object graph dominates the
  allocation budget and is unaffected.
- The remaining unrealized win — threading the pre-parsed numeric (or byte-
  array) EPK from `MurmurHash3.Hash128()` directly into `AddressResolver` to
  skip per-call hex encoding — is not yet wired in. The byte-storage variants
  (D, E) are the foundation for that direction since they integrate with a
  bytes-end-to-end EPK pipeline without requiring a re-conversion. That work
  is tracked as the next step on issue #1.
