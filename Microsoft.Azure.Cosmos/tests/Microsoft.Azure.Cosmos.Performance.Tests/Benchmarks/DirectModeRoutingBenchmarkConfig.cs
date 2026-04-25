//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Benchmarks
{
    using System.Linq;
    using BenchmarkDotNet.Columns;
    using BenchmarkDotNet.Configs;
    using BenchmarkDotNet.Diagnosers;

    /// <summary>
    /// Benchmark configuration for <see cref="DirectModeRoutingBenchmark"/> that augments the
    /// BDN defaults with the built-in latency-percentile columns (P50, P85, P90, P95, P100)
    /// and the memory diagnoser. Higher fractional percentiles (P99.9, P99.99) are not part
    /// of <see cref="StatisticColumn"/>; they will be added in a follow-up if needed.
    /// </summary>
    public sealed class DirectModeRoutingBenchmarkConfig : ManualConfig
    {
        public DirectModeRoutingBenchmarkConfig()
        {
            this.AddColumnProvider(DefaultConfig.Instance.GetColumnProviders().ToArray());
            this.AddLogger(DefaultConfig.Instance.GetLoggers().ToArray());
            this.AddExporter(DefaultConfig.Instance.GetExporters().ToArray());
            this.AddAnalyser(DefaultConfig.Instance.GetAnalysers().ToArray());
            this.AddValidator(DefaultConfig.Instance.GetValidators().ToArray());

            this.AddDiagnoser(MemoryDiagnoser.Default);

            this.AddColumn(StatisticColumn.P50);
            this.AddColumn(StatisticColumn.P85);
            this.AddColumn(StatisticColumn.P90);
            this.AddColumn(StatisticColumn.P95);
            this.AddColumn(StatisticColumn.P100);
        }
    }
}
