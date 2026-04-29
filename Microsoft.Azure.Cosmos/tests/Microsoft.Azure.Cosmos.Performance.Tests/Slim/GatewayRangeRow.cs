//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Slim
{
    using System;
    using Microsoft.Azure.Cosmos.Routing.FastPathVariants.Internal;
    using UInt128 = Microsoft.Azure.Cosmos.UInt128;

    /// <summary>
    /// Pre-parsed numeric form of a single gateway PKRange row, ready for direct
    /// insertion into <see cref="SlimRoutingMap"/>'s parallel arrays. Allocation
    /// for these rows is amortized in benchmark <c>[GlobalSetup]</c> so the
    /// per-op build measurement reflects only steady-state cache fill cost.
    /// </summary>
    internal readonly struct GatewayRangeRow
    {
        public readonly UInt128 MinNumeric;
        public readonly UInt128 MaxNumeric;
        public readonly int IdAsInt;
        public readonly byte StatusBits;

        public GatewayRangeRow(UInt128 minNumeric, UInt128 maxNumeric, int idAsInt, byte statusBits)
        {
            this.MinNumeric = minNumeric;
            this.MaxNumeric = maxNumeric;
            this.IdAsInt = idAsInt;
            this.StatusBits = statusBits;
        }

        /// <summary>
        /// Parses a (id, minHex, maxHex) triple. Honors the SDK sentinels:
        /// empty min → <see cref="UInt128.MinValue"/>, "FF" max → <see cref="UInt128.MaxValue"/>.
        /// </summary>
        public static GatewayRangeRow From(string id, string minHex, string maxHex)
        {
            UInt128 min = ParseBoundary(minHex, isMax: false);
            UInt128 max = ParseBoundary(maxHex, isMax: true);
            int idInt = int.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
            return new GatewayRangeRow(min, max, idInt, statusBits: 0);
        }

        private static UInt128 ParseBoundary(string hex, bool isMax)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return UInt128.MinValue;
            }

            if (isMax && string.Equals(hex, "FF", StringComparison.Ordinal))
            {
                return UInt128.MaxValue;
            }

            if (!HexCodec.TryParseHex32ToUInt128(hex, out UInt128 v))
            {
                throw new ArgumentException($"Boundary hex '{hex}' is not a 32-char hex string.");
            }

            return v;
        }
    }
}
