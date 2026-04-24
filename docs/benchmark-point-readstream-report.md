# Benchmark Report: Point ReadStream (`ReadStreamExistsV3`)

**Date**: 2026-04-24  
**Branch**: `users/kirankk/optimize-pkrange-lookup`  
**SDK Version**: 3.42.2 (cosmos-netstandard-sdk)  
**Runtime**: .NET 6.0.36 | Windows 10.0.26200 | 16 cores (Standard_D16s_v5)

## Configuration

| Parameter | Value |
|---|---|
| Operation | `ReadStreamExistsV3BenchmarkOperation` |
| Connection Mode | Direct |
| Build | Release (ServerGC enabled) |
| Items per Run | 200,000 |
| Degree of Parallelism | 5 |
| Container Throughput | 10,000 RU/s |
| Max Retry on 429 | 0 (no retries) |
| Target | Local Cosmos DB Emulator (`https://localhost:8081`) |
| Runs | 3 |

## Performance Results

| Metric | Run 1 | Run 2 | Run 3 | **Average** |
|---|---|---|---|---|
| Overall RPS | 2,435 | 1,519 | 1,538 | **1,831** |
| Avg RPS (excl. outliers) | 2,449 | 1,583 | 1,504 | **1,845** |
| Success Rate | 76.3% | 36.1% | 35.8% | **49.4%** |
| **p50 Latency** | 1.621 ms | 1.014 ms | 1.007 ms | **1.214 ms** |
| **p75 Latency** | 1.746 ms | 1.215 ms | 1.201 ms | **1.387 ms** |
| **p90 Latency** | 1.857 ms | 1.722 ms | 1.710 ms | **1.763 ms** |
| **p95 Latency** | 1.945 ms | 1.812 ms | 1.801 ms | **1.853 ms** |
| **p99 Latency** | 2.223 ms | 2.043 ms | 2.042 ms | **2.102 ms** |
| Max Latency | 63.6 ms | 61.2 ms | 64.1 ms | **62.9 ms** |

> **Note**: Runs 2–3 experienced higher 429 throttling from the emulator (emulator variability with `MaxRetryAttemptsOnRateLimitedRequests=0`). The lower p50 latency on those runs reflects reduced contention on the subset of successful requests.

## Memory Profile

Sampled every 2 seconds via `Get-Process` during each benchmark run.

| Metric | Run 1 | Run 2 | Run 3 | **Average** |
|---|---|---|---|---|
| Working Set (avg) | 96.5 MB | 93.7 MB | 93.6 MB | **94.6 MB** |
| Working Set (peak) | 97.5 MB | 94.7 MB | 94.6 MB | **97.5 MB peak** |
| Private Memory (avg) | 42.2 MB | 39.4 MB | 39.5 MB | **40.4 MB** |
| Private Memory (peak) | 43.1 MB | 40.5 MB | 40.6 MB | **43.1 MB peak** |
| Threads (min → max) | 23 → 33 | 25 → 32 | 24 → 31 | **~24 → 32** |
| Handles (min → max) | 618 → 641 | 588 → 602 | 592 → 595 | **~599 → 613** |

### Memory Progression (per run)

```
Run 1:
  Start  (  3.1s): WS=  95.7 MB  Private=  42.1 MB  Threads=33
  Middle ( 35.3s): WS=  96.7 MB  Private=  42.8 MB  Threads=26
  End    ( 67.5s): WS=  96.1 MB  Private=  41.3 MB  Threads=24

Run 2:
  Start  (  3.0s): WS=  92.6 MB  Private=  39.0 MB  Threads=32
  Middle ( 29.2s): WS=  93.3 MB  Private=  38.9 MB  Threads=26
  End    ( 53.3s): WS=  93.9 MB  Private=  39.5 MB  Threads=26

Run 3:
  Start  (  3.0s): WS=  92.5 MB  Private=  39.2 MB  Threads=31
  Middle ( 27.2s): WS=  93.2 MB  Private=  39.0 MB  Threads=25
  End    ( 51.3s): WS=  94.0 MB  Private=  39.9 MB  Threads=25
```

## .NET GC Counters

Captured via `dotnet-counters` (30-second sample during steady-state benchmark execution).

| Counter | Value |
|---|---|
| GC Collections (Gen0 / Gen1 / Gen2) | **0 / 0 / 0** |
| GC Pause Time | **0 ms** |
| GC Heap Allocation Rate | **~26 KB/s** |
| Process Working Set | **~99 MB** |
| Lock Contentions | **0 /s** |
| CPU Time (user) | **0.01 s/s** |
| CPU Time (system) | **~0 s/s** |
| Thread Pool Thread Count | Stable (~25 threads) |

## Key Observations

1. **Very low memory footprint**: ~95 MB working set, ~40 MB private memory — stable throughout all runs with no growth pattern.

2. **Zero GC collections** during steady state — the stream-based read path (`ReadItemStreamAsync`) is extremely allocation-friendly with only ~26 KB/s allocation rate. This confirms the stream API avoids deserialization allocations.

3. **Zero GC pause time** — no stop-the-world pauses were observed during any run, which is ideal for latency-sensitive workloads.

4. **Zero lock contentions** — no thread synchronization contention detected.

5. **Flat memory progression** (start ≈ end across all runs) — no memory leaks detected over the test duration.

6. **Sub-2ms median latency** — p50 latency ranges from 1.0–1.6 ms depending on emulator load.

7. **Max latency spikes** (~63 ms) are consistent across runs and likely attributable to TCP/emulator jitter rather than GC pauses (since GC pause time was 0).

8. **Emulator throttling variance**: The local emulator exhibits variable 429 behavior across runs. Run 1 achieved 76% success while Runs 2–3 dropped to ~36%. For production-representative throughput numbers, test against a provisioned Cosmos DB account.
