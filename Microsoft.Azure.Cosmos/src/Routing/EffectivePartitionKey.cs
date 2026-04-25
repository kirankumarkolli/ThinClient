//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing
{
    using System;

    /// <summary>
    /// Opaque <c>ref struct</c> view of an effective partition key (EPK) as a
    /// big-endian byte sequence. Carried end-to-end on the routing fast path so callers
    /// compute the EPK once into a stack buffer and consume it without allocating a
    /// 32-char hex string or wrapping into a heap object.
    ///
    /// V2 hash EPKs are always 16 bytes big-endian (top 2 bits of byte[0] cleared).
    /// Future variable-length cases (V1 hash, MultiHash, hierarchical PK) can use
    /// longer spans without changing this contract.
    ///
    /// Because this is a <c>ref struct</c> it cannot cross <c>await</c> boundaries,
    /// be a field on a class, or be stored in a collection. Callers that need to
    /// persist an EPK across async work should keep the underlying buffer (e.g., a
    /// rented <c>byte[]</c>) and reconstruct the view on demand.
    /// </summary>
    internal readonly ref struct EffectivePartitionKey
    {
        private readonly ReadOnlySpan<byte> bytes;

        private EffectivePartitionKey(ReadOnlySpan<byte> bytes)
        {
            this.bytes = bytes;
        }

        /// <summary>
        /// Constructs an EPK view over a caller-owned buffer in big-endian form.
        /// For V2 hash partitioning, the buffer must be exactly 16 bytes with the
        /// top two bits of byte[0] cleared.
        /// </summary>
        internal static EffectivePartitionKey FromBigEndianBytes(ReadOnlySpan<byte> bigEndianBytes)
        {
            return new EffectivePartitionKey(bigEndianBytes);
        }

        /// <summary>
        /// Internal accessor for the routing-map binary search. Marked internal so consumers
        /// in <see cref="CollectionRoutingMap"/> can read the underlying span; external
        /// callers must not depend on this shape.
        /// </summary>
        internal ReadOnlySpan<byte> AsSpan()
        {
            return this.bytes;
        }

        internal int Length => this.bytes.Length;
    }
}
