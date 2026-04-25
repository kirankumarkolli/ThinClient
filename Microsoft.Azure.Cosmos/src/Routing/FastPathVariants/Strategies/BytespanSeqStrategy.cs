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

    /// <summary>
    /// V2-hash-only strategy: binary search over a packed <c>byte[16N]</c> boundary array,
    /// using <see cref="ReadOnlySpan{T}.SequenceCompareTo(ReadOnlySpan{T})"/> for each probe.
    /// Variant D in the comparison sweep.
    /// </summary>
    internal sealed class BytespanSeqStrategy : IRoutingFastPathStrategy
    {
        private readonly byte[] sortedByteBoundaries;
        private readonly PartitionKeyRange[] orderedRanges;

        internal BytespanSeqStrategy(IReadOnlyList<PartitionKeyRange> orderedRanges)
        {
            int n = orderedRanges.Count;
            this.sortedByteBoundaries = new byte[n * 16];
            this.orderedRanges = new PartitionKeyRange[n];
            for (int i = 0; i < n; i++)
            {
                PartitionKeyRange range = orderedRanges[i];
                this.orderedRanges[i] = range;
                string min = range.MinInclusive;
                if (min.Length != 0)
                {
                    HexCodec.WriteHex32ToBytes(min, this.sortedByteBoundaries, i * 16);
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

            Span<byte> epkBytes = stackalloc byte[16];
            if (!HexCodec.TryParseHex32ToBytes(effectivePartitionKey, epkBytes))
            {
                throw new ArgumentException("EPK is not 32-char hex; BytespanSeqStrategy is V2-hash-only.", nameof(effectivePartitionKey));
            }

            return this.LookupBytes(epkBytes);
        }

        public PartitionKeyRange Resolve(in EffectivePartitionKey effectivePartitionKey) =>
            this.LookupBytes(effectivePartitionKey.AsSpan());

        public PartitionKeyRange Resolve(UInt128 effectivePartitionKey)
        {
            Span<byte> bytes = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, effectivePartitionKey.GetHigh());
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8), effectivePartitionKey.GetLow());
            return this.LookupBytes(bytes);
        }

        private PartitionKeyRange LookupBytes(ReadOnlySpan<byte> key)
        {
            int index = BinarySearchByBytes.Sequence(this.sortedByteBoundaries, key);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return this.orderedRanges[index];
        }
    }
}
