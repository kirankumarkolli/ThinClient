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
        private readonly byte[] sortedByteBoundaries; // flat: 16 bytes per range MIN (inclusive), big-endian
        private readonly byte[] sortedMaxBytes;       // flat: 16 bytes per range MAX (exclusive), big-endian. Layer 3.
        private readonly bool hasNumericFastPath;

        // Radix index: bucketStart256[b] = first index in sortedNumericBoundaries whose top byte >= b.
        // Sentinel at [256] = ranges.Count. Built only when hasNumericFastPath. ~1 KB per map.
        private readonly int[] bucketStart256;

        // Radix index: bucketStart64K[b] = first index whose top 16 bits >= b.
        // Sentinel at [65536] = ranges.Count. Built only when hasNumericFastPath. ~256 KB per map.
        private readonly int[] bucketStart64K;

        // Payload SoA (Layer 1, variant G).
        // Parallel array of PartitionKeyRange references with the same ordering as
        // sortedByteBoundaries and orderedPartitionKeyRanges. Reads in the V2-hash
        // hot path go through this T[] (single bounds-checked index) instead of
        // List<T>, avoiding the List indirection layer. orderedPartitionKeyRanges
        // is retained because it backs the public OrderedPartitionKeyRanges
        // IReadOnlyList<PartitionKeyRange> property.
        private readonly PartitionKeyRange[] sortedRangePayloads;
        private readonly string[] sortedRangeIds;
        private readonly ServiceIdentity[] sortedServiceIdentities;

        // Last-resolved-index cache for the LastResolvedCache variant. Single int, racy reads OK
        // (worst case: cache miss leads to fallthrough to UInt128 binary search).
        private int lastResolvedIndex;

        /// <summary>
        /// Experimental variant selector for benchmarking the routing-map point lookup.
        /// Controlled by env var COSMOS_PKRANGE_VARIANT:
        ///   "string"          => Phase 1a only (string Array.BinarySearch)
        ///   "uint128"         => Phase 1a + UInt128 fast path  (default)
        ///   "bytespan-seq"    => Phase 1a + byte[16N] with SequenceCompareTo
        ///   "bytespan-hand"   => Phase 1a + byte[16N] with hand-rolled ReadUInt64BE compare
        ///   "radix1"          => UInt128 + top-byte (256-bucket) radix dispatch
        ///   "radix2"          => UInt128 + top-16-bit (65536-bucket) radix dispatch
        ///   "cache-last"      => UInt128 + last-resolved-index cache fast path
        ///   "soa"             => bytespan-hand + payload SoA (parallel arrays, no List indirection)
        ///                        + branchless binary search + gated software prefetch.
        ///   "string-soa"      => string Array.BinarySearch comparison + payload SoA + branchless
        ///                        string binary search. Targets V1 hash / hierarchical PK /
        ///                        variable-length-EPK collections that cannot use the V2 byte
        ///                        fast path. Same SoA bookkeeping as variant G but keeps
        ///                        string.CompareOrdinal as the per-probe comparator.
        /// </summary>
        internal enum FastPathVariant
        {
            String,
            UInt128,
            BytespanSeq,
            BytespanHand,
            Radix1,
            Radix2,
            CacheLast,
            Soa,
            StringSoa,
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
                case "radix1": return FastPathVariant.Radix1;
                case "radix2": return FastPathVariant.Radix2;
                case "cache-last": return FastPathVariant.CacheLast;
                case "soa": return FastPathVariant.Soa;
                case "string-soa": return FastPathVariant.StringSoa;
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

            // Layer 1 — Payload SoA. Built unconditionally (not gated on hasNumericFastPath)
            // so the string path (variant H = StringSoa) and the byte path (variant G = Soa)
            // both share the same parallel arrays. sortedRangePayloads is a plain T[] (no
            // List<T>) so SoA hot paths return sortedRangePayloads[index] without the
            // List indirection. orderedPartitionKeyRanges is retained because it backs
            // the public OrderedPartitionKeyRanges IReadOnlyList<PartitionKeyRange> property.
            {
                int n = orderedPartitionKeyRanges.Count;
                PartitionKeyRange[] payloads = new PartitionKeyRange[n];
                string[] rangeIds = new string[n];
                ServiceIdentity[] serviceIdentities = new ServiceIdentity[n];
                for (int i = 0; i < n; i++)
                {
                    PartitionKeyRange pkr = orderedPartitionKeyRanges[i];
                    payloads[i] = pkr;
                    rangeIds[i] = pkr.Id;
                    if (rangeById.TryGetValue(pkr.Id, out Tuple<PartitionKeyRange, ServiceIdentity> entry))
                    {
                        serviceIdentities[i] = entry.Item2;
                    }
                }

                this.sortedRangePayloads = payloads;
                this.sortedRangeIds = rangeIds;
                this.sortedServiceIdentities = serviceIdentities;
            }

            // Build parallel byte[] representation (16 bytes per boundary, big-endian).
            // V2-hash-only — gated on hasNumericFastPath since it requires 32-char hex Mins.
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

                // Layer 3 — packed MaxExclusive boundaries for SoA GetOverlappingRangesByBytes.
                // Mirrors sortedByteBoundaries but stores each range's MaxExclusive as 16 bytes
                // big-endian. The last range's MaxExclusive is the sentinel "FF"
                // (PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey) which is not
                // a 32-char hex string; we encode it as all-0xFF to act as positive infinity
                // for byte-wise compares.
                int n = orderedPartitionKeyRanges.Count;
                byte[] maxBytes = new byte[n * 16];
                for (int i = 0; i < n; i++)
                {
                    string max = orderedPartitionKeyRanges[i].MaxExclusive;
                    if (max != null && max.Length == 32)
                    {
                        CollectionRoutingMap.WriteHex32ToBytes(max, maxBytes, i * 16);
                    }
                    else
                    {
                        // Sentinel for the trailing "FF" (MaximumExclusive). All-0xFF is the
                        // greatest 16-byte big-endian value, preserving compare ordering.
                        for (int j = 0; j < 16; j++)
                        {
                            maxBytes[(i * 16) + j] = 0xFF;
                        }
                    }
                }

                this.sortedMaxBytes = maxBytes;
            }

            // Radix indexes for Radix1/Radix2/CacheLast variants. Built only when numeric
            // fast path is active (V2 hash collections). Top bits are extracted from the
            // original hex string boundaries (avoids needing UInt128 shift operators).
            if (allParsed)
            {
                this.bucketStart256 = BuildBucketStarts(this.sortedMinBoundaries, 8);
                this.bucketStart64K = BuildBucketStarts(this.sortedMinBoundaries, 16);
            }

            this.lastResolvedIndex = 0;

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

        /// <summary>
        /// Layer 3 — SoA range query. Returns the contiguous block of partition key ranges
        /// that overlap [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).
        /// Both inputs must be exactly 16 bytes big-endian (V2 hash EPK form). Only valid
        /// when the numeric fast path is available — i.e. the collection's partition
        /// boundaries are V2 128-bit hash. For V1 / hierarchical / variable-length EPKs,
        /// callers must continue to use the string-based <see cref="GetOverlappingRanges(Range{string})"/>
        /// overloads.
        ///
        /// This overload exists so internal call sites that already hold raw EPK bytes
        /// (post-<see cref="EffectivePartitionKey"/> plumbing) can avoid the per-iteration
        /// <c>List&lt;Range&lt;string&gt;&gt;.BinarySearch</c> + <c>IComparer</c> dispatch
        /// in the standard overload — replacing it with two branchless byte binary searches
        /// against the packed boundary arrays plus an array-index walk over the SoA payload.
        /// </summary>
        public IReadOnlyList<PartitionKeyRange> GetOverlappingRangesByBytes(
            ReadOnlySpan<byte> minInclusive,
            ReadOnlySpan<byte> maxExclusive)
        {
            if (!this.hasNumericFastPath)
            {
                throw new InvalidOperationException(
                    "Numeric fast path is not available for this routing map. Use the string overload.");
            }

            if (minInclusive.Length != 16 || maxExclusive.Length != 16)
            {
                throw new ArgumentException("V2 hash EPK ranges must be exactly 16 bytes each.");
            }

            int n = this.sortedByteBoundaries.Length >> 4;

            // minIdx = largest range index whose Min boundary is <= request.minInclusive.
            // BinarySearchBytesBranchless returns ~lo with upper-bound semantics; ~ret - 1
            // gives that "largest <= key" position.
            int minIdx = ~CollectionRoutingMap.BinarySearchBytesBranchless(this.sortedByteBoundaries, minInclusive) - 1;
            if (minIdx < 0)
            {
                minIdx = 0;
            }

            // maxIdx = largest range index whose Min boundary is <= request.maxExclusive,
            // then trim by one if that range's Min equals request.maxExclusive (the range
            // starts at the request's exclusive upper bound and therefore does not overlap).
            int maxIdx = ~CollectionRoutingMap.BinarySearchBytesBranchless(this.sortedByteBoundaries, maxExclusive) - 1;
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

        /// <summary>
        /// Layer 3 (variant H) — SoA range query keyed by string Min boundaries. Mirrors
        /// <see cref="GetOverlappingRangesByBytes"/> but uses
        /// <see cref="BinarySearchStringsBranchless"/> + <see cref="sortedMinBoundaries"/>
        /// so it works on any topology (V1 hash, V2 hash, hierarchical PK, variable-length
        /// EPKs). Avoids the per-iteration <c>List&lt;Range&lt;string&gt;&gt;.BinarySearch</c>
        /// + <c>IComparer</c> dispatch in the standard
        /// <see cref="GetOverlappingRanges(Range{string})"/> overload.
        ///
        /// <paramref name="minInclusive"/> may be the empty string (denoting the
        /// MinimumInclusive sentinel); <paramref name="maxExclusive"/> may be the literal
        /// "FF" (denoting the MaximumExclusive sentinel) -- both are treated correctly by
        /// ordinal compare given the boundaries array's natural ordering.
        /// </summary>
        public IReadOnlyList<PartitionKeyRange> GetOverlappingRangesByStrings(
            string minInclusive,
            string maxExclusive)
        {
            if (minInclusive == null) throw new ArgumentNullException(nameof(minInclusive));
            if (maxExclusive == null) throw new ArgumentNullException(nameof(maxExclusive));
            if (string.CompareOrdinal(minInclusive, maxExclusive) >= 0)
            {
                throw new ArgumentException("minInclusive must be strictly less than maxExclusive.");
            }

            int n = this.sortedMinBoundaries.Length;

            int minIdx = ~CollectionRoutingMap.BinarySearchStringsBranchless(this.sortedMinBoundaries, minInclusive) - 1;
            if (minIdx < 0)
            {
                minIdx = 0;
            }

            int maxIdx = ~CollectionRoutingMap.BinarySearchStringsBranchless(this.sortedMinBoundaries, maxExclusive) - 1;
            if (maxIdx < 0)
            {
                maxIdx = 0;
            }
            else if (maxIdx >= n)
            {
                maxIdx = n - 1;
            }
            else if (maxIdx > minIdx
                && string.CompareOrdinal(this.sortedMinBoundaries[maxIdx], maxExclusive) == 0)
            {
                // The range whose Min equals maxExclusive starts at the request's exclusive
                // upper bound and therefore does not overlap.
                maxIdx--;
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

            // SoA fast path (G): bytespan-hand search + payload SoA (no List<T> indirection)
            // + branchless binary search + gated software prefetch (Layers 1 + 2).
            if (variant == FastPathVariant.Soa
                && this.hasNumericFastPath
                && effectivePartitionKeyValue.Length == 32)
            {
                Span<byte> epkBytes = stackalloc byte[16];
                if (CollectionRoutingMap.TryParseHex32ToBytes(effectivePartitionKeyValue, epkBytes))
                {
                    int index = CollectionRoutingMap.BinarySearchBytesBranchless(this.sortedByteBoundaries, epkBytes);
                    if (index < 0)
                    {
                        index = ~index - 1;
                    }

                    return this.sortedRangePayloads[index];
                }
            }

            // String-SoA fast path (H): string Array.BinarySearch comparator + payload SoA
            // (no List<T> indirection) + branchless string binary search. Targets V1 hash /
            // hierarchical PK / variable-length-EPK collections that cannot use the V2 byte
            // fast path (G). Independent of hasNumericFastPath: works on any topology.
            if (variant == FastPathVariant.StringSoa)
            {
                int index = CollectionRoutingMap.BinarySearchStringsBranchless(
                    this.sortedMinBoundaries, effectivePartitionKeyValue);
                if (index < 0)
                {
                    index = ~index - 1;
                }

                return this.sortedRangePayloads[index];
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

            // Radix1: top-byte (256-bucket) dispatch then narrow UInt128 binary search.
            if (variant == FastPathVariant.Radix1
                && this.hasNumericFastPath
                && effectivePartitionKeyValue.Length == 32
                && CollectionRoutingMap.TryParseHex32ToUInt128(effectivePartitionKeyValue, out UInt128 epkR1))
            {
                int b = TopBitsFromHex(effectivePartitionKeyValue, 2);
                int lo = this.bucketStart256[b];
                int hi = this.bucketStart256[b + 1]; // sentinel handles b==255
                int index = SubarrayBinarySearch(this.sortedNumericBoundaries, lo, hi, epkR1);
                if (index < 0)
                {
                    index = ~index - 1;
                    if (index < 0) index = 0;
                }

                return this.orderedPartitionKeyRanges[index];
            }

            // Radix2: top-16-bit (65536-bucket) dispatch then narrow UInt128 binary search.
            if (variant == FastPathVariant.Radix2
                && this.hasNumericFastPath
                && effectivePartitionKeyValue.Length == 32
                && CollectionRoutingMap.TryParseHex32ToUInt128(effectivePartitionKeyValue, out UInt128 epkR2))
            {
                int b = TopBitsFromHex(effectivePartitionKeyValue, 4);
                int lo = this.bucketStart64K[b];
                int hi = this.bucketStart64K[b + 1];
                int index = SubarrayBinarySearch(this.sortedNumericBoundaries, lo, hi, epkR2);
                if (index < 0)
                {
                    index = ~index - 1;
                    if (index < 0) index = 0;
                }

                return this.orderedPartitionKeyRanges[index];
            }

            // CacheLast: try the last resolved index first (range contains EPK?), else UInt128 BS.
            if (variant == FastPathVariant.CacheLast
                && this.hasNumericFastPath
                && CollectionRoutingMap.TryParseHex32ToUInt128(effectivePartitionKeyValue, out UInt128 epkCL))
            {
                int cached = this.lastResolvedIndex;
                UInt128[] mins = this.sortedNumericBoundaries;
                if ((uint)cached < (uint)mins.Length)
                {
                    bool minOk = epkCL >= mins[cached];
                    bool maxOk = (cached + 1 == mins.Length) || epkCL < mins[cached + 1];
                    if (minOk && maxOk)
                    {
                        return this.orderedPartitionKeyRanges[cached];
                    }
                }

                int index = Array.BinarySearch(mins, epkCL);
                if (index < 0)
                {
                    index = ~index - 1;
                }

                this.lastResolvedIndex = index;
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

            // Variant-G: branchless binary search + gated prefetch + SoA payload.
            if (CollectionRoutingMap.ActiveVariant == FastPathVariant.Soa)
            {
                int index = CollectionRoutingMap.BinarySearchBytesBranchless(this.sortedByteBoundaries, key);
                if (index < 0)
                {
                    index = ~index - 1;
                }

                return this.sortedRangePayloads[index];
            }
            else
            {
                int index = CollectionRoutingMap.BinarySearchBytes(this.sortedByteBoundaries, key);
                if (index < 0)
                {
                    index = ~index - 1;
                }

                return this.orderedPartitionKeyRanges[index];
            }
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
        /// Builds a radix bucket-start index over a sorted string[] of 32-char hex boundaries
        /// (with index 0 possibly being the "" sentinel that maps to all zeros), dispatching by
        /// the top <paramref name="topBits"/> bits of each boundary. Returned array has length
        /// (1 &lt;&lt; topBits) + 1; entry [b] is the smallest index whose top bits &gt;= b, with
        /// [last] = boundaries.Length acting as a sentinel so callers can read [b+1] unconditionally.
        /// </summary>
        private static int[] BuildBucketStarts(string[] sortedMinBoundaries, int topBits)
        {
            int bucketCount = 1 << topBits;
            int[] starts = new int[bucketCount + 1];
            int hexChars = topBits / 4; // 8 bits => 2 hex chars; 16 bits => 4 hex chars

            int j = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                while (j < sortedMinBoundaries.Length
                    && TopBitsFromHex(sortedMinBoundaries[j], hexChars) < b)
                {
                    j++;
                }

                starts[b] = j;
            }

            starts[bucketCount] = sortedMinBoundaries.Length;
            return starts;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TopBitsFromHex(string hex, int hexChars)
        {
            // Empty (first-range) sentinel — minimum boundary, top bits = 0.
            if (hex.Length == 0)
            {
                return 0;
            }

            int v = 0;
            for (int i = 0; i < hexChars; i++)
            {
                v = (v << 4) | CollectionRoutingMap.HexCharToNibble(hex[i]);
            }

            return v;
        }

        /// <summary>
        /// Array.BinarySearch over <paramref name="arr"/>[<paramref name="lo"/>..<paramref name="hi"/>)
        /// for <paramref name="key"/>. Returns matching index or bitwise-complement of insertion
        /// point (Array.BinarySearch semantics, but bounded to the supplied subrange).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SubarrayBinarySearch(UInt128[] arr, int lo, int hi, UInt128 key)
        {
            int length = hi - lo;
            if (length <= 0)
            {
                return ~lo;
            }

            return Array.BinarySearch(arr, lo, length, key);
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

        /// <summary>
        /// Branchless binary search over the packed 16-byte boundary array. Variant G.
        /// Differences vs <see cref="BinarySearchBytes"/>:
        ///   1. Uses upper-bound semantics (<c>mid &lt;= key</c>) instead of the existing
        ///      <see cref="BinarySearchBytes"/>'s strict-less + equality-short-circuit form.
        ///      With upper-bound semantics, the answer is uniformly <c>lo - 1</c> regardless
        ///      of whether an exact match exists, so the loop body has no equality branch.
        ///      The method always returns <c>~lo</c>; callers' standard
        ///      <c>if (index &lt; 0) index = ~index - 1;</c> handling produces <c>lo - 1</c>,
        ///      which is the largest boundary index &lt;= key (exact-match-or-not).
        ///   2. The probe-direction update (lo / hi) uses arithmetic-mask selection so the
        ///      JIT can lower it to cmov-style code, avoiding the ~50%-mispredict-rate branch
        ///      on each iteration of the data-dependent probe direction.
        ///
        /// Software prefetch was evaluated but not enabled here because this assembly
        /// targets netstandard2.0, which doesn't expose <c>System.Runtime.Intrinsics.X86.Sse</c>.
        /// A conditionally-compiled prefetch path could be added if/when the project gets
        /// a net6.0 (or later) TFM; the branchless update alone is the primary win.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BinarySearchBytesBranchless(byte[] boundaries, ReadOnlySpan<byte> key)
        {
            // Key is exactly 16 bytes big-endian; read it once outside the loop.
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

                // mid <= key  iff  midHi < keyHi  OR  (midHi == keyHi AND midLo <= keyLo).
                // Upper-bound semantics: "advance lo past mid when boundary[mid] <= key".
                bool midLeq = midHi < keyHi || (midHi == keyHi && midLo <= keyLo);
                int midLeqInt = System.Runtime.CompilerServices.Unsafe.As<bool, byte>(ref midLeq);

                // mask = -1 (all bits) when mid<=key, else 0. Branchless lo/hi update.
                int mask = -midLeqInt;
                int newLo = (mid + 1) & mask;
                int newHi = (mid - 1) & ~mask;
                int keepLo = lo & ~mask;
                int keepHi = hi & mask;
                lo = newLo | keepLo;
                hi = newHi | keepHi;
            }

            // Always return ~lo. Caller does ~index - 1 → lo - 1, which is the largest
            // index with boundary <= key under the upper-bound semantics above.
            return ~lo;
        }

        /// <summary>
        /// Branchless binary search over the string Min boundaries. Variant H (StringSoa).
        /// Mirrors <see cref="BinarySearchBytesBranchless"/> but probes via
        /// <see cref="string.CompareOrdinal(string, string)"/> so it works on any boundary
        /// representation -- V1 hash (10-char hex), V2 hash (32-char hex), or hierarchical /
        /// variable-length EPK strings.
        ///
        /// Uses upper-bound semantics (<c>mid &lt;= key</c>) so the answer is uniformly
        /// <c>lo - 1</c> regardless of whether an exact match exists, and the probe-direction
        /// update is arithmetic-mask-driven so the JIT can lower it to cmov-style code.
        /// Treats the empty string ("") as &lt; every non-empty string, matching the
        /// MinimumInclusive sentinel ordering used by the caller.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BinarySearchStringsBranchless(string[] boundaries, string key)
        {
            int lo = 0;
            int hi = boundaries.Length - 1;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);

                // mid <= key under ordinal compare. boundaries[mid] is the Min boundary;
                // CompareOrdinal returns < 0 when mid < key, 0 when equal, > 0 when mid > key.
                bool midLeq = string.CompareOrdinal(boundaries[mid], key) <= 0;
                int midLeqInt = System.Runtime.CompilerServices.Unsafe.As<bool, byte>(ref midLeq);

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
