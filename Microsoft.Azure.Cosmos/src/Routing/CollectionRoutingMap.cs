//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;

    /// <summary>
    /// Stored partition key ranges in an efficient way with some additional information and provides
    /// convenience methods for working with set of ranges.
    /// </summary>
    internal sealed class CollectionRoutingMap
    {
        /// <summary>
        /// Partition key range id to partition address and range.
        /// </summary>
        private readonly Dictionary<string, Tuple<PartitionKeyRange, ServiceIdentity>> rangeById;

        private readonly List<PartitionKeyRange> orderedPartitionKeyRanges;
        private readonly List<Range<string>> orderedRanges;
        private readonly string[] sortedMinBoundaries;
        private readonly UInt128[] sortedNumericBoundaries;
        private readonly byte[] sortedByteBoundaries; // flat: 16 bytes per range, big-endian
        private readonly bool hasNumericFastPath;

        /// <summary>
        /// Experimental variant selector for benchmarking the routing-map point lookup.
        /// Controlled by env var COSMOS_PKRANGE_VARIANT:
        ///   "string"     => Phase 1a only (string Array.BinarySearch)
        ///   "uint128"    => Phase 1a + UInt128 fast path  (default)
        ///   "bytespan-seq"  => Phase 1a + byte[16N] with SequenceCompareTo
        ///   "bytespan-hand" => Phase 1a + byte[16N] with hand-rolled ReadUInt64BE compare
        /// </summary>
        internal enum FastPathVariant
        {
            String,
            UInt128,
            BytespanSeq,
            BytespanHand,
        }

        internal static FastPathVariant ActiveVariant { get; set; } =
            ParseVariant(Environment.GetEnvironmentVariable("COSMOS_PKRANGE_VARIANT"));

        private static FastPathVariant ParseVariant(string s)
        {
            if (string.IsNullOrEmpty(s)) return FastPathVariant.UInt128;
            switch (s.Trim().ToLowerInvariant())
            {
                case "string": return FastPathVariant.String;
                case "uint128": return FastPathVariant.UInt128;
                case "bytespan-seq": return FastPathVariant.BytespanSeq;
                case "bytespan-hand": return FastPathVariant.BytespanHand;
                default: return FastPathVariant.UInt128;
            }
        }
        private readonly HashSet<string> goneRanges;
        private readonly static int InvalidPkRangeId = -1;

        internal int HighestNonOfflinePkRangeId { get; private set; }

        private readonly (IComparer<Range<string>> MinComparer, IComparer<Range<string>> MaxComparer) comparers;

        private CollectionRoutingMap(
            Dictionary<string, Tuple<PartitionKeyRange, ServiceIdentity>> rangeById,
            List<PartitionKeyRange> orderedPartitionKeyRanges,
            string collectionUniqueId,
            string changeFeedNextIfNoneMatch,
            bool useLengthAwareRangeComparer)
        {
            this.rangeById = rangeById;
            this.orderedPartitionKeyRanges = orderedPartitionKeyRanges;
            this.orderedRanges = orderedPartitionKeyRanges.Select(
                    range =>
                    new Range<string>(
                        range.MinInclusive,
                        range.MaxExclusive,
                        true,
                        false)).ToList();

            this.sortedMinBoundaries = new string[orderedPartitionKeyRanges.Count];
            for (int i = 0; i < orderedPartitionKeyRanges.Count; i++)
            {
                this.sortedMinBoundaries[i] = this.orderedRanges[i].Min;
            }

            // Build optional numeric boundary index for fast-path point lookups.
            // Only activates when all boundaries are parseable as 32-char hex (128-bit hash V2).
            UInt128[] numericBoundaries = new UInt128[orderedPartitionKeyRanges.Count];
            bool allParsed = true;
            for (int i = 0; i < orderedPartitionKeyRanges.Count; i++)
            {
                string min = this.sortedMinBoundaries[i];
                if (min.Length == 0)
                {
                    // First range min is "" (minimum inclusive) — maps to UInt128.MinValue
                    numericBoundaries[i] = UInt128.MinValue;
                }
                else if (CollectionRoutingMap.TryParseHex32ToUInt128(min, out UInt128 val))
                {
                    numericBoundaries[i] = val;
                }
                else
                {
                    allParsed = false;
                    break;
                }
            }

            this.sortedNumericBoundaries = allParsed ? numericBoundaries : null;
            this.hasNumericFastPath = allParsed;

            // Build parallel byte[] representation (16 bytes per boundary, big-endian).
            // Used when UseByteSpanFastPath is set — kept alongside UInt128 path for A/B benchmarking.
            if (allParsed)
            {
                byte[] byteBoundaries = new byte[orderedPartitionKeyRanges.Count * 16];
                for (int i = 0; i < orderedPartitionKeyRanges.Count; i++)
                {
                    string min = this.sortedMinBoundaries[i];
                    if (min.Length != 0)
                    {
                        CollectionRoutingMap.WriteHex32ToBytes(min, byteBoundaries, i * 16);
                    }
                    // empty min -> all zeros (already default)
                }

                this.sortedByteBoundaries = byteBoundaries;
            }

            this.CollectionUniqueId = collectionUniqueId;
            this.ChangeFeedNextIfNoneMatch = changeFeedNextIfNoneMatch;
            this.goneRanges = new HashSet<string>(orderedPartitionKeyRanges.SelectMany(r => r.Parents ?? Enumerable.Empty<string>()));

            this.HighestNonOfflinePkRangeId = orderedPartitionKeyRanges.Max(range =>
                {
                    int pkId = CollectionRoutingMap.InvalidPkRangeId;
                    if (!int.TryParse(range.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out pkId))
                    {
                        DefaultTrace.TraceCritical(
                            "Could not parse partition key range Id as int {0} for collectionRid {1}",
                            range.Id,
                            this.CollectionUniqueId);
                        throw new ArgumentException(string.Format(
                            CultureInfo.InvariantCulture,
                            "Could not parse partition key range Id as int {0} for collectionRid {1}",
                            range.Id,
                            this.CollectionUniqueId));
                    }
                    return range.Status == PartitionKeyRangeStatus.Offline ? CollectionRoutingMap.InvalidPkRangeId : pkId;
                });
            this.comparers = RangeComparerProvider.GetComparers(useLengthAwareRangeComparer);
        }

        public static CollectionRoutingMap TryCreateCompleteRoutingMap(
            IEnumerable<Tuple<PartitionKeyRange, ServiceIdentity>> ranges,
            string collectionUniqueId,
            bool useLengthAwareRangeComparer,
            string changeFeedNextIfNoneMatch = null)
        {
            Dictionary<string, Tuple<PartitionKeyRange, ServiceIdentity>> rangeById =
                new Dictionary<string, Tuple<PartitionKeyRange, ServiceIdentity>>(StringComparer.Ordinal);

            foreach (Tuple<PartitionKeyRange, ServiceIdentity> range in ranges)
            {
                rangeById[range.Item1.Id] = range;
            }

            List<Tuple<PartitionKeyRange, ServiceIdentity>> sortedRanges = rangeById.Values.ToList();
            sortedRanges.Sort(new MinPartitionKeyTupleComparer());
            List<PartitionKeyRange> orderedRanges = sortedRanges.Select(range => range.Item1).ToList();

            if (!IsCompleteSetOfRanges(orderedRanges))
            {
                return null;
            }

            return new CollectionRoutingMap(rangeById, orderedRanges, collectionUniqueId, changeFeedNextIfNoneMatch, useLengthAwareRangeComparer);
        }

        public string CollectionUniqueId { get; private set; }

        public string ChangeFeedNextIfNoneMatch { get; private set; }

        /// <summary>
        /// Ranges in increasing order.
        /// </summary>
        public IReadOnlyList<PartitionKeyRange> OrderedPartitionKeyRanges
        {
            get
            {
                return this.orderedPartitionKeyRanges;
            }
        }

        public IReadOnlyList<PartitionKeyRange> GetOverlappingRanges(Range<string> range)
        {
            return this.GetOverlappingRanges(new[] { range });
        }

        public IReadOnlyList<PartitionKeyRange> GetOverlappingRanges(IReadOnlyList<Range<string>> providedPartitionKeyRanges)
        {
            if (providedPartitionKeyRanges == null)
            {
                throw new ArgumentNullException("providedPartitionKeyRanges");
            }

            SortedList<string, PartitionKeyRange> partitionRanges = new SortedList<string, PartitionKeyRange>();

            // Algorithm: Use binary search to find the positions of the min key and max key in the routing map
            // Then within that two positions, check for overlapping partition key ranges
            foreach (Range<string> providedRange in providedPartitionKeyRanges)
            {
                int minIndex = this.orderedRanges.BinarySearch(providedRange, this.comparers.MinComparer);
                if (minIndex < 0)
                {
                    minIndex = Math.Max(0, (~minIndex) - 1);
                }

                int maxIndex = this.orderedRanges.BinarySearch(providedRange, this.comparers.MaxComparer);
                if (maxIndex < 0)
                {
                    maxIndex = Math.Min(this.OrderedPartitionKeyRanges.Count - 1, ~maxIndex);
                }

                for (int i = minIndex; i <= maxIndex; ++i)
                {
                    if (Range<string>.CheckOverlapping(this.orderedRanges[i], providedRange))
                    {
                        // Use orderedRanges[i].Min (already cached string) instead of
                        // OrderedPartitionKeyRanges[i].MinInclusive which triggers
                        // JsonSerializable.GetValue → JToken.ToObject on every access.
                        // See: https://github.com/Azure/azure-cosmos-dotnet-v3/issues/5747
                        partitionRanges[this.orderedRanges[i].Min] = this.OrderedPartitionKeyRanges[i];
                    }
                }
            }

            return new ReadOnlyCollection<PartitionKeyRange>(partitionRanges.Values);
        }

        public PartitionKeyRange GetRangeByEffectivePartitionKey(string effectivePartitionKeyValue)
        {
            if (string.CompareOrdinal(effectivePartitionKeyValue, PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey) >= 0)
            {
                throw new ArgumentException("effectivePartitionKeyValue");
            }

            if (string.CompareOrdinal(PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey, effectivePartitionKeyValue) == 0)
            {
                return this.orderedPartitionKeyRanges[0];
            }

            FastPathVariant variant = CollectionRoutingMap.ActiveVariant;

            // Span<byte> fast path with SequenceCompareTo (D).
            if (variant == FastPathVariant.BytespanSeq
                && this.hasNumericFastPath
                && effectivePartitionKeyValue.Length == 32)
            {
                Span<byte> epkBytes = stackalloc byte[16];
                if (CollectionRoutingMap.TryParseHex32ToBytes(effectivePartitionKeyValue, epkBytes))
                {
                    int index = CollectionRoutingMap.BinarySearchBytesSeq(this.sortedByteBoundaries, epkBytes);
                    if (index < 0)
                    {
                        index = ~index - 1;
                    }

                    return this.orderedPartitionKeyRanges[index];
                }
            }

            // Span<byte> fast path with hand-rolled ReadUInt64BE compare (E).
            if (variant == FastPathVariant.BytespanHand
                && this.hasNumericFastPath
                && effectivePartitionKeyValue.Length == 32)
            {
                Span<byte> epkBytes = stackalloc byte[16];
                if (CollectionRoutingMap.TryParseHex32ToBytes(effectivePartitionKeyValue, epkBytes))
                {
                    int index = CollectionRoutingMap.BinarySearchBytes(this.sortedByteBoundaries, epkBytes);
                    if (index < 0)
                    {
                        index = ~index - 1;
                    }

                    return this.orderedPartitionKeyRanges[index];
                }
            }

            // Numeric fast path: UInt128 comparison (C).
            if (variant == FastPathVariant.UInt128
                && this.hasNumericFastPath
                && CollectionRoutingMap.TryParseHex32ToUInt128(effectivePartitionKeyValue, out UInt128 numericEpk))
            {
                int index = Array.BinarySearch(
                    this.sortedNumericBoundaries,
                    numericEpk);

                if (index < 0)
                {
                    index = ~index - 1;
                }

                return this.orderedPartitionKeyRanges[index];
            }

            // String fallback / Phase 1a only (B).
            int idx = Array.BinarySearch(
                this.sortedMinBoundaries,
                effectivePartitionKeyValue,
                StringComparer.Ordinal);

            if (idx < 0)
            {
                idx = ~idx - 1;
                Debug.Assert(idx >= 0);
                Debug.Assert(this.orderedRanges[idx].Contains(effectivePartitionKeyValue));
            }

            return this.orderedPartitionKeyRanges[idx];
        }

        /// <summary>
        /// Point lookup using an opaque <see cref="EffectivePartitionKey"/> ref-struct over a
        /// 16-byte big-endian buffer. Skips the 32-char hex string round-trip entirely and
        /// runs the hand-rolled <see cref="BinarySearchBytes"/> compare against the byte-storage
        /// boundary array. Only valid when the numeric fast path is active (128-bit hash V2
        /// collections).
        /// </summary>
        public PartitionKeyRange GetRangeByEffectivePartitionKey(in EffectivePartitionKey effectivePartitionKey)
        {
            if (!this.hasNumericFastPath)
            {
                throw new InvalidOperationException(
                    "Numeric fast path is not available for this routing map. Use the string overload.");
            }

            ReadOnlySpan<byte> key = effectivePartitionKey.AsSpan();
            if (key.Length != 16)
            {
                throw new ArgumentException("V2 hash EPK must be exactly 16 bytes.", nameof(effectivePartitionKey));
            }

            // All-zero buffer maps to MinValue range — first range covers MinimumInclusive.
            bool allZero = true;
            for (int i = 0; i < 16; i++)
            {
                if (key[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }

            if (allZero)
            {
                return this.orderedPartitionKeyRanges[0];
            }

            int index = CollectionRoutingMap.BinarySearchBytes(this.sortedByteBoundaries, key);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return this.orderedPartitionKeyRanges[index];
        }

        /// <summary>
        /// Point lookup using a pre-parsed UInt128 effective partition key.
        /// Skips hex parsing overhead — callers that already have numeric EPK values
        /// (e.g., from MurmurHash3.Hash128) should prefer this overload.
        /// Only valid when the numeric fast path is active (128-bit hash V2 collections).
        /// </summary>
        public PartitionKeyRange GetRangeByEffectivePartitionKey(UInt128 effectivePartitionKeyValue)
        {
            if (!this.hasNumericFastPath)
            {
                throw new InvalidOperationException(
                    "Numeric fast path is not available for this routing map. Use the string overload.");
            }

            if (effectivePartitionKeyValue == UInt128.MinValue)
            {
                return this.orderedPartitionKeyRanges[0];
            }

            int index = Array.BinarySearch(
                this.sortedNumericBoundaries,
                effectivePartitionKeyValue);

            if (index < 0)
            {
                index = ~index - 1;
            }

            return this.orderedPartitionKeyRanges[index];
        }

        /// <summary>
        /// Returns true if this routing map has the numeric fast path available
        /// (all boundaries are 32-char hex, i.e., 128-bit hash V2 partitioning).
        /// </summary>
        internal bool HasNumericFastPath => this.hasNumericFastPath;

        public PartitionKeyRange TryGetRangeByPartitionKeyRangeId(string partitionKeyRangeId)
        {
            Tuple<PartitionKeyRange, ServiceIdentity> addresses;
            if (this.rangeById.TryGetValue(partitionKeyRangeId, out addresses))
            {
                return addresses.Item1;
            }

            return null;
        }

        public ServiceIdentity TryGetInfoByPartitionKeyRangeId(string partitionKeyRangeId)
        {
            Tuple<PartitionKeyRange, ServiceIdentity> addresses;
            if (this.rangeById.TryGetValue(partitionKeyRangeId, out addresses))
            {
                return addresses.Item2;
            }

            return null;
        }

        public CollectionRoutingMap TryCombine(
            IEnumerable<Tuple<PartitionKeyRange, ServiceIdentity>> ranges,
            string changeFeedNextIfNoneMatch,
            bool useLengthAwareComparer)
        {
            HashSet<string> newGoneRanges = new HashSet<string>(ranges.SelectMany(tuple => tuple.Item1.Parents ?? Enumerable.Empty<string>()));
            newGoneRanges.UnionWith(this.goneRanges);

            Dictionary<string, Tuple<PartitionKeyRange, ServiceIdentity>> newRangeById =
                this.rangeById.Values.Where(tuple => !newGoneRanges.Contains(tuple.Item1.Id)).ToDictionary(tuple => tuple.Item1.Id, StringComparer.Ordinal);

            foreach (Tuple<PartitionKeyRange, ServiceIdentity> tuple in ranges.Where(tuple => !newGoneRanges.Contains(tuple.Item1.Id)))
            {
                newRangeById[tuple.Item1.Id] = tuple;

                DefaultTrace.TraceInformation(
                    "CollectionRoutingMap.TryCombine newRangeById[{0}] = {1}",
                    tuple.Item1.Id, tuple);
            }

            List<Tuple<PartitionKeyRange, ServiceIdentity>> sortedRanges = newRangeById.Values.ToList();

            sortedRanges.Sort(new MinPartitionKeyTupleComparer());
            List<PartitionKeyRange> newOrderedRanges = sortedRanges.Select(range => range.Item1).ToList();

            if (!IsCompleteSetOfRanges(newOrderedRanges))
            {
                return null;
            }

            return new CollectionRoutingMap(newRangeById, newOrderedRanges, this.CollectionUniqueId, changeFeedNextIfNoneMatch, useLengthAwareComparer);
        }

        private class MinPartitionKeyTupleComparer : IComparer<Tuple<PartitionKeyRange, ServiceIdentity>>
        {
            public int Compare(Tuple<PartitionKeyRange, ServiceIdentity> left, Tuple<PartitionKeyRange, ServiceIdentity> right)
            {
                return string.CompareOrdinal(left.Item1.MinInclusive, right.Item1.MinInclusive);
            }
        }

        private static bool IsCompleteSetOfRanges(IList<PartitionKeyRange> orderedRanges)
        {
            bool isComplete = false;
            if (orderedRanges.Count > 0)
            {
                PartitionKeyRange firstRange = orderedRanges[0];
                PartitionKeyRange lastRange = orderedRanges[orderedRanges.Count - 1];
                isComplete = string.CompareOrdinal(firstRange.MinInclusive, PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey) == 0;
                isComplete &= string.CompareOrdinal(lastRange.MaxExclusive, PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey) == 0;

                for (int i = 1; i < orderedRanges.Count; i++)
                {
                    PartitionKeyRange previousRange = orderedRanges[i - 1];
                    PartitionKeyRange currentRange = orderedRanges[i];
                    isComplete &= previousRange.MaxExclusive.Equals(currentRange.MinInclusive);

                    if (!isComplete)
                    {
                        if (string.CompareOrdinal(previousRange.MaxExclusive, currentRange.MinInclusive) > 0)
                        {
                            throw new InvalidOperationException("Ranges overlap");
                        }

                        break;
                    }
                }
            }

            return isComplete;
        }

        public bool IsGone(string partitionKeyRangeId)
        {
            return this.goneRanges.Contains(partitionKeyRangeId);
        }

        /// <summary>
        /// Zero-alloc parser for 32-char continuous hex strings into UInt128.
        /// Big-endian: first 16 hex chars → high 64 bits, last 16 → low 64 bits.
        /// Returns false for non-32-char or non-hex input.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseHex32ToUInt128(string hex, out UInt128 result)
        {
            if (hex.Length != 32)
            {
                result = default;
                return false;
            }

            if (CollectionRoutingMap.TryParseHexUInt64(hex, 0, out ulong high)
                && CollectionRoutingMap.TryParseHexUInt64(hex, 16, out ulong low))
            {
                result = UInt128.Create(low, high);
                return true;
            }

            result = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryParseHexUInt64(string hex, int offset, out ulong value)
        {
            value = 0;
            for (int i = 0; i < 16; i++)
            {
                int nibble = CollectionRoutingMap.HexCharToNibble(hex[offset + i]);
                if (nibble < 0)
                {
                    return false;
                }

                value = (value << 4) | (uint)nibble;
            }

            return true;
        }

        /// <summary>
        /// Parses a 32-char hex string into a 16-byte big-endian span. Returns false on bad input.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseHex32ToBytes(string hex, Span<byte> destination)
        {
            if (hex.Length != 32 || destination.Length < 16)
            {
                return false;
            }

            for (int i = 0; i < 16; i++)
            {
                int hi = CollectionRoutingMap.HexCharToNibble(hex[2 * i]);
                int lo = CollectionRoutingMap.HexCharToNibble(hex[(2 * i) + 1]);
                if ((hi | lo) < 0)
                {
                    return false;
                }

                destination[i] = (byte)((hi << 4) | lo);
            }

            return true;
        }

        private static void WriteHex32ToBytes(string hex, byte[] dest, int offset)
        {
            for (int i = 0; i < 16; i++)
            {
                int hi = CollectionRoutingMap.HexCharToNibble(hex[2 * i]);
                int lo = CollectionRoutingMap.HexCharToNibble(hex[(2 * i) + 1]);
                dest[offset + i] = (byte)((hi << 4) | lo);
            }
        }

        /// <summary>
        /// Binary-searches a flat byte[] of 16-byte big-endian boundaries for the given key.
        /// Returns the index (or bitwise-complement of insertion point), matching Array.BinarySearch semantics.
        /// </summary>
        /// <summary>
        /// Variant D: SequenceCompareTo-based binary search.
        /// </summary>
        private static int BinarySearchBytesSeq(byte[] boundaries, ReadOnlySpan<byte> key)
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
        /// Variant E: hand-rolled binary search using two big-endian ulong reads per probe.
        /// </summary>
        private static int BinarySearchBytes(byte[] boundaries, ReadOnlySpan<byte> key)
        {
            // Key is exactly 16 bytes big-endian; read it once outside the loop.
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int HexCharToNibble(char c)
        {
            if ((uint)(c - '0') <= 9)
            {
                return c - '0';
            }

            if ((uint)(c - 'a') <= 5)
            {
                return c - 'a' + 10;
            }

            if ((uint)(c - 'A') <= 5)
            {
                return c - 'A' + 10;
            }

            return -1;
        }
    }
}
