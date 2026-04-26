//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants.Strategies
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Azure.Cosmos.Routing.FastPathVariants.Internal;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;
    using UInt128 = Microsoft.Azure.Cosmos.UInt128;

    /// <summary>
    /// V2-hash-only strategy: <see cref="Array.BinarySearch{T}(T[], T)"/> over a packed
    /// <see cref="UInt128"/> boundary array. The default production fast-path variant.
    /// </summary>
    /// <remarks>
    /// Boundaries are parsed once from 32-char hex into UInt128 at construction time.
    /// The 16-byte numeric form yields a single <see cref="UInt128"/> compare per probe
    /// (vs. ordinal string compare on 32 chars), which keeps the inner loop small and
    /// branch-light.
    /// </remarks>
    internal sealed class UInt128Strategy : IRoutingFastPathStrategy
    {
        private readonly UInt128[] sortedNumericBoundaries;
        private readonly PartitionKeyRange[] orderedRanges;

        internal UInt128Strategy(IReadOnlyList<PartitionKeyRange> orderedRanges)
        {
            int n = orderedRanges.Count;
            this.sortedNumericBoundaries = new UInt128[n];
            this.orderedRanges = new PartitionKeyRange[n];
            for (int i = 0; i < n; i++)
            {
                PartitionKeyRange range = orderedRanges[i];
                this.orderedRanges[i] = range;
                string min = range.MinInclusive;
                if (min.Length == 0)
                {
                    this.sortedNumericBoundaries[i] = UInt128.MinValue;
                }
                else
                {
                    HexCodec.TryParseHex32ToUInt128(min, out this.sortedNumericBoundaries[i]);
                }
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

            if (!HexCodec.TryParseHex32ToUInt128(effectivePartitionKey, out UInt128 numeric))
            {
                throw new ArgumentException("EPK is not 32-char hex; UInt128Strategy is V2-hash-only.", nameof(effectivePartitionKey));
            }

            return this.Resolve(numeric);
        }

        public PartitionKeyRange Resolve(in EffectivePartitionKey effectivePartitionKey)
        {
            ReadOnlySpan<byte> key = effectivePartitionKey.AsSpan();
            ulong hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key);
            ulong lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key.Slice(8));
            return this.Resolve(UInt128.Create(lo, hi));
        }

        public PartitionKeyRange Resolve(UInt128 effectivePartitionKey)
        {
            int index = Array.BinarySearch(this.sortedNumericBoundaries, effectivePartitionKey);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return this.orderedRanges[index];
        }
    }
}
