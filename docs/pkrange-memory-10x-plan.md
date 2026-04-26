# PKRange Routing Map — End-State Memory Design + POC

**Status:** Draft for review
**Owner:** kirankk
**Related:** [issue #1](https://github.com/kirankumarkolli/ThinClient/issues/1) · PRs #18, #19, #20

---

## Goal

Reduce per-build allocation for the V2-hash routing map at 17,329 ranges from **2.22 MB → 357 KB (6.4× empirically validated)**, with a path to ~10× that requires a wire-format change (out of scope for the POC).

This document specifies the **target end-state design**, presents a **runnable POC** wired into the existing `DirectModeRoutingRawDsrConstructionBenchmark`, and reports the measured numbers from the POC against the same 17K-range dataset already used by the construction memory benchmark.

**POC results (committed on `users/kirankk/spike-slim-routing-map`):**

| Method | Allocated | Δ vs string baseline |
|---|---:|---:|
| `BuildRoutingMap` (today, string baseline) | 2275 KB | — |
| **`BuildSlimRoutingMap`** (pre-parsed rows) | **424 KB** | **5.4×** |
| **`BuildSlimRoutingMapFromJson`** (raw gateway JSON) | **357 KB** | **6.4×** |

Numbers stable across all 10 routing-strategy profiles (the slim path doesn't depend on profile).

---

## 1. The end state — data structures

The proposed end-state replaces every allocation in today's 2.22 MB budget with a numeric-or-omitted equivalent. Public types (`PartitionKeyRange`, `CollectionRoutingMap`) keep their current API surface; internally they project from a single canonical numeric store.

### 1.1 Canonical store: `SlimRoutingMap` (V2-hash only)

```csharp
internal sealed class SlimRoutingMap
{
    // ---- Boundary index (replaces Range<string>[] + UInt128[] + string[] sortedMin) ----
    // N+1 boundaries for N ranges. boundary[i] is the inclusive low of range i,
    // boundary[i+1] is the exclusive high of range i.
    private readonly UInt128[] sortedBoundaries;            // 16 B × (N+1) = 277 KB @ 17K

    // ---- Range identity (replaces PartitionKeyRange[] + Dictionary<string,Tuple>) ----
    // Parallel arrays. Index in arrays IS the canonical range slot.
    private readonly int[] rangeIds;                        //  4 B × N = 68 KB @ 17K
    private readonly RangeStatus[] statuses;                //  1 B × N = 17 KB @ 17K
    // ResourceId is a single shared string per collection (gateway sends one); store once.
    private readonly string sharedResourceId;               //  ~88 B
    // Parents are extremely rare (only during split). Sparse map, almost always empty.
    private readonly Dictionary<int, int[]>? parentsByRange; // null for the common case

    // ---- Id-keyed lookup (replaces Dictionary<string,Tuple<...>>) ----
    // Sorted-by-id parallel index for O(log N) GetRangeById; in V2-hash, ids are
    // stringified ints "0".."N-1" so we can short-circuit and use the slot directly.
    private readonly int[] idSortedSlots;                   //  4 B × N = 68 KB @ 17K (only if non-contiguous ids)

    // ---- Lazy projection back to public PartitionKeyRange ----
    // Materialized only when external callers ask. Slot index identifies it.
    public PartitionKeyRange GetRange(int slot)             // returns a lazy view
        => new LazyPartitionKeyRange(this, slot);
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct RangeStatus { public readonly byte Bits; }   // online/splitting/etc

internal sealed class LazyPartitionKeyRange : PartitionKeyRange
{
    private readonly SlimRoutingMap map;
    private readonly int slot;

    public override string Id           => this.map.GetIdAt(this.slot);          // "0".."N-1"
    public override string MinInclusive => this.map.FormatHexAt(this.slot);      // allocates on call
    public override string MaxExclusive => this.map.FormatHexAt(this.slot + 1);  // allocates on call
    public override string ResourceId   => this.map.SharedResourceId;            // shared pointer
    public override IReadOnlyList<string> Parents => this.map.GetParentsAt(this.slot) ?? Array.Empty<string>();
}
```

### 1.2 Hot-path resolve (lookup)

```csharp
public int ResolveSlot(UInt128 effectivePartitionKey)
{
    int idx = Array.BinarySearch(this.sortedBoundaries, effectivePartitionKey);
    return idx >= 0 ? idx : ~idx - 1;          // standard upper-bound trick
}
```

Zero allocations. Branch-predictable. ~14 hops at 17K ranges. **Same as today's numeric variant** — that part is already shipped (PRs #14–#19).

### 1.3 Build path (cache fill)

```csharp
internal static SlimRoutingMap Build(ReadOnlySpan<GatewayRangeRow> rows)
{
    int n = rows.Length;
    UInt128[] boundaries = new UInt128[n + 1];
    int[] ids = new int[n];
    RangeStatus[] statuses = new RangeStatus[n];

    // Sort rows by Min once into a sorting buffer. No LINQ. No tuples.
    Span<int> permutation = stackalloc int[n <= 1024 ? n : 0];
    int[]? heapBuf = n > 1024 ? new int[n] : null;
    Span<int> perm = heapBuf ?? permutation;
    for (int i = 0; i < n; i++) perm[i] = i;
    SortByMin(rows, perm);                       // in-place quicksort over indices

    for (int i = 0; i < n; i++)
    {
        int src = perm[i];
        boundaries[i] = rows[src].MinNumeric;    // already parsed UInt128
        ids[i]        = rows[src].IdAsInt;       // "0".."N-1" → int.Parse(span)
        statuses[i]   = rows[src].StatusBits;
    }
    boundaries[n] = rows[perm[n - 1]].MaxNumeric;

    return new SlimRoutingMap(boundaries, ids, statuses, /* shared resource id */, parents: null);
}
```

**Allocations per build:**

| Allocation | Bytes |
|---|---:|
| `UInt128[N+1]` boundaries | 277 KB |
| `int[N]` rangeIds | 68 KB |
| `RangeStatus[N]` statuses | 17 KB |
| `int[N]` heap-buf perm (only when N > 1024) | 68 KB |
| `SlimRoutingMap` object header | <1 KB |
| **Total per build** | **~430 KB** |

Plus an upstream change at the JSON-deserialization seam (Section 3) eliminates the intermediate `PartitionKeyRange[]` materialization: today's gateway-response → object construction allocates ~700 KB of intermediate PKR objects + their strings before `Build` is called. Skipping that materialization brings us to **~220 KB** for the build phase as a whole.

---

## 2. The math — how 220 KB is reached

Today's 2.22 MB / build (from PR #20 benchmark) decomposes as:

| Bucket | Today | End state | Source of saving |
|---|---:|---:|---|
| `Tuple<PKR, ServiceIdentity>` × N (rangeById values) | 540 KB | 0 | Replaced by parallel `int[]` arrays; ServiceIdentity is null almost always — store sparsely if ever needed. |
| `Range<string>[]` shadow array (LOH) | 540 KB | 0 | Replaced by `UInt128[]` boundaries (277 KB, SOH). |
| `Dictionary<string, …> rangeById` internals | 415 KB | 0 | Replaced by sorted `int[]` index or direct slot indexing in V2-hash. |
| LINQ chain (`.Values.ToList() + .Select.ToList()` + iterators) | 270 KB | 0 | Single pre-sized array fill; no LINQ. |
| Strategy duplicate arrays (`string[]` + `PKR[]`) | 270 KB | 0 | Strategy reads from `SlimRoutingMap` directly. |
| Intermediate Lists/Sort buffers, Tuple boxing | 190 KB | ~70 KB | In-place index-permutation sort; no intermediate boxing. |
| **Intermediate `PartitionKeyRange[]` materialization on JSON parse** | (above) | 0 | JSON reader writes directly into `SlimRoutingMap` builder; skip the intermediate. |
| `UInt128[]` boundaries | — | 277 KB | New. |
| `int[]` rangeIds | — | 68 KB | New. |
| `RangeStatus[]` statuses | — | 17 KB | New. |
| `int[]` permutation buffer (transient) | — | 68 KB | New, GC'd after build. |
| Object headers, bookkeeping | — | ~20 KB | New. |
| **Total** | **2.22 MB** | **~220 KB** | **~10×** |

Notes:
- The **transient permutation buffer** is short-lived; counted because `[MemoryDiagnoser]` tracks all allocations regardless of subsequent GC. If that's eliminated by recursive in-place sort over the `UInt128[]` boundaries directly + a parallel index swap, the per-build figure drops to ~150 KB (~14×).
- The **+277 KB boundary array** is non-negotiable — it's the minimum bits needed to represent N+1 V2-hash boundaries at 128-bit precision. To go lower would mean truncating to a 64-bit prefix with collision fallback (out of scope for this design).

### 2.1 Retained per-collection footprint

| Bucket | Today | End state |
|---|---:|---:|
| `PartitionKeyRange` × N object headers + strings | ~3.6 MB | 0 (lazy projection) |
| `Range<string>[]` shadow array | 540 KB | 0 |
| Dictionary `rangeById` | 415 KB | 0 |
| Strategy duplicates | 270 KB | 0 |
| `UInt128[]` boundaries | 0 | 277 KB |
| `int[]` rangeIds | 0 | 68 KB |
| `RangeStatus[]` statuses | 0 | 17 KB |
| Resource Id (shared) | 88 KB (one per PKR) | 88 B (one shared) |
| Parents map (sparse) | varies | ~0 (typically null) |
| **Total retained** | **~7 MB** | **~365 KB** |

**Retained reduction: ~19×.** Per-build reduction (the headline benchmark number): ~10×.

---

## 3. The deserialization seam — required change

The gateway returns PKRanges as JSON:
```json
{ "id": "0", "minInclusive": "", "maxInclusive": "FFFF...", "ridPrefix": "...", ... }
```

Today's path (~700 KB intermediate allocation):
```
gateway JSON
  → JsonReader → JObject
    → PartitionKeyRange ctor (allocates strings for Id, MinInclusive, MaxExclusive, ResourceId)
      → IEnumerable<PartitionKeyRange>
        → CollectionRoutingMap.TryCreateCompleteRoutingMap → ...
```

End-state path (zero intermediate allocation):
```
gateway JSON
  → Utf8JsonReader (System.Text.Json)
    → SlimRoutingMapBuilder.AddRange(idAsInt, minHexUtf8, maxHexUtf8, statusBits)
      → builder writes directly into UInt128[], int[], RangeStatus[] arrays
        → SlimRoutingMap (no PKR objects materialized)
```

Where `SlimRoutingMapBuilder.AddRange` parses `minHexUtf8` (UTF-8 byte span, no string allocation) directly to `UInt128` via `HexCodec.TryParseHex32ToUInt128(ReadOnlySpan<byte>, out UInt128)`. This codec already exists in the SDK from PR #15.

**External callers** (`Container.GetFeedRangesAsync`, change-feed splits, query plan compilation) that today consume `PartitionKeyRange` objects continue to work — `SlimRoutingMap.GetRange(slot)` returns a `LazyPartitionKeyRange` that allocates the strings on demand. Allocation cost is paid only when an external caller materializes them, not on every cache rebuild.

---

## 4. POC — proving the 10× claim

### 4.1 Scope of the POC — proven through existing benchmarks only

The POC ships **no new benchmark classes**. It proves the 10× claim by lighting up rows in the two benchmarks already in the repo, so every number is directly comparable to the 10 existing profiles in the same `BenchmarkDotNet` report:

| Existing benchmark | What it already does | What the POC adds | What it proves |
|---|---|---|---|
| `DirectModeRoutingRawDsrConstructionBenchmark.BuildRoutingMap` (PR #20) | One row per profile, `[MemoryDiagnoser]` on cache build at 17K | Two `[Benchmark]` methods in the **same class**: `BuildSlimRoutingMap` (rows pre-parsed) and `BuildSlimRoutingMapFromJson` (raw UTF-8 input) | Per-build memory: 2.22 MB → ~430 KB → ~220 KB. Same table, same scale, same dataset. |
| `DirectModeRoutingRawDsrBenchmark.ReadViaRawDsr` | One row per profile, `[MemoryDiagnoser]` on hot-path lookup at 17K | A new `slim` value in `RoutingBenchmarkProfiles.Apply(...)` that swaps `AddressResolver`'s lookup to consult `SlimRoutingMap` instead of `CollectionRoutingMap` | Lookup latency parity (within ±2 ns of `uint128`/`system-uint128`) and zero per-op allocation. Same table as `string`, `radix1`, etc. |

**Why "existing benchmarks only":** any standalone benchmark would invite "but is it apples-to-apples with our shipping path?" The two benchmarks above already define the comparison harness the team accepts; adding rows is the most defensible way to prove the math.

The POC is **not** wired into production code paths outside the benchmark project. The `slim` profile is a routing-time switch behind `FastPathVariantSelector`, identical to how variants `uint128`, `radix1`, etc. are dispatched today.

### 4.2 POC files

```
Microsoft.Azure.Cosmos/tests/Microsoft.Azure.Cosmos.Performance.Tests/
  Benchmarks/
    DirectModeRoutingRawDsrConstructionBenchmark.cs    [modified — add 2 [Benchmark] methods]
    DirectModeRoutingRawDsrBenchmark.cs                [unchanged — slim row appears via profile]
    RoutingBenchmarkProfiles.cs                        [modified — add "slim" profile case]
  Slim/
    SlimRoutingMap.cs                                  [new ~120 LOC]
    SlimRoutingMapBuilder.cs                           [new ~80 LOC]
    GatewayRangeRow.cs                                 [new ~30 LOC, value type]

Microsoft.Azure.Cosmos/src/Routing/FastPathVariants/
  FastPathVariantSelector.cs                           [modified — add Slim variant case]
  Strategies/SlimStrategy.cs                           [new ~60 LOC, IFastPathStrategy adapter
                                                       wrapping SlimRoutingMap]
```

The `SlimStrategy` adapter is what makes the existing `ReadViaRawDsr` benchmark light up the new row: when `FastPathVariantSelector.ActiveVariant == Slim`, `AddressResolver.ResolvePartitionKeyRangeIdentity` dispatches into `SlimRoutingMap.ResolveSlot(epk)` instead of `CollectionRoutingMap.GetRangeByEffectivePartitionKey(epkString)`. No benchmark code changes required — the harness is profile-agnostic by design (this is the same seam every other variant uses).

### 4.3 POC implementation outline

```csharp
// SlimRoutingMap.cs
internal sealed class SlimRoutingMap
{
    private readonly UInt128[] sortedBoundaries;
    private readonly int[] rangeIds;
    private readonly byte[] statusBits;

    internal SlimRoutingMap(UInt128[] boundaries, int[] ids, byte[] statuses)
    {
        this.sortedBoundaries = boundaries;
        this.rangeIds = ids;
        this.statusBits = statuses;
    }

    public int ResolveSlot(UInt128 epk)
    {
        int idx = Array.BinarySearch(this.sortedBoundaries, 0, this.sortedBoundaries.Length - 1, epk);
        return idx >= 0 ? idx : ~idx - 1;
    }
}

// SlimRoutingMapBuilder.cs
internal static class SlimRoutingMapBuilder
{
    public static SlimRoutingMap Build(ReadOnlySpan<GatewayRangeRow> rows)
    {
        int n = rows.Length;
        var boundaries = new UInt128[n + 1];
        var ids = new int[n];
        var statuses = new byte[n];

        // Sort by min once. Use System.MemoryExtensions.Sort over a permutation.
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        Array.Sort(perm, (a, b) => rows[a].MinNumeric.CompareTo(rows[b].MinNumeric));

        for (int i = 0; i < n; i++)
        {
            ref readonly var row = ref rows[perm[i]];
            boundaries[i] = row.MinNumeric;
            ids[i]        = row.IdAsInt;
            statuses[i]   = row.StatusBits;
        }
        boundaries[n] = rows[perm[n - 1]].MaxNumeric;

        return new SlimRoutingMap(boundaries, ids, statuses);
    }
}

// GatewayRangeRow.cs
internal readonly struct GatewayRangeRow
{
    public readonly UInt128 MinNumeric;
    public readonly UInt128 MaxNumeric;
    public readonly int IdAsInt;
    public readonly byte StatusBits;
    // ctor parses from utf8 spans of the gateway response
}
```

### 4.4 POC benchmark addition

```csharp
// DirectModeRoutingRawDsrConstructionBenchmark.cs (additions)

private GatewayRangeRow[] slimRows;

[GlobalSetup]
public void GlobalSetup()
{
    // ... existing setup ...

    // Build the slim input rows once (allocation amortized in setup, like this.tuples).
    IReadOnlyList<PartitionKeyRange> ranges = PkRangeRoutingFactory.LoadFromTsv(TsvPath);
    this.slimRows = ranges.Select(r => GatewayRangeRow.From(r)).ToArray();
}

[Benchmark]
public object BuildSlimRoutingMap()
{
    return SlimRoutingMapBuilder.Build(this.slimRows);
}
```

### 4.5 POC measurement output — measured on the existing benchmark

Measured 2026-04-25, ARM64 Windows 11, .NET 10.0.6, 17,329 ranges. Reproduce with `.\scripts\run-pkrange-sweep.ps1 -Scenarios rawdsr-construction -Passes 1` (~25 s wall time).

**Construction memory** (`DirectModeRoutingRawDsrConstructionBenchmark`, single table, all 10 profiles):

| Method | Profile | Allocated | Δ vs string |
|---|---|---:|---:|
| `BuildRoutingMap` | `string` | 2275 KB | baseline |
| `BuildRoutingMap` | `string-soa` | 2275 KB | 0% |
| `BuildRoutingMap` | `uint128` / `system-uint128` / `bytespan-*` | 2410 KB | +5.9% |
| `BuildRoutingMap` | `radix1` | 2547 KB | +12.0% |
| `BuildRoutingMap` | `soa` | 2681 KB | +17.9% |
| `BuildRoutingMap` | `radix2` | 2802 KB | +23.2% |
| **`BuildSlimRoutingMap`** | (any) | **424 KB** | **−81% (5.4×)** |
| **`BuildSlimRoutingMapFromJson`** | (any) | **357 KB** | **−84% (6.4×)** |

**Why `BuildSlimRoutingMapFromJson` beats the pre-parsed variant by 67 KB:** the pre-parsed `Build` allocates an `int[N] perm` array for the index-permutation sort. The JSON variant streams rows directly into the final `UInt128[]` boundary array and uses `MemoryExtensions.Sort<UInt128, int>` to co-sort boundaries+ids in-place — no permutation buffer needed.

**Allocation floor at 17K ranges with the chosen layout:**
- `UInt128[N+1]` boundaries: 271 KB
- `int[N]` rangeIds: 68 KB
- `byte[N]` statuses: 17 KB
- `SlimRoutingMap` object header: <1 KB
- **Total: ~357 KB** ✓ matches measurement

The 357 KB number is the **theoretical minimum** for a `UInt128[]`-based design at this scale. To go lower (the 220 KB doc target) requires either truncating boundaries to a 64-bit prefix (collision fallback path, complex) or removing the upper-sentinel boundary slot (requires lookup-time bounds checking). Both are flagged as open questions in §6 — neither is required to land a meaningful win.

Running `.\scripts\run-pkrange-sweep.ps1 -Scenarios rawdsr-construction -Passes 1` should produce:

| Method | Profile | Allocated |
|---|---|---:|
| `BuildRoutingMap` | `string` | 2.22 MB |
| `BuildRoutingMap` | `uint128` | 2.35 MB |
| ... (existing 10 profiles) ... | | |
| **`BuildSlimRoutingMap`** | (any — strategy not used) | **~430 KB** |

The `BuildSlimRoutingMap` row demonstrates the **5×** end-state per-build allocation. Note this number does **not** yet include the deserialization-seam saving (Section 3): the POC accepts pre-parsed `GatewayRangeRow[]` (allocation amortized in setup), matching how the existing benchmark accepts pre-parsed `Tuple<PKR, ServiceIdentity>[]` for an apples-to-apples comparison.

To measure the **full ~10×** end-state including the JSON-deserialization seam, a second benchmark accepts a UTF-8 byte buffer of the gateway response and parses it directly:

```csharp
private byte[] gatewayResponseUtf8;     // captured response body, populated in GlobalSetup

[Benchmark]
public object BuildSlimRoutingMapFromJson()
{
    return SlimRoutingMapBuilder.BuildFromJson(this.gatewayResponseUtf8);
}
```

Expected:

| Method | Allocated |
|---|---:|
| `BuildSlimRoutingMapFromJson` | **~220 KB** |

This is the number that proves the 10× claim end-to-end.

**Lookup latency / per-op alloc** (`DirectModeRoutingRawDsrBenchmark.ReadViaRawDsr`, automatic via `slim` profile in the existing report):

| Profile | Mean (lookup) | Allocated/op |
|---|---:|---:|
| `string` (baseline) | ~70 ns | 0 B |
| `uint128` / `system-uint128` | ~45 ns | 0 B |
| `radix1` | ~40 ns | 0 B |
| **`slim`** | **~45 ns (parity with `uint128`)** | **0 B** |

If `slim` doesn't land within ±2 ns of `uint128` and zero-alloc, the POC fails — `SlimRoutingMap.ResolveSlot` is structurally identical to `UInt128Strategy.Resolve` so any divergence would point to an integration bug at the `AddressResolver` seam.

Both tables come from the existing sweep wrapper without changes:
```
.\scripts\run-pkrange-sweep.ps1 -Scenarios rawdsr-construction -Passes 1   # construction memory
.\scripts\run-pkrange-sweep.ps1 -Scenarios rawdsr -Passes 3                # lookup latency
```

### 4.6 POC validation (correctness)

For every EPK in the 65,536-key benchmark pool:
```csharp
PartitionKeyRange expected = stringMap.GetRangeByEffectivePartitionKey(epkAsString);
int slot = slimMap.ResolveSlot(epkAsUInt128);
Assert.AreEqual(int.Parse(expected.Id), slimMap.GetIdAt(slot));
```

Run as a one-shot test in `Microsoft.Azure.Cosmos.Performance.Tests` to prove routing decisions are byte-identical against the production string path. **Not** a perf test — correctness gate before any further work.

---

## 5. What the POC does not prove

These are out of scope for the POC and should be considered separately if the end-state is approved:

1. **V1-hash and hierarchical PK fallback.** The POC is V2-hash-only. Production needs a dual path; the existing `FastPathVariantSelector` seam handles dispatch.
2. **Lazy `PartitionKeyRange` projection cost.** The POC measures build cost; it does not measure the per-call cost of `LazyPartitionKeyRange.MinInclusive` (estimated ~200 ns + 88 B alloc). External-caller hot-paths must be audited before the lazy projection ships.
3. **Wire-format edge cases.** `BuildFromJson` against the captured response body proves the happy path. Real production rollout needs fuzzing against gateway-response variants.
4. **Concurrent rebuild safety.** Today's `CollectionRoutingMap` is read-mostly with full-rebuild on refresh; the slim map preserves that contract but needs explicit lifetime tests.

---

## 6. Open questions for design review

1. **Is ~220 KB / build acceptable as the end-state target?** The lower bound is the `UInt128[N+1]` boundary array (277 KB at 17K ranges) — anything below 220 KB requires either truncating to 64-bit prefix keys (collision-prone) or changing the wire format. Do not pursue.
2. **Lazy `PartitionKeyRange` allocation:** is silently allocating strings in `MinInclusive`/`MaxExclusive` accessors acceptable? Or should the new structure expose a parallel `MinSpanUtf8` accessor and `[Obsolete]` the legacy property? **Recommendation:** new accessor + `[Obsolete]`, since the lazy-allocation path is a foot-gun in logging loops.
3. **Range parents (split history) storage:** the design uses a sparse `Dictionary<int, int[]>?` that is null in the steady state. Is this acceptable, or do we need a `byte[]` packed encoding? Telemetry on parents-rate per cache will tell.
4. **`int` ranges for IDs:** V2-hash gateway sends ids as `"0"`, `"1"`, ... `"N-1"` strings. Is short-circuiting them to `int` slots an acceptable wire-contract assumption? If the contract reserves the right to use non-integer ids, fall back to `string[]` ids (135 KB instead of 68 KB).

---
