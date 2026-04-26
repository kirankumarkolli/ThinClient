//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants.Internal
{
    using System;
    using System.Runtime.CompilerServices;
    using Microsoft.Azure.Documents;
    using UInt128 = Microsoft.Azure.Cosmos.UInt128;

    /// <summary>
    /// Zero-allocation helpers for parsing fixed-width 32-character hexadecimal
    /// effective-partition-key strings into compact numeric / byte representations.
    /// All routines are pure and stateless; they are shared by every fast-path strategy
    /// that needs to interpret an EPK string.
    /// </summary>
    internal static class HexCodec
    {
        /// <summary>
        /// Parses a 32-char continuous hex string into a UInt128.
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

            if (TryParseHexUInt64(hex, 0, out ulong high)
                && TryParseHexUInt64(hex, 16, out ulong low))
            {
                result = UInt128.Create(low, high);
                return true;
            }

            result = default;
            return false;
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
                int hi = HexCharToNibble(hex[2 * i]);
                int lo = HexCharToNibble(hex[(2 * i) + 1]);
                if ((hi | lo) < 0)
                {
                    return false;
                }

                destination[i] = (byte)((hi << 4) | lo);
            }

            return true;
        }

        /// <summary>
        /// Writes a 32-char hex string into a 16-byte slice of <paramref name="dest"/> at <paramref name="offset"/>.
        /// Caller must guarantee well-formed hex input (used during ctor build paths over trusted boundaries).
        /// </summary>
        internal static void WriteHex32ToBytes(string hex, byte[] dest, int offset)
        {
            for (int i = 0; i < 16; i++)
            {
                int hi = HexCharToNibble(hex[2 * i]);
                int lo = HexCharToNibble(hex[(2 * i) + 1]);
                dest[offset + i] = (byte)((hi << 4) | lo);
            }
        }

        /// <summary>
        /// Returns the integer value of the top <paramref name="hexChars"/> hex characters of <paramref name="hex"/>.
        /// The empty (first-range) sentinel returns 0 (minimum boundary).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int TopBitsFromHex(string hex, int hexChars)
        {
            if (hex.Length == 0)
            {
                return 0;
            }

            int v = 0;
            for (int i = 0; i < hexChars; i++)
            {
                v = (v << 4) | HexCharToNibble(hex[i]);
            }

            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryParseHexUInt64(string hex, int offset, out ulong value)
        {
            value = 0;
            for (int i = 0; i < 16; i++)
            {
                int nibble = HexCharToNibble(hex[offset + i]);
                if (nibble < 0)
                {
                    return false;
                }

                value = (value << 4) | (uint)nibble;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int HexCharToNibble(char c)
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
