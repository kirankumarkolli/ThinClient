//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Benchmarks
{
    using System.Linq;
    using BenchmarkDotNet.Configs;
    using BenchmarkDotNet.Diagnosers;
    using BenchmarkDotNet.Engines;
    using BenchmarkDotNet.Jobs;

    /// <summary>
    /// Lightweight config for <see cref="DirectModeRoutingRawDsrConstructionBenchmark"/>.
    /// Routing-map construction at 17K ranges is ~100 ms per op — orders of magnitude
    /// slower than the lookup benchmarks. Memory allocations are deterministic, so a
    /// handful of iterations is sufficient; BDN's default 6 warmup + 15 measure pilot
    /// would burn ~20 s per profile × 9 profiles unnecessarily.
    /// </summary>
    public sealed class RoutingMapConstructionBenchmarkConfig : ManualConfig
    {
        public RoutingMapConstructionBenchmarkConfig()
        {
            this.AddColumnProvider(DefaultConfig.Instance.GetColumnProviders().ToArray());
            this.AddLogger(DefaultConfig.Instance.GetLoggers().ToArray());
            this.AddExporter(DefaultConfig.Instance.GetExporters().ToArray());
            this.AddAnalyser(DefaultConfig.Instance.GetAnalysers().ToArray());
            this.AddValidator(DefaultConfig.Instance.GetValidators().ToArray());

            this.AddDiagnoser(MemoryDiagnoser.Default);

            // 1 invocation per iteration (each build is already long), 2 warmup + 5 measure.
            // The Allocated column comes from MemoryDiagnoser tracking GC.GetAllocatedBytesForCurrentThread()
            // around the invocation — it is byte-exact and does not benefit from more samples.
            this.AddJob(Job.Default
                .WithStrategy(RunStrategy.Monitoring)
                .WithUnrollFactor(1)
                .WithInvocationCount(1)
                .WithWarmupCount(2)
                .WithIterationCount(5));
        }
    }
}
