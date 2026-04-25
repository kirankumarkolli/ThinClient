//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants.Strategies
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Runtime.CompilerServices;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;

    /// <summary>
    /// Variant H. Branchless ordinal-string binary search over a sorted boundary array
    /// plus payload SoA (parallel <see cref="PartitionKeyRange"/>[] indexed by boundary
    /// position). Targets V1 hash / hierarchical PK / variable-length-EPK collections
    /// that cannot use the V2 byte fast path (variant G / <see cref="SoaStrategy"/>).
    /// Independent of <c>hasNumericFastPath</c>; works on any topology.
    /// </summary>
    /// <remarks>
    /// Layer 1 (payload SoA) eliminates the <see cref="System.Collections.Generic.List{T}"/>
    /// indirection vs <see cref="StringStrategy"/>; layer 2
    /// (<see cref="BinarySearchStringsBranchless"/>) replaces the per-probe
    /// <see cref="IComparer{T}"/> dispatch in <see cref="Array.BinarySearch{T}(T[], T, IComparer{T})"/>
    /// with a direct <see cref="string.CompareOrdinal(string, string)"/> + arithmetic-mask
    /// lo/hi update so the JIT can lower it to cmov-style code; layer 3
    /// (<see cref="GetOverlappingRangesByStrings"/>) is the string analog of the byte
    /// SoA range query for non-V2 topologies.
    /// </remarks>
    internal sealed class StringSoaStrategy : IRoutingFastPathStrategy
    {
        private readonly string[] sortedMinBoundaries;
        private readonly PartitionKeyRange[] sortedRangePayloads;

        internal StringSoaStrategy(IReadOnlyList<PartitionKeyRange> orderedRanges)
        {
            int n = orderedRanges.Count;
            this.sortedMinBoundaries = new string[n];
            this.sortedRangePayloads = new PartitionKeyRange[n];
            for (int i = 0; i < n; i++)
            {
                PartitionKeyRange range = orderedRanges[i];
                this.sortedMinBoundaries[i] = range.MinInclusive;
                this.sortedRangePayloads[i] = range;
            }
        }

        public PartitionKeyRange Resolve(string effectivePartitionKey)
        {
            if (string.CompareOrdinal(effectivePartitionKey, PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey) >= 0)
            {
                throw new ArgumentException(nameof(effectivePartitionKey));
            }

            if (string.CompareOrdinal(PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey, effectivePartitionKey) == 0)
            {
                return this.sortedRangePayloads[0];
            }

            int index = BinarySearchStringsBranchless(this.sortedMinBoundaries, effectivePartitionKey);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return this.sortedRangePayloads[index];
        }

        public PartitionKeyRange Resolve(in EffectivePartitionKey effectivePartitionKey) =>
            throw new NotSupportedException("StringSoaStrategy does not support byte-EPK lookup; use the string overload.");

        public PartitionKeyRange Resolve(UInt128 effectivePartitionKey) =>
            throw new NotSupportedException("StringSoaStrategy does not support UInt128-EPK lookup; use the string overload.");

        /// <summary>
        /// Layer 3 — SoA range query keyed by string Min boundaries. String analog of
        /// <see cref="SoaStrategy.GetOverlappingRangesByBytes(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte})"/>;
        /// works on any topology.
        /// </summary>
        public IReadOnlyList<PartitionKeyRange> GetOverlappingRangesByStrings(
            string minInclusive,
            string maxExclusive)
        {
            if (minInclusive == null) throw new ArgumentNullException(nameof(minInclusive));
            if (maxExclusive == null) throw new ArgumentNullException(nameof(maxExclusive));
            if (string.CompareOrdinal(minInclusive, maxExclusive) >= 0)
            {
                throw new ArgumentException("minInclusive must be strictly less than maxExclusive.");
            }

            int n = this.sortedMinBoundaries.Length;

            int minIdx = ~BinarySearchStringsBranchless(this.sortedMinBoundaries, minInclusive) - 1;
            if (minIdx < 0)
            {
                minIdx = 0;
            }

            int maxIdx = ~BinarySearchStringsBranchless(this.sortedMinBoundaries, maxExclusive) - 1;
            if (maxIdx < 0)
            {
                maxIdx = 0;
            }
            else if (maxIdx >= n)
            {
                maxIdx = n - 1;
            }
            else if (maxIdx > minIdx
                && string.CompareOrdinal(this.sortedMinBoundaries[maxIdx], maxExclusive) == 0)
            {
                // The range whose Min equals maxExclusive starts at the request's exclusive
                // upper bound and therefore does not overlap.
                maxIdx--;
            }

            int count = maxIdx - minIdx + 1;
            if (count <= 0)
            {
                return Array.Empty<PartitionKeyRange>();
            }

            PartitionKeyRange[] result = new PartitionKeyRange[count];
            Array.Copy(this.sortedRangePayloads, minIdx, result, 0, count);
            return new ReadOnlyCollection<PartitionKeyRange>(result);
        }

        /// <summary>
        /// Branchless binary search over the string Min boundaries using
        /// <see cref="string.CompareOrdinal(string, string)"/>. Upper-bound semantics:
        /// returns <c>~lo</c> where <c>lo - 1</c> is the largest index with
        /// <c>boundaries[i] &lt;= key</c>. Probe-direction update is arithmetic-mask-driven
        /// so the JIT lowers it to cmov-style code.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BinarySearchStringsBranchless(string[] boundaries, string key)
        {
            int lo = 0;
            int hi = boundaries.Length - 1;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);

                bool midLeq = string.CompareOrdinal(boundaries[mid], key) <= 0;
                int midLeqInt = Unsafe.As<bool, byte>(ref midLeq);

                int mask = -midLeqInt;
                int newLo = (mid + 1) & mask;
                int newHi = (mid - 1) & ~mask;
                int keepLo = lo & ~mask;
                int keepHi = hi & mask;
                lo = newLo | keepLo;
                hi = newHi | keepHi;
            }

            return ~lo;
        }
    }
}
