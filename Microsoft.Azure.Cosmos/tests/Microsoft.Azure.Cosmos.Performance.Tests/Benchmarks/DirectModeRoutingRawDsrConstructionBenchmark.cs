//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using BenchmarkDotNet.Attributes;
    using Microsoft.Azure.Cosmos.Performance.Tests.Data;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Companion to <see cref="DirectModeRoutingRawDsrBenchmark"/> that measures the
    /// <em>cache-construction</em> cost of <see cref="CollectionRoutingMap"/> at the same
    /// production-shape ~17K-range scale, across the same routing-strategy profiles. The
    /// hot-path lookup benchmark is alloc-free for every variant; the boundary-storage
    /// savings of the numeric variants only materialize at construction time and as
    /// retained working-set on the cached map.
    ///
    /// <para>With <c>[MemoryDiagnoser]</c>, BDN reports per-op <c>Allocated</c> bytes,
    /// which is the headline number for "string vs UInt128" memory savings — directly
    /// comparable across profiles.</para>
    ///
    /// <para>Profile axis matches <see cref="DirectModeRoutingRawDsrBenchmark"/> verbatim
    /// so the same sweep script can drive both classes.</para>
    /// </summary>
    [MemoryDiagnoser]
    [Config(typeof(DirectModeRoutingBenchmarkConfig))]
    public class DirectModeRoutingRawDsrConstructionBenchmark
    {
        private const string TsvPath = "Data/shared_conversations_pkranges.tsv";

        private Tuple<PartitionKeyRange, ServiceIdentity>[] tuples;

        [ParamsSource(nameof(Profiles))]
        public string Profile { get; set; }

        public static IEnumerable<string> Profiles => RoutingBenchmarkProfiles.AllProfiles;

        [GlobalSetup]
        public void GlobalSetup()
        {
            RoutingBenchmarkProfiles.Apply(this.Profile);

            string exeDir = System.IO.Path.GetDirectoryName(
                typeof(DirectModeRoutingRawDsrConstructionBenchmark).Assembly.Location);
            if (!string.IsNullOrEmpty(exeDir))
            {
                System.IO.Directory.SetCurrentDirectory(exeDir);
            }

            IReadOnlyList<PartitionKeyRange> ranges = PkRangeRoutingFactory.LoadFromTsv(TsvPath);
            this.tuples = ranges
                .Select(r => Tuple.Create(r, (ServiceIdentity)null))
                .ToArray();
        }

        /// <summary>
        /// Builds a fresh <see cref="CollectionRoutingMap"/> per op from the same input
        /// tuples (allocations attributable to the input array are amortized in setup).
        /// MemoryDiagnoser captures alloc-bytes/op — directly comparable across profiles.
        /// </summary>
        [Benchmark]
        public object BuildRoutingMap()
        {
            return CollectionRoutingMap.TryCreateCompleteRoutingMap(
                this.tuples,
                string.Empty,
                useLengthAwareRangeComparer: false);
        }
    }
}
