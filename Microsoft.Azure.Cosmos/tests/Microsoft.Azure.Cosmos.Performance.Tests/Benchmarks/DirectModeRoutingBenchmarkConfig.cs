//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Benchmarks
{
    using System.Linq;
    using BenchmarkDotNet.Columns;
    using BenchmarkDotNet.Configs;
    using BenchmarkDotNet.Diagnosers;
    using BenchmarkDotNet.Exporters.Csv;
    using BenchmarkDotNet.Jobs;
    using Microsoft.Azure.Cosmos.Performance.Tests.Data;

    /// <summary>
    /// Benchmark configuration for <see cref="DirectModeRoutingBenchmark"/> that augments the
    /// BDN defaults with the built-in latency-percentile columns (P90, P95, P100) and the
    /// memory diagnoser. Higher fractional percentiles (P99.9, P99.99) are not part of
    /// <see cref="StatisticColumn"/>; they will be added in a follow-up if needed.
    /// </summary>
    public sealed class DirectModeRoutingBenchmarkConfig : ManualConfig
    {
        // Pin invocations-per-iteration to at-least 1.5 × the PKRange count so every iteration
        // exercises a representative slice of the routing map (and not just a pilot-tuned subset).
        // UnrollFactor=1 is required because the benchmark is async; with that, InvocationCount
        // can be any positive value.
        private const int InvocationsPerIteration = (PkRangeRoutingFactory.ExpectedRowCount * 3 / 2) + 1;

        public DirectModeRoutingBenchmarkConfig()
        {
            this.AddColumnProvider(DefaultConfig.Instance.GetColumnProviders().ToArray());
            this.AddLogger(DefaultConfig.Instance.GetLoggers().ToArray());
            this.AddExporter(DefaultConfig.Instance.GetExporters().ToArray());
            this.AddAnalyser(DefaultConfig.Instance.GetAnalysers().ToArray());
            this.AddValidator(DefaultConfig.Instance.GetValidators().ToArray());

            this.AddDiagnoser(MemoryDiagnoser.Default);

            this.AddJob(Job.Default
                .WithUnrollFactor(1)
                .WithInvocationCount(InvocationsPerIteration));

            this.AddColumn(StatisticColumn.P0);
            this.AddColumn(StatisticColumn.P25);
            this.AddColumn(StatisticColumn.P50);
            this.AddColumn(StatisticColumn.P67);
            this.AddColumn(StatisticColumn.P80);
            this.AddColumn(StatisticColumn.P85);
            this.AddColumn(StatisticColumn.P90);
            this.AddColumn(StatisticColumn.P95);
            this.AddColumn(StatisticColumn.P100);

            // Raw per-iteration measurements so we can compute fine-grained percentiles
            // (P99, P99.9 etc.) offline that BDN's StatisticColumn doesn't expose.
            this.AddExporter(CsvMeasurementsExporter.Default);
        }
    }
}
