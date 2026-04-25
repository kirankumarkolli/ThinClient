//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing.FastPathVariants.Internal
{
    /// <summary>
    /// Builds radix-bucket start-index arrays used by Radix1 / Radix2 fast-path strategies.
    /// </summary>
    internal static class RadixBucketStarts
    {
        /// <summary>
        /// Builds a radix bucket-start index over a sorted string[] of 32-char hex boundaries
        /// (with index 0 possibly being the "" sentinel that maps to all zeros), dispatching by
        /// the top <paramref name="topBits"/> bits of each boundary. Returned array has length
        /// (1 &lt;&lt; topBits) + 1; entry [b] is the smallest index whose top bits &gt;= b, with
        /// [last] = boundaries.Length acting as a sentinel so callers can read [b+1] unconditionally.
        /// </summary>
        internal static int[] Build(string[] sortedMinBoundaries, int topBits)
        {
            int bucketCount = 1 << topBits;
            int[] starts = new int[bucketCount + 1];
            int hexChars = topBits / 4;

            int j = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                while (j < sortedMinBoundaries.Length
                    && HexCodec.TopBitsFromHex(sortedMinBoundaries[j], hexChars) < b)
                {
                    j++;
                }

                starts[b] = j;
            }

            starts[bucketCount] = sortedMinBoundaries.Length;
            return starts;
        }
    }
}
