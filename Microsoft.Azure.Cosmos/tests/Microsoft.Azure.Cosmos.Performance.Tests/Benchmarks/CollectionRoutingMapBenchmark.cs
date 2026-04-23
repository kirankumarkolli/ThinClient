//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using BenchmarkDotNet.Attributes;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;

    /// <summary>
    /// Benchmarks for <see cref="CollectionRoutingMap"/> point lookup operations.
    /// Compares three approaches:
    /// 1. String-only (baseline): ordinal string binary search on 32-char hex EPKs
    /// 2. Numeric with parse: UInt128 binary search, parsing hex string per call
    /// 3. Numeric pre-parsed: UInt128 binary search, EPK already parsed (no per-call parse cost)
    /// </summary>
    [MemoryDiagnoser]
    public class CollectionRoutingMapBenchmark
    {
        /// <summary>
        /// Routing map with 32-char hex boundaries — numeric fast path is active.
        /// </summary>
        private CollectionRoutingMap numericRoutingMap;

        /// <summary>
        /// Routing map with 10-char hex boundaries — forces string-only fallback.
        /// </summary>
        private CollectionRoutingMap stringOnlyRoutingMap;

        private string numericLookupEpk;
        private string stringLookupEpk;
        private UInt128 preParsedLookupEpk;
        private Range<string> singleRange;
        private Range<string>[] multipleRanges;

        [Params(100, 1000, 10000, 50000)]
        public int PartitionCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            // Build numeric routing map (32-char hex → UInt128 fast path activates)
            this.numericRoutingMap = CollectionRoutingMapBenchmark.BuildRoutingMap(this.PartitionCount, hex32: true);
            this.numericLookupEpk = (this.PartitionCount / 2).ToString("X32");
            CollectionRoutingMap.TryParseHex32ToUInt128(this.numericLookupEpk, out this.preParsedLookupEpk);

            // Build string-only routing map (10-char hex → fast path does NOT activate)
            this.stringOnlyRoutingMap = CollectionRoutingMapBenchmark.BuildRoutingMap(this.PartitionCount, hex32: false);
            this.stringLookupEpk = (this.PartitionCount / 2).ToString("X10");

            // Ranges for overlap benchmarks (using numeric map format)
            int midStart = this.PartitionCount / 4;
            int midEnd = this.PartitionCount / 2;
            this.singleRange = new Range<string>(
                midStart.ToString("X32"),
                midEnd.ToString("X32"),
                isMinInclusive: true,
                isMaxInclusive: false);

            this.multipleRanges = new Range<string>[]
            {
                new Range<string>(
                    (this.PartitionCount / 8).ToString("X32"),
                    (this.PartitionCount / 4).ToString("X32"),
                    isMinInclusive: true,
                    isMaxInclusive: false),
                new Range<string>(
                    (this.PartitionCount / 2).ToString("X32"),
                    (3 * this.PartitionCount / 4).ToString("X32"),
                    isMinInclusive: true,
                    isMaxInclusive: false),
            };
        }

        [Benchmark(Baseline = true)]
        public string PointLookup_StringOnly()
        {
            return this.stringOnlyRoutingMap.GetRangeByEffectivePartitionKey(this.stringLookupEpk).Id;
        }

        [Benchmark]
        public string PointLookup_NumericWithParse()
        {
            return this.numericRoutingMap.GetRangeByEffectivePartitionKey(this.numericLookupEpk).Id;
        }

        [Benchmark]
        public string PointLookup_NumericPreParsed()
        {
            return this.numericRoutingMap.GetRangeByEffectivePartitionKey(this.preParsedLookupEpk).Id;
        }

        [Benchmark]
        public int GetOverlappingRanges_SingleRange()
        {
            return this.numericRoutingMap.GetOverlappingRanges(this.singleRange).Count;
        }

        [Benchmark]
        public int GetOverlappingRanges_MultipleRanges()
        {
            return this.numericRoutingMap.GetOverlappingRanges(this.multipleRanges).Count;
        }

        private static CollectionRoutingMap BuildRoutingMap(int partitionCount, bool hex32)
        {
            string format = hex32 ? "X32" : "X10";
            List<Tuple<PartitionKeyRange, ServiceIdentity>> ranges = new(partitionCount);

            for (int i = 0; i < partitionCount; i++)
            {
                string min = i == 0 ? string.Empty : i.ToString(format);
                string max = i == partitionCount - 1 ? "FF" : (i + 1).ToString(format);

                ranges.Add(Tuple.Create(
                    new PartitionKeyRange
                    {
                        Id = i.ToString(),
                        MinInclusive = min,
                        MaxExclusive = max
                    },
                    (ServiceIdentity)null));
            }

            return CollectionRoutingMap.TryCreateCompleteRoutingMap(
                ranges,
                collectionUniqueId: "bench-collection",
                useLengthAwareRangeComparer: false);
        }
    }
}
