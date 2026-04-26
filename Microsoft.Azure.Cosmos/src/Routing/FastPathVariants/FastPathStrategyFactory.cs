//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants
{
    using System.Collections.Generic;
    using Microsoft.Azure.Cosmos.Routing.FastPathVariants.Strategies;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Builds a single <see cref="IRoutingFastPathStrategy"/> for a routing-map instance,
    /// based on the requested <see cref="FastPathVariant"/> and whether the map's boundaries
    /// support the numeric (V2-hash) fast path.
    /// </summary>
    /// <remarks>
    /// Adding a new variant requires:
    /// <list type="number">
    /// <item><description>Adding the enum value to <see cref="FastPathVariant"/>.</description></item>
    /// <item><description>Adding a parse case in <see cref="FastPathVariantSelector.Parse(string)"/>.</description></item>
    /// <item><description>Adding a new <c>{Name}Strategy.cs</c> file under <c>Strategies/</c>.</description></item>
    /// <item><description>Adding a new <c>case</c> below.</description></item>
    /// </list>
    /// No edits to <see cref="CollectionRoutingMap"/> are required.
    /// </remarks>
    internal static class FastPathStrategyFactory
    {
        /// <summary>
        /// Creates the strategy instance for this routing map. The strategy independently
        /// builds whatever data it needs from <paramref name="orderedRanges"/>; the caller's
        /// list is not retained.
        /// </summary>
        /// <param name="variant">Requested variant. If the map does not support the variant's
        /// preconditions (e.g. variant requires V2 hash but boundaries are V1), this method
        /// returns a <see cref="StringStrategy"/> instead.</param>
        /// <param name="orderedRanges">Sorted list of partition key ranges, complete cover.</param>
        /// <param name="hasNumericFastPath">Whether the boundaries are all 32-char hex (V2 hash).</param>
        internal static IRoutingFastPathStrategy Create(
            FastPathVariant variant,
            IReadOnlyList<PartitionKeyRange> orderedRanges,
            bool hasNumericFastPath)
        {
            if (!hasNumericFastPath)
            {
                // Numeric strategies require 32-char hex boundaries. StringSoa works on any
                // topology so it's allowed through; everything else falls back to StringStrategy.
                if (variant == FastPathVariant.StringSoa)
                {
                    return new StringSoaStrategy(orderedRanges);
                }

                return new StringStrategy(orderedRanges);
            }

            switch (variant)
            {
                case FastPathVariant.String:
                    return new StringStrategy(orderedRanges);
                case FastPathVariant.UInt128:
                    return new UInt128Strategy(orderedRanges);
                case FastPathVariant.BytespanSeq:
                    return new BytespanSeqStrategy(orderedRanges);
                case FastPathVariant.BytespanHand:
                    return new BytespanHandStrategy(orderedRanges);
                case FastPathVariant.Radix1:
                    return new Radix1Strategy(orderedRanges);
                case FastPathVariant.Radix2:
                    return new Radix2Strategy(orderedRanges);
                case FastPathVariant.Soa:
                    return new SoaStrategy(orderedRanges);
                case FastPathVariant.StringSoa:
                    return new StringSoaStrategy(orderedRanges);
                default:
                    return new UInt128Strategy(orderedRanges);
            }
        }
    }
}
