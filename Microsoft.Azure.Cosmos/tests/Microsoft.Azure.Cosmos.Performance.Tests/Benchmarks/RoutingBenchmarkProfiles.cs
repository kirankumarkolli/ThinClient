//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Cosmos.Routing.FastPathVariants;

    /// <summary>
    /// Single source of truth for the (variant, bypass) tuples that the routing benchmarks
    /// sweep over. Apply via <see cref="Apply(string)"/> from a <c>[GlobalSetup]</c>; the
    /// benchmark <c>[Params]</c> axis should use <see cref="AllProfiles"/> verbatim.
    ///
    /// <para>Replaces the previous COSMOS_PKRANGE_VARIANT / COSMOS_PKRANGE_BYPASS_STRING_EPK
    /// env-var coupling: the benchmark drives both <see cref="FastPathVariantSelector.ActiveVariant"/>
    /// and <see cref="AddressResolver.UseStringEpkBypass"/> directly.</para>
    /// </summary>
    internal static class RoutingBenchmarkProfiles
    {
        /// <summary>BDN <c>[Params]</c> axis — keep these tokens stable; the bench sweep
        /// script uses them as both label and command-line param value.</summary>
        public static readonly string[] AllProfiles =
        {
            "string",
            "uint128",
            "bytespan-seq",
            "bytespan-hand",
            "bytespan-hand-bypass",
            "radix1",
            "radix2",
            "soa",
            "string-soa",
        };

        /// <summary>Resolves a profile token to (variant, bypass) and applies it
        /// to the SDK's static configuration. Idempotent.</summary>
        public static (FastPathVariant Variant, bool Bypass) Apply(string profile)
        {
            (FastPathVariant variant, bool bypass) = profile switch
            {
                "string"               => (FastPathVariant.String,        false),
                "uint128"              => (FastPathVariant.UInt128,       false),
                "bytespan-seq"         => (FastPathVariant.BytespanSeq,   false),
                "bytespan-hand"        => (FastPathVariant.BytespanHand,  false),
                "bytespan-hand-bypass" => (FastPathVariant.BytespanHand,  true),
                "radix1"               => (FastPathVariant.Radix1,        true),
                "radix2"               => (FastPathVariant.Radix2,        true),
                "soa"                  => (FastPathVariant.Soa,           true),
                "string-soa"           => (FastPathVariant.StringSoa,     false),
                _ => throw new ArgumentException(
                        $"Unknown routing benchmark profile '{profile}'. Valid values: " +
                        string.Join(", ", AllProfiles), nameof(profile)),
            };

            FastPathVariantSelector.ActiveVariant = variant;
            AddressResolver.UseStringEpkBypass    = bypass;
            return (variant, bypass);
        }
    }
}
