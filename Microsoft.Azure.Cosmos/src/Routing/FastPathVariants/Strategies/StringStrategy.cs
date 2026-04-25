//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants.Strategies
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;

    /// <summary>
    /// Production baseline strategy: ordinal <see cref="Array.BinarySearch{T}(T[], T, IComparer{T})"/>
    /// over the sorted <see cref="PartitionKeyRange.MinInclusive"/> boundary strings.
    /// Always valid (no V2-hash precondition) — this is the only strategy that supports
    /// V1 hash, hierarchical partition keys, and any other variable-length EPK shape.
    /// Selected by <see cref="FastPathStrategyFactory"/> when the active variant is
    /// <see cref="FastPathVariant.String"/> or when the routing map's boundaries are not
    /// V2 128-bit hash (in which case no other strategy is constructible).
    /// </summary>
    internal sealed class StringStrategy : IRoutingFastPathStrategy
    {
        private readonly string[] sortedMinBoundaries;
        private readonly PartitionKeyRange[] orderedRanges;

        internal StringStrategy(IReadOnlyList<PartitionKeyRange> orderedRanges)
        {
            int n = orderedRanges.Count;
            this.sortedMinBoundaries = new string[n];
            this.orderedRanges = new PartitionKeyRange[n];
            for (int i = 0; i < n; i++)
            {
                this.sortedMinBoundaries[i] = orderedRanges[i].MinInclusive;
                this.orderedRanges[i] = orderedRanges[i];
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
                return this.orderedRanges[0];
            }

            int idx = Array.BinarySearch(this.sortedMinBoundaries, effectivePartitionKey, StringComparer.Ordinal);
            if (idx < 0)
            {
                idx = ~idx - 1;
                Debug.Assert(idx >= 0);
            }

            return this.orderedRanges[idx];
        }

        public PartitionKeyRange Resolve(in EffectivePartitionKey effectivePartitionKey) =>
            throw new NotSupportedException("StringStrategy does not support byte-EPK lookup; numeric fast path required.");

        public PartitionKeyRange Resolve(UInt128 effectivePartitionKey) =>
            throw new NotSupportedException("StringStrategy does not support UInt128-EPK lookup; numeric fast path required.");
    }
}
