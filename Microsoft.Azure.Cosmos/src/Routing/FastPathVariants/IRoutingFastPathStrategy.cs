//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants
{
    using System;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;
    using UInt128 = Microsoft.Azure.Cosmos.UInt128;

    /// <summary>
    /// Contract for a routing-map fast-path lookup strategy.
    /// </summary>
    /// <remarks>
    /// One implementation per <see cref="FastPathVariant"/>, plus the <c>StringStrategy</c>
    /// baseline that implements the same contract using the original ordinal-string binary
    /// search. A <see cref="CollectionRoutingMap"/> instance holds exactly one strategy,
    /// chosen at construction by <see cref="FastPathStrategyFactory"/> from
    /// <see cref="FastPathVariant.ActiveVariant"/>.
    /// <para>
    /// Strategies do NOT share state with the owning <see cref="CollectionRoutingMap"/>:
    /// each strategy independently materializes its own snapshot of whatever range data
    /// it needs from the raw input passed into its constructor. This makes per-variant
    /// memory footprint a function of the strategy alone and lets BDN's
    /// <c>[MemoryDiagnoser]</c> attribute report it accurately.
    /// </para>
    /// <para>
    /// The byte / <see cref="Documents.UInt128"/> overloads are valid only for strategies
    /// constructed against a V2-hash routing map. <see cref="Strategies.StringStrategy"/>
    /// throws <see cref="NotSupportedException"/> for those overloads; the map screens
    /// the precondition before calling.
    /// </para>
    /// </remarks>
    internal interface IRoutingFastPathStrategy
    {
        /// <summary>
        /// Resolves an effective-partition-key string to its containing range.
        /// Sentinel values (<see cref="PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey"/>
        /// and <see cref="PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey"/>) are
        /// handled inside the strategy.
        /// </summary>
        PartitionKeyRange Resolve(string effectivePartitionKey);

        /// <summary>
        /// Resolves a 16-byte big-endian effective-partition-key (V2 hash form). Numeric-only.
        /// </summary>
        PartitionKeyRange Resolve(in EffectivePartitionKey effectivePartitionKey);

        /// <summary>
        /// Resolves a pre-parsed UInt128 effective-partition-key (V2 hash form). Numeric-only.
        /// </summary>
        PartitionKeyRange Resolve(UInt128 effectivePartitionKey);
    }
}
