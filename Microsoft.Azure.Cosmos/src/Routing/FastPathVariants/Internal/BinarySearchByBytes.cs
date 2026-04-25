//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants.Internal
{
    using System;
    using System.Buffers.Binary;
    using System.Runtime.CompilerServices;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Family of binary searches over a flat byte[] of 16-byte big-endian boundaries,
    /// shared by the bytespan / radix / SoA fast-path strategies. All return the index
    /// of an exact match, or the bitwise-complement of the insertion point
    /// (<see cref="Array.BinarySearch{T}(T[], T)"/> semantics).
    /// </summary>
    internal static class BinarySearchByBytes
    {
        /// <summary>
        /// Sequence-compare based search (variant D): one <see cref="ReadOnlySpan{T}.SequenceCompareTo(ReadOnlySpan{T})"/>
        /// per probe.
        /// </summary>
        internal static int Sequence(byte[] boundaries, ReadOnlySpan<byte> key)
        {
            int lo = 0;
            int hi = (boundaries.Length / 16) - 1;
            ReadOnlySpan<byte> all = boundaries;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int cmp = all.Slice(mid * 16, 16).SequenceCompareTo(key);
                if (cmp == 0)
                {
                    return mid;
                }
                else if (cmp < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return ~lo;
        }

        /// <summary>
        /// Hand-rolled search (variant E) using two big-endian ulong reads per probe,
        /// short-circuiting on the high half.
        /// </summary>
        internal static int Hand(byte[] boundaries, ReadOnlySpan<byte> key)
        {
            ulong keyHi = BinaryPrimitives.ReadUInt64BigEndian(key);
            ulong keyLo = BinaryPrimitives.ReadUInt64BigEndian(key.Slice(8));

            int lo = 0;
            int hi = (boundaries.Length / 16) - 1;
            ReadOnlySpan<byte> all = boundaries;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                ReadOnlySpan<byte> midSpan = all.Slice(mid * 16, 16);
                ulong midHi = BinaryPrimitives.ReadUInt64BigEndian(midSpan);
                if (midHi != keyHi)
                {
                    if (midHi < keyHi)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }

                    continue;
                }

                ulong midLo = BinaryPrimitives.ReadUInt64BigEndian(midSpan.Slice(8));
                if (midLo == keyLo)
                {
                    return mid;
                }
                else if (midLo < keyLo)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return ~lo;
        }

        /// <summary>
        /// Branchless search (variant G) using upper-bound semantics and arithmetic-mask selection
        /// so the JIT can lower probe-direction updates to cmov-style code, eliminating the
        /// ~50%-mispredict-rate branch on each iteration.
        /// </summary>
        /// <remarks>
        /// Always returns <c>~lo</c>; callers' standard <c>if (i &lt; 0) i = ~i - 1;</c> handling
        /// produces <c>lo - 1</c>, the largest boundary index &lt;= key (exact-match-or-not).
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Branchless(byte[] boundaries, ReadOnlySpan<byte> key)
        {
            ulong keyHi = BinaryPrimitives.ReadUInt64BigEndian(key);
            ulong keyLo = BinaryPrimitives.ReadUInt64BigEndian(key.Slice(8));

            int n = boundaries.Length >> 4;
            int lo = 0;
            int hi = n - 1;
            ReadOnlySpan<byte> all = boundaries;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int midOffset = mid << 4;

                ulong midHi = BinaryPrimitives.ReadUInt64BigEndian(all.Slice(midOffset, 8));
                ulong midLo = BinaryPrimitives.ReadUInt64BigEndian(all.Slice(midOffset + 8, 8));

                bool midLeq = midHi < keyHi || (midHi == keyHi && midLo <= keyLo);
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

        /// <summary>
        /// <see cref="Array.BinarySearch{T}(T[], int, int, T)"/> over a UInt128[] subrange,
        /// used by radix variants once the bucket-bounded sub-array is identified.
        /// Returns <c>~lo</c> for an empty subrange.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Subarray(UInt128[] arr, int lo, int hi, UInt128 key)
        {
            int length = hi - lo;
            if (length <= 0)
            {
                return ~lo;
            }

            return Array.BinarySearch(arr, lo, length, key);
        }
    }
}
