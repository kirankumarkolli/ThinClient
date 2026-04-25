//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants.Strategies
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using Microsoft.Azure.Cosmos.Routing.FastPathVariants.Internal;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;

    /// <summary>
    /// V2-hash-only strategy: branchless binary search over packed byte boundaries +
    /// payload "Structure of Arrays" (parallel <see cref="PartitionKeyRange"/>[]) for direct
    /// index-into-array lookup, plus a packed <c>MaxExclusive</c> array enabling a fast
    /// byte-wise <see cref="GetOverlappingRangesByBytes(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// range query.
    /// </summary>
    /// <remarks>
    /// Memory footprint: <c>byte[16N] + byte[16N] + PartitionKeyRange[N]</c>. The two
    /// 16N byte arrays are the sorted MinInclusive (search key) and the parallel
    /// MaxExclusive boundaries (used by the range-query helper to detect equality with
    /// the requested upper bound).
    /// </remarks>
    internal sealed class SoaStrategy : IRoutingFastPathStrategy
    {
        private readonly byte[] sortedByteBoundaries;
        private readonly byte[] sortedMaxBytes;
        private readonly PartitionKeyRange[] sortedRangePayloads;

        internal SoaStrategy(IReadOnlyList<PartitionKeyRange> orderedRanges)
        {
            int n = orderedRanges.Count;
            this.sortedByteBoundaries = new byte[n * 16];
            this.sortedMaxBytes = new byte[n * 16];
            this.sortedRangePayloads = new PartitionKeyRange[n];
            for (int i = 0; i < n; i++)
            {
                PartitionKeyRange range = orderedRanges[i];
                this.sortedRangePayloads[i] = range;

                string min = range.MinInclusive;
                if (min.Length != 0)
                {
                    HexCodec.WriteHex32ToBytes(min, this.sortedByteBoundaries, i * 16);
                }

                string max = range.MaxExclusive;
                if (max != null && max.Length == 32)
                {
                    HexCodec.WriteHex32ToBytes(max, this.sortedMaxBytes, i * 16);
                }
                else
                {
                    // The trailing "FF" sentinel (MaximumExclusive) is not 32-char hex; encode
                    // as all-0xFF so byte-wise compares preserve "positive infinity" ordering.
                    Span<byte> slot = this.sortedMaxBytes.AsSpan(i * 16, 16);
                    slot.Fill(0xFF);
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
                return this.sortedRangePayloads[0];
            }

            Span<byte> epkBytes = stackalloc byte[16];
            if (!HexCodec.TryParseHex32ToBytes(effectivePartitionKey, epkBytes))
            {
                throw new ArgumentException("EPK is not 32-char hex; SoaStrategy is V2-hash-only.", nameof(effectivePartitionKey));
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

        /// <summary>
        /// Range query: returns all partition key ranges overlapping <c>[minInclusive, maxExclusive)</c>.
        /// Both inputs must be exactly 16 bytes big-endian (V2 hash form). Two branchless
        /// byte binary searches plus an <see cref="Array.Copy(Array, int, Array, int, int)"/>
        /// over the parallel payload array.
        /// </summary>
        public IReadOnlyList<PartitionKeyRange> GetOverlappingRangesByBytes(
            ReadOnlySpan<byte> minInclusive,
            ReadOnlySpan<byte> maxExclusive)
        {
            if (minInclusive.Length != 16 || maxExclusive.Length != 16)
            {
                throw new ArgumentException("V2 hash EPK ranges must be exactly 16 bytes each.");
            }

            int n = this.sortedByteBoundaries.Length >> 4;

            int minIdx = ~BinarySearchByBytes.Branchless(this.sortedByteBoundaries, minInclusive) - 1;
            if (minIdx < 0)
            {
                minIdx = 0;
            }

            int maxIdx = ~BinarySearchByBytes.Branchless(this.sortedByteBoundaries, maxExclusive) - 1;
            if (maxIdx < 0)
            {
                maxIdx = 0;
            }
            else if (maxIdx >= n)
            {
                maxIdx = n - 1;
            }
            else if (maxIdx > minIdx)
            {
                ReadOnlySpan<byte> minAtMaxIdx = new ReadOnlySpan<byte>(this.sortedByteBoundaries, maxIdx << 4, 16);
                if (minAtMaxIdx.SequenceEqual(maxExclusive))
                {
                    maxIdx--;
                }
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

        private PartitionKeyRange LookupBytes(ReadOnlySpan<byte> key)
        {
            int index = BinarySearchByBytes.Branchless(this.sortedByteBoundaries, key);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return this.sortedRangePayloads[index];
        }
    }
}
