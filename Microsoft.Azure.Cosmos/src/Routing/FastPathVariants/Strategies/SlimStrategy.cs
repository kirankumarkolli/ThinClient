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
    /// Slim numeric V2-hash strategy: uses an N+1-element <see cref="UInt128"/> boundary
    /// array (<see cref="Array.BinarySearch{T}(T[], T, int, int)"/>) as the canonical
    /// boundary store, mirroring the end-state design in
    /// <c>docs/pkrange-memory-10x-plan.md</c>.
    /// </summary>
    /// <remarks>
    /// Functionally equivalent to <see cref="UInt128Strategy"/> on the lookup hot path —
    /// both reduce to a single <see cref="UInt128"/> binary search per probe. The visible
    /// difference vs. <see cref="UInt128Strategy"/> is the boundary-array size (N+1 vs N)
    /// and the use of the upper-sentinel slot to terminate the binary search at the
    /// max-exclusive boundary, which is the layout assumed by the slim end-state.
    ///
    /// <para>Construction-time memory wins (<c>UInt128[N+1]</c> + <c>int[N]</c> + <c>byte[N]</c>
    /// vs the layered string cache) are NOT realized in this strategy because it still
    /// retains the input <see cref="PartitionKeyRange"/> array to satisfy the existing
    /// <see cref="IRoutingFastPathStrategy.Resolve(UInt128)"/> contract. Those wins live
    /// in <c>SlimRoutingMap</c> + <c>SlimRoutingMapBuilder</c> under the test project, and
    /// are measured by the companion <c>BuildSlimRoutingMap[FromJson]</c> benchmarks.</para>
    /// </remarks>
    internal sealed class SlimStrategy : IRoutingFastPathStrategy
    {
        private readonly UInt128[] sortedBoundaries; // N+1 entries
        private readonly PartitionKeyRange[] orderedRanges;

        internal SlimStrategy(IReadOnlyList<PartitionKeyRange> orderedRanges)
        {
            int n = orderedRanges.Count;
            this.sortedBoundaries = new UInt128[n + 1];
            this.orderedRanges = new PartitionKeyRange[n];

            for (int i = 0; i < n; i++)
            {
                PartitionKeyRange range = orderedRanges[i];
                this.orderedRanges[i] = range;

                string min = range.MinInclusive;
                if (min.Length == 0)
                {
                    this.sortedBoundaries[i] = UInt128.MinValue;
                }
                else
                {
                    HexCodec.TryParseHex32ToUInt128(min, out this.sortedBoundaries[i]);
                }
            }

            // Upper sentinel: max-exclusive of last range, or UInt128.MaxValue if "FF".
            string lastMax = orderedRanges[n - 1].MaxExclusive;
            if (string.Equals(lastMax, "FF", StringComparison.Ordinal) || lastMax.Length == 0)
            {
                this.sortedBoundaries[n] = UInt128.MaxValue;
            }
            else
            {
                HexCodec.TryParseHex32ToUInt128(lastMax, out this.sortedBoundaries[n]);
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
                throw new ArgumentException("EPK is not 32-char hex; SlimStrategy is V2-hash-only.", nameof(effectivePartitionKey));
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
            // Binary search over the [0..N) boundary slots; the upper sentinel at [N]
            // is part of the array so the search bounds never read past valid data
            // even on a key equal to the global maximum.
            int index = Array.BinarySearch(
                this.sortedBoundaries,
                0,
                this.sortedBoundaries.Length - 1,
                effectivePartitionKey);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return this.orderedRanges[index];
        }
    }
}
