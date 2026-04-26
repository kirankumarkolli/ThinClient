//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants
{
    using System;

    /// <summary>
    /// Selector for the routing-map point-lookup strategy. Controlled by env var
    /// <c>COSMOS_PKRANGE_VARIANT</c>; default is <see cref="UInt128"/>.
    /// </summary>
    /// <remarks>
    /// Each variant maps to exactly one <see cref="Strategies"/> implementation chosen by
    /// <see cref="FastPathStrategyFactory.Create"/>. <see cref="String"/> is the production
    /// baseline that has shipped for years; the others are alternative implementations
    /// kept alive for benchmarking.
    /// </remarks>
    internal enum FastPathVariant
    {
        /// <summary>Production baseline: ordinal <see cref="System.Array.BinarySearch{T}(T[], T, System.Collections.Generic.IComparer{T})"/> over <c>string</c> boundaries. Always valid.</summary>
        String,

        /// <summary>UInt128 binary search over numeric boundaries. Default.</summary>
        UInt128,

        /// <summary>Packed byte[16N] with <see cref="System.ReadOnlySpan{T}.SequenceCompareTo(System.ReadOnlySpan{T})"/> per probe.</summary>
        BytespanSeq,

        /// <summary>Packed byte[16N] with hand-rolled big-endian ulong compares.</summary>
        BytespanHand,

        /// <summary>UInt128 + top-byte (256-bucket) radix dispatch then narrow binary search.</summary>
        Radix1,

        /// <summary>UInt128 + top-16-bit (65536-bucket) radix dispatch then narrow binary search.</summary>
        Radix2,

        /// <summary>Bytespan + payload SoA + branchless binary search.</summary>
        Soa,

        /// <summary>String binary search + payload SoA + branchless string binary search. Works on any topology (V1/V2/hierarchical).</summary>
        StringSoa,

        /// <summary>UInt128 binary search using the .NET 7+ intrinsic <see cref="System.UInt128"/>. V2-hash only. Available on net8.0+; falls back to <see cref="UInt128"/> on netstandard2.0.</summary>
        SystemUInt128,
    }

    /// <summary>
    /// Parses and exposes the <see cref="FastPathVariant"/> selected by environment variable.
    /// </summary>
    internal static class FastPathVariantSelector
    {
        internal const string EnvironmentVariableName = "COSMOS_PKRANGE_VARIANT";

        private static FastPathVariant activeVariant = Parse(Environment.GetEnvironmentVariable(EnvironmentVariableName));

        /// <summary>
        /// Currently-selected variant. Test code (e.g. <c>CollectionRoutingMapTest</c>) sets this
        /// directly to validate equivalence across variants without spawning new processes.
        /// </summary>
        internal static FastPathVariant ActiveVariant
        {
            get => activeVariant;
            set => activeVariant = value;
        }

        internal static FastPathVariant Parse(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return FastPathVariant.UInt128;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "string": return FastPathVariant.String;
                case "uint128": return FastPathVariant.UInt128;
                case "bytespan-seq": return FastPathVariant.BytespanSeq;
                case "bytespan-hand": return FastPathVariant.BytespanHand;
                case "radix1": return FastPathVariant.Radix1;
                case "radix2": return FastPathVariant.Radix2;
                case "soa": return FastPathVariant.Soa;
                case "string-soa": return FastPathVariant.StringSoa;
                case "system-uint128": return FastPathVariant.SystemUInt128;
                default: return FastPathVariant.UInt128;
            }
        }
    }
}
