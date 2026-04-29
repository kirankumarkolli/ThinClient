//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Slim
{
    using System;
    using System.Runtime.CompilerServices;
    using UInt128 = Microsoft.Azure.Cosmos.UInt128;

    /// <summary>
    /// Numeric, parallel-array routing map used by the spike to prove the end-state
    /// memory footprint claimed in <c>docs/pkrange-memory-10x-plan.md</c>.
    ///
    /// <para>Storage layout for N ranges:
    ///   <c>UInt128[N+1] sortedBoundaries</c> — 16 B × (N+1)
    ///   <c>int[N] rangeIds</c>               —  4 B × N
    ///   <c>byte[N] statusBits</c>            —  1 B × N
    /// </para>
    ///
    /// <para>No <see cref="Documents.PartitionKeyRange"/> objects, no <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>,
    /// no <c>Range&lt;string&gt;[]</c> shadow array. Lookup is a single <see cref="Array.BinarySearch{T}(T[], T)"/>
    /// over the boundary array.</para>
    /// </summary>
    internal sealed class SlimRoutingMap
    {
        private readonly UInt128[] sortedBoundaries;
        private readonly int[] rangeIds;
        private readonly byte[] statusBits;

        internal SlimRoutingMap(UInt128[] sortedBoundaries, int[] rangeIds, byte[] statusBits)
        {
            this.sortedBoundaries = sortedBoundaries;
            this.rangeIds = rangeIds;
            this.statusBits = statusBits;
        }

        public int Count => this.rangeIds.Length;

        /// <summary>
        /// Hot-path lookup. Returns the slot index of the range whose
        /// [Min, Max) contains <paramref name="effectivePartitionKey"/>.
        /// Zero allocations.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ResolveSlot(UInt128 effectivePartitionKey)
        {
            int idx = Array.BinarySearch(
                this.sortedBoundaries,
                0,
                this.sortedBoundaries.Length - 1,
                effectivePartitionKey);
            return idx >= 0 ? idx : ~idx - 1;
        }

        public int GetIdAt(int slot) => this.rangeIds[slot];

        public byte GetStatusAt(int slot) => this.statusBits[slot];
    }
}
