//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants.Strategies
{
    using System;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Decorator strategy: tries the last successfully-resolved range first; on miss,
    /// delegates to an inner strategy for the full lookup. Useful for workloads with
    /// strong temporal locality (repeated queries against the same partition key range).
    /// </summary>
    /// <remarks>
    /// Memory footprint: just a single <c>int</c> in addition to whatever the inner
    /// strategy holds. The inner strategy is fully owned by this decorator — there is
    /// no shared state with the routing map.
    /// <para>
    /// The cached index is only consulted on UInt128-shaped lookups where range
    /// containment can be checked in two compares (min &lt;= key &lt; nextMin). For
    /// string and byte overloads the decorator forwards directly to the inner strategy
    /// without consulting the cache.
    /// </para>
    /// </remarks>
    internal sealed class CacheLastStrategy : IRoutingFastPathStrategy
    {
        private readonly IRoutingFastPathStrategy inner;
        private readonly UInt128[] sortedNumericBoundaries;
        private readonly PartitionKeyRange[] orderedRanges;
        private int lastResolvedIndex;

        internal CacheLastStrategy(
            IRoutingFastPathStrategy inner,
            UInt128[] sortedNumericBoundaries,
            PartitionKeyRange[] orderedRanges)
        {
            this.inner = inner;
            this.sortedNumericBoundaries = sortedNumericBoundaries;
            this.orderedRanges = orderedRanges;
            this.lastResolvedIndex = 0;
        }

        public PartitionKeyRange Resolve(string effectivePartitionKey)
        {
            if (effectivePartitionKey != null
                && effectivePartitionKey.Length == 32
                && Internal.HexCodec.TryParseHex32ToUInt128(effectivePartitionKey, out UInt128 epk))
            {
                return this.Resolve(epk);
            }

            return this.inner.Resolve(effectivePartitionKey);
        }

        public PartitionKeyRange Resolve(in EffectivePartitionKey effectivePartitionKey) =>
            this.inner.Resolve(in effectivePartitionKey);

        public PartitionKeyRange Resolve(UInt128 effectivePartitionKey)
        {
            int cached = this.lastResolvedIndex;
            UInt128[] mins = this.sortedNumericBoundaries;
            if ((uint)cached < (uint)mins.Length)
            {
                bool minOk = effectivePartitionKey >= mins[cached];
                bool maxOk = (cached + 1 == mins.Length) || effectivePartitionKey < mins[cached + 1];
                if (minOk && maxOk)
                {
                    return this.orderedRanges[cached];
                }
            }

            int index = Array.BinarySearch(mins, effectivePartitionKey);
            if (index < 0)
            {
                index = ~index - 1;
            }

            this.lastResolvedIndex = index;
            return this.orderedRanges[index];
        }
    }
}
