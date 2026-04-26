//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.Linq;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Cosmos.Routing.FastPathVariants;
    using Microsoft.Azure.Cosmos.Routing.FastPathVariants.Internal;
    using Microsoft.Azure.Cosmos.Routing.FastPathVariants.Strategies;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;
    using UInt128 = Microsoft.Azure.Cosmos.UInt128;

    /// <summary>
    /// Stored partition key ranges in an efficient way with some additional information and provides
    /// convenience methods for working with set of ranges.
    /// </summary>
    /// <remarks>
    /// Point lookup by EPK is delegated to a single <see cref="IRoutingFastPathStrategy"/>
    /// chosen at construction time by <see cref="FastPathStrategyFactory"/> from
    /// <see cref="FastPathVariantSelector.ActiveVariant"/>. The strategy holds its own
    /// snapshot of whatever range data it needs — there is no shared variant state on the
    /// map. See <c>Routing/FastPathVariants/</c> for the strategy implementations.
    /// </remarks>
    internal sealed class CollectionRoutingMap
    {
        /// <summary>
        /// Partition key range id to partition address and range.
        /// </summary>
        private readonly Dictionary<string, Tuple<PartitionKeyRange, ServiceIdentity>> rangeById;

        private readonly List<PartitionKeyRange> orderedPartitionKeyRanges;
        private readonly List<Range<string>> orderedRanges;
        private readonly bool hasNumericFastPath;
        private readonly IRoutingFastPathStrategy fastPathStrategy;
        private readonly HashSet<string> goneRanges;
        private static readonly int InvalidPkRangeId = -1;
        private readonly (IComparer<Range<string>> MinComparer, IComparer<Range<string>> MaxComparer) comparers;

        internal int HighestNonOfflinePkRangeId { get; private set; }

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

            this.hasNumericFastPath = HasV2HashBoundaries(orderedPartitionKeyRanges);
            this.fastPathStrategy = FastPathStrategyFactory.Create(
                FastPathVariantSelector.ActiveVariant,
                orderedPartitionKeyRanges,
                this.hasNumericFastPath);

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

        /// <summary>
        /// True iff every range's <see cref="PartitionKeyRange.MinInclusive"/> boundary is either
        /// the empty sentinel ("") or a 32-character hex string parseable as a 128-bit value.
        /// Determines whether numeric / byte / radix fast-path strategies are constructible.
        /// </summary>
        private static bool HasV2HashBoundaries(IReadOnlyList<PartitionKeyRange> orderedRanges)
        {
            for (int i = 0; i < orderedRanges.Count; i++)
            {
                string min = orderedRanges[i].MinInclusive;
                if (min.Length == 0)
                {
                    continue;
                }

                if (!HexCodec.TryParseHex32ToUInt128(min, out _))
                {
                    return false;
                }
            }

            return true;
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
        public IReadOnlyList<PartitionKeyRange> OrderedPartitionKeyRanges => this.orderedPartitionKeyRanges;

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
                        partitionRanges[this.orderedRanges[i].Min] = this.OrderedPartitionKeyRanges[i];
                    }
                }
            }

            return new ReadOnlyCollection<PartitionKeyRange>(partitionRanges.Values);
        }

        /// <summary>
        /// Range query over packed 16-byte EPKs, valid only when the active fast-path strategy
        /// is <see cref="SoaStrategy"/>. Throws otherwise.
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

            if (this.fastPathStrategy is SoaStrategy soa)
            {
                return soa.GetOverlappingRangesByBytes(minInclusive, maxExclusive);
            }

            throw new InvalidOperationException(
                "GetOverlappingRangesByBytes requires the SoA fast-path strategy. " +
                "Set COSMOS_PKRANGE_VARIANT=soa.");
        }

        /// <summary>
        /// Range query over string Min boundaries, valid only when the active fast-path
        /// strategy is <see cref="StringSoaStrategy"/>. Works on any topology
        /// (V1 hash, V2 hash, hierarchical PK, variable-length EPKs).
        /// </summary>
        public IReadOnlyList<PartitionKeyRange> GetOverlappingRangesByStrings(
            string minInclusive,
            string maxExclusive)
        {
            if (this.fastPathStrategy is StringSoaStrategy stringSoa)
            {
                return stringSoa.GetOverlappingRangesByStrings(minInclusive, maxExclusive);
            }

            throw new InvalidOperationException(
                "GetOverlappingRangesByStrings requires the StringSoa fast-path strategy. " +
                "Set COSMOS_PKRANGE_VARIANT=string-soa.");
        }

        public PartitionKeyRange GetRangeByEffectivePartitionKey(string effectivePartitionKeyValue) =>
            this.fastPathStrategy.Resolve(effectivePartitionKeyValue);

        /// <summary>
        /// Point lookup using an opaque <see cref="EffectivePartitionKey"/> ref-struct over a
        /// 16-byte big-endian buffer. Only valid when the numeric fast path is active.
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

            return this.fastPathStrategy.Resolve(in effectivePartitionKey);
        }

        /// <summary>
        /// Point lookup using a pre-parsed <see cref="UInt128"/> EPK. Only valid when the
        /// numeric fast path is active.
        /// </summary>
        public PartitionKeyRange GetRangeByEffectivePartitionKey(UInt128 effectivePartitionKeyValue)
        {
            if (!this.hasNumericFastPath)
            {
                throw new InvalidOperationException(
                    "Numeric fast path is not available for this routing map. Use the string overload.");
            }

            return this.fastPathStrategy.Resolve(effectivePartitionKeyValue);
        }

        /// <summary>
        /// Returns true if this routing map has the numeric fast path available
        /// (all boundaries are 32-char hex, i.e. 128-bit hash V2 partitioning).
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
    }
}
