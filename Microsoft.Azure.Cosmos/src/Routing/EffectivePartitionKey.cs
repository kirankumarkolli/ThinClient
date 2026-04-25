//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing
{
    using System;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Opaque value-type representation of an effective partition key (EPK).
    /// Carried end-to-end on the routing fast path so that callers compute the EPK once
    /// and consume it without round-tripping through the 32-char hex string form.
    ///
    /// The internal storage is intentionally hidden behind this type so that the
    /// underlying representation (currently <see cref="UInt128"/>; potential future
    /// alternatives include a 16-byte big-endian span, two <see cref="ulong"/> halves,
    /// or <see cref="System.Runtime.Intrinsics.Vector128{Byte}"/>) can change without
    /// touching producer or consumer call sites.
    ///
    /// Currently only V2 hash partitioning is supported by the bypass path; for V1 hash,
    /// range, and hierarchical partition kinds, callers must continue to use the string
    /// EPK overloads of <see cref="CollectionRoutingMap.GetRangeByEffectivePartitionKey(string)"/>.
    /// </summary>
    internal readonly struct EffectivePartitionKey : IEquatable<EffectivePartitionKey>
    {
        // Today: UInt128. The field is private; callers must use the From*/To* helpers
        // so we can change the representation without breaking the API contract.
        private readonly UInt128 numericValue;

        private EffectivePartitionKey(UInt128 numericValue)
        {
            this.numericValue = numericValue;
        }

        /// <summary>Constructs an EPK from a pre-computed UInt128 (used by the V2-hash producer).</summary>
        internal static EffectivePartitionKey FromUInt128(UInt128 value)
        {
            return new EffectivePartitionKey(value);
        }

        /// <summary>
        /// Internal accessor for routing-map binary search. Marked internal so consumers
        /// in <see cref="CollectionRoutingMap"/> can unwrap; external callers must not depend on this.
        /// </summary>
        internal UInt128 ToUInt128()
        {
            return this.numericValue;
        }

        public bool Equals(EffectivePartitionKey other)
        {
            return this.numericValue == other.numericValue;
        }

        public override bool Equals(object obj)
        {
            return obj is EffectivePartitionKey other && this.Equals(other);
        }

        public override int GetHashCode()
        {
            return this.numericValue.GetHashCode();
        }

        public static bool operator ==(EffectivePartitionKey left, EffectivePartitionKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(EffectivePartitionKey left, EffectivePartitionKey right)
        {
            return !left.Equals(right);
        }
    }
}
