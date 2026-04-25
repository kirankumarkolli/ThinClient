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
    /// V2-hash-only strategy: top-16-bit (65536-bucket) radix dispatch over the packed
    /// <see cref="UInt128"/> boundaries, then narrow binary search inside the bucket.
    /// </summary>
    /// <remarks>
    /// Memory footprint: <c>UInt128[N] + int[65537] + PartitionKeyRange[N]</c>.
    /// The 65537-int sentinel-tail bucket array is ~256 KB regardless of N — payable
    /// for very high range counts where the smaller bucket reduces the binary search
    /// span by 2x compared to <see cref="Radix1Strategy"/>.
    /// </remarks>
    internal sealed class Radix2Strategy : IRoutingFastPathStrategy
    {
        private readonly UInt128[] sortedNumericBoundaries;
        private readonly int[] bucketStart64K;
        private readonly PartitionKeyRange[] orderedRanges;

        internal Radix2Strategy(IReadOnlyList<PartitionKeyRange> orderedRanges)
        {
            int n = orderedRanges.Count;
            this.sortedNumericBoundaries = new UInt128[n];
            this.orderedRanges = new PartitionKeyRange[n];
            string[] minBoundaries = new string[n];
            for (int i = 0; i < n; i++)
            {
                PartitionKeyRange range = orderedRanges[i];
                this.orderedRanges[i] = range;
                string min = range.MinInclusive;
                minBoundaries[i] = min;
                if (min.Length == 0)
                {
                    this.sortedNumericBoundaries[i] = UInt128.MinValue;
                }
                else
                {
                    HexCodec.TryParseHex32ToUInt128(min, out this.sortedNumericBoundaries[i]);
                }
            }

            this.bucketStart64K = RadixBucketStarts.Build(minBoundaries, 16);
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
                throw new ArgumentException("EPK is not 32-char hex; Radix2Strategy is V2-hash-only.", nameof(effectivePartitionKey));
            }

            int b = HexCodec.TopBitsFromHex(effectivePartitionKey, 4);
            return this.LookupBucket(b, numeric);
        }

        public PartitionKeyRange Resolve(in EffectivePartitionKey effectivePartitionKey)
        {
            ReadOnlySpan<byte> key = effectivePartitionKey.AsSpan();
            ulong hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key);
            ulong lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key.Slice(8));
            int bucket = (key[0] << 8) | key[1];
            return this.LookupBucket(bucket, UInt128.Create(lo, hi));
        }

        public PartitionKeyRange Resolve(UInt128 effectivePartitionKey)
        {
            int bucket = (int)((effectivePartitionKey.GetHigh() >> 48) & 0xFFFF);
            return this.LookupBucket(bucket, effectivePartitionKey);
        }

        private PartitionKeyRange LookupBucket(int bucket, UInt128 key)
        {
            int lo = this.bucketStart64K[bucket];
            int hi = this.bucketStart64K[bucket + 1];
            int index = BinarySearchByBytes.Subarray(this.sortedNumericBoundaries, lo, hi, key);
            if (index < 0)
            {
                index = ~index - 1;
                if (index < 0) index = 0;
            }

            return this.orderedRanges[index];
        }
    }
}
