//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Slim
{
    using System;
    using System.Buffers;
    using System.Buffers.Text;
    using System.Text.Json;
    using UInt128 = Microsoft.Azure.Cosmos.UInt128;

    /// <summary>
    /// Builds <see cref="SlimRoutingMap"/> instances from either pre-parsed
    /// <see cref="GatewayRangeRow"/> spans (apples-to-apples with today's
    /// <c>Tuple&lt;PKR, ServiceIdentity&gt;[]</c> input) or directly from a
    /// gateway <c>/pkranges</c> JSON byte buffer (proves the deserialization-
    /// seam saving by skipping the intermediate <see cref="Documents.PartitionKeyRange"/>
    /// materialization entirely).
    /// </summary>
    internal static class SlimRoutingMapBuilder
    {
        /// <summary>
        /// Build from pre-parsed numeric rows. Allocates only the destination
        /// arrays + an <c>int[]</c> permutation buffer for sort-by-min.
        /// </summary>
        public static SlimRoutingMap Build(ReadOnlySpan<GatewayRangeRow> rows)
        {
            int n = rows.Length;
            UInt128[] boundaries = new UInt128[n + 1];
            int[] ids = new int[n];
            byte[] statuses = new byte[n];

            int[] perm = new int[n];
            for (int i = 0; i < n; i++)
            {
                perm[i] = i;
            }

            QuicksortByMin(perm, rows, 0, n - 1);

            for (int i = 0; i < n; i++)
            {
                int src = perm[i];
                boundaries[i] = rows[src].MinNumeric;
                ids[i] = rows[src].IdAsInt;
                statuses[i] = rows[src].StatusBits;
            }

            boundaries[n] = rows[perm[n - 1]].MaxNumeric;

            return new SlimRoutingMap(boundaries, ids, statuses);
        }

        /// <summary>
        /// Build directly from a gateway <c>/pkranges</c> response body. Uses
        /// <see cref="Utf8JsonReader"/> over the byte buffer; never materializes
        /// intermediate <see cref="Documents.PartitionKeyRange"/> objects, never
        /// allocates <see cref="string"/> for Min/Max/Id beyond the destination arrays.
        /// </summary>
        public static SlimRoutingMap BuildFromJson(ReadOnlySpan<byte> jsonUtf8, int expectedCount)
        {
            // Final destination arrays — only ones that survive the build.
            UInt128[] boundaries = new UInt128[expectedCount + 1];
            int[] ids = new int[expectedCount];
            byte[] statuses = new byte[expectedCount];
            UInt128 globalMax = UInt128.MinValue;

            UInt128 curMin = UInt128.MinValue;
            UInt128 curMax = UInt128.MinValue;
            int curId = -1;
            int rowIndex = 0;

            bool inArray = false;
            int objectDepth = 0;

            Utf8JsonReader reader = new Utf8JsonReader(jsonUtf8, isFinalBlock: true, state: default);

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        if (inArray && objectDepth == 1)
                        {
                            if (reader.ValueTextEquals("id"))
                            {
                                reader.Read();
                                curId = ParseIntFromJsonString(ref reader);
                            }
                            else if (reader.ValueTextEquals("minInclusive"))
                            {
                                reader.Read();
                                curMin = ParseBoundaryFromJsonString(ref reader, isMax: false);
                            }
                            else if (reader.ValueTextEquals("maxExclusive"))
                            {
                                reader.Read();
                                curMax = ParseBoundaryFromJsonString(ref reader, isMax: true);
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                        break;

                    case JsonTokenType.StartArray:
                        if (!inArray)
                        {
                            inArray = true;
                        }
                        break;

                    case JsonTokenType.EndArray:
                        inArray = false;
                        break;

                    case JsonTokenType.StartObject:
                        if (inArray)
                        {
                            objectDepth++;
                            curMin = UInt128.MinValue;
                            curMax = UInt128.MinValue;
                            curId = -1;
                        }
                        break;

                    case JsonTokenType.EndObject:
                        if (inArray && objectDepth == 1)
                        {
                            // Stream rows directly into final arrays in source order.
                            boundaries[rowIndex] = curMin;
                            ids[rowIndex] = curId;
                            if (curMax.CompareTo(globalMax) > 0)
                            {
                                globalMax = curMax;
                            }
                            rowIndex++;
                            objectDepth--;
                        }
                        break;
                }
            }

            // In-place co-sort of (boundaries[0..N), ids[0..N)) using Span sort.
            // No transient arrays — both spans alias the final destination arrays.
            boundaries.AsSpan(0, expectedCount).Sort(ids.AsSpan());
            boundaries[expectedCount] = globalMax;

            return new SlimRoutingMap(boundaries, ids, statuses);
        }

        private static int ParseIntFromJsonString(ref Utf8JsonReader reader)
        {
            ReadOnlySpan<byte> raw = reader.HasValueSequence
                ? reader.ValueSequence.ToArray()
                : reader.ValueSpan;
            if (!Utf8Parser.TryParse(raw, out int v, out _))
            {
                throw new InvalidOperationException("Bad id value.");
            }
            return v;
        }

        private static UInt128 ParseBoundaryFromJsonString(ref Utf8JsonReader reader, bool isMax)
        {
            ReadOnlySpan<byte> raw = reader.HasValueSequence
                ? reader.ValueSequence.ToArray()
                : reader.ValueSpan;
            if (raw.Length == 0)
            {
                return UInt128.MinValue;
            }

            if (isMax && raw.Length == 2 && raw[0] == (byte)'F' && raw[1] == (byte)'F')
            {
                return UInt128.MaxValue;
            }

            if (raw.Length != 32)
            {
                throw new InvalidOperationException($"Boundary length {raw.Length} unsupported.");
            }

            return ParseHex32Utf8(raw);
        }

        private static UInt128 ParseHex32Utf8(ReadOnlySpan<byte> hex)
        {
            ulong high = 0;
            for (int i = 0; i < 16; i++)
            {
                int n = HexNibble(hex[i]);
                high = (high << 4) | (uint)n;
            }

            ulong low = 0;
            for (int i = 16; i < 32; i++)
            {
                int n = HexNibble(hex[i]);
                low = (low << 4) | (uint)n;
            }

            return UInt128.Create(low, high);
        }

        private static int HexNibble(byte b)
        {
            if ((uint)(b - (byte)'0') <= 9)
            {
                return b - (byte)'0';
            }
            if ((uint)(b - (byte)'a') <= 5)
            {
                return b - (byte)'a' + 10;
            }
            if ((uint)(b - (byte)'A') <= 5)
            {
                return b - (byte)'A' + 10;
            }
            throw new FormatException($"Bad hex byte 0x{b:X2}.");
        }

        private static void QuicksortByMin(int[] perm, ReadOnlySpan<GatewayRangeRow> rows, int lo, int hi)
        {
            while (lo < hi)
            {
                int pivot = perm[lo + ((hi - lo) >> 1)];
                UInt128 pivotKey = rows[pivot].MinNumeric;
                int i = lo - 1;
                int j = hi + 1;
                while (true)
                {
                    do { i++; } while (rows[perm[i]].MinNumeric.CompareTo(pivotKey) < 0);
                    do { j--; } while (rows[perm[j]].MinNumeric.CompareTo(pivotKey) > 0);
                    if (i >= j)
                    {
                        break;
                    }
                    (perm[i], perm[j]) = (perm[j], perm[i]);
                }

                if (j - lo < hi - j - 1)
                {
                    QuicksortByMin(perm, rows, lo, j);
                    lo = j + 1;
                }
                else
                {
                    QuicksortByMin(perm, rows, j + 1, hi);
                    hi = j;
                }
            }
        }
    }
}
