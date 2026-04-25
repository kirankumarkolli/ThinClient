//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class CollectionRoutingMapTest
    {
        [TestMethod]
        public void TestCollectionRoutingMap()
        {
            ServiceIdentity serviceIdentity0 = new ServiceIdentity("1", new Uri("http://1"), false);
            ServiceIdentity serviceIdentity1 = new ServiceIdentity("2", new Uri("http://2"), false);
            ServiceIdentity serviceIdentity2 = new ServiceIdentity("3", new Uri("http://3"), false);
            ServiceIdentity serviceIdentity3 = new ServiceIdentity("4", new Uri("http://4"), false);
            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                new[]
                    {
                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "2",
                            MinInclusive = "0000000050",
                            MaxExclusive = "0000000070"},
                            serviceIdentity2),

                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "0",
                            MinInclusive = "",
                            MaxExclusive = "0000000030"},
                            serviceIdentity0),

                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "1",
                            MinInclusive = "0000000030",
                            MaxExclusive = "0000000050"},
                            serviceIdentity1),

                         Tuple.Create(
                            new PartitionKeyRange{
                            Id = "3",
                            MinInclusive = "0000000070",
                            MaxExclusive = "FF"},
                            serviceIdentity3),


                    }, string.Empty, false);

            Assert.AreEqual("0", routingMap.OrderedPartitionKeyRanges[0].Id);
            Assert.AreEqual("1", routingMap.OrderedPartitionKeyRanges[1].Id);
            Assert.AreEqual("2", routingMap.OrderedPartitionKeyRanges[2].Id);
            Assert.AreEqual("3", routingMap.OrderedPartitionKeyRanges[3].Id);

            Assert.AreEqual(serviceIdentity0, routingMap.TryGetInfoByPartitionKeyRangeId("0"));
            Assert.AreEqual(serviceIdentity1, routingMap.TryGetInfoByPartitionKeyRangeId("1"));
            Assert.AreEqual(serviceIdentity2, routingMap.TryGetInfoByPartitionKeyRangeId("2"));
            Assert.AreEqual(serviceIdentity3, routingMap.TryGetInfoByPartitionKeyRangeId("3"));

            Assert.AreEqual("0", routingMap.GetRangeByEffectivePartitionKey("").Id);
            Assert.AreEqual("0", routingMap.GetRangeByEffectivePartitionKey("0000000000").Id);
            Assert.AreEqual("1", routingMap.GetRangeByEffectivePartitionKey("0000000030").Id);
            Assert.AreEqual("1", routingMap.GetRangeByEffectivePartitionKey("0000000031").Id);
            Assert.AreEqual("3", routingMap.GetRangeByEffectivePartitionKey("0000000071").Id);

            Assert.AreEqual("0", routingMap.TryGetRangeByPartitionKeyRangeId("0").Id);
            Assert.AreEqual("1", routingMap.TryGetRangeByPartitionKeyRangeId("1").Id);

            Assert.AreEqual(4, routingMap.GetOverlappingRanges(new[] { new Range<string>(PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey, PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey, true, false) }).Count);
            Assert.AreEqual(0, routingMap.GetOverlappingRanges(new[] { new Range<string>(PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey, PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey, false, false) }).Count);
            IReadOnlyList<PartitionKeyRange> partitionKeyRanges =
                routingMap.GetOverlappingRanges(new[]
                                                    {
                                                        new Range<string>(
                                                            "0000000040",
                                                            "0000000040",
                                                            true,
                                                            true)
                                                    });

            Assert.AreEqual(1, partitionKeyRanges.Count);
            Assert.AreEqual("1", partitionKeyRanges.ElementAt(0).Id);

            IReadOnlyList<PartitionKeyRange> partitionKeyRanges1 =
               routingMap.GetOverlappingRanges(new[]
                                                    {
                                                        new Range<string>(
                                                            "0000000040",
                                                            "0000000045",
                                                            true,
                                                            true),
                                                        new Range<string>(
                                                            "0000000045",
                                                            "0000000046",
                                                            true,
                                                            true),
                                                       new Range<string>(
                                                            "0000000046",
                                                            "0000000050",
                                                            true,
                                                            true)
                                                    });

            Assert.AreEqual(2, partitionKeyRanges1.Count);
            Assert.AreEqual("1", partitionKeyRanges1.ElementAt(0).Id);
            Assert.AreEqual("2", partitionKeyRanges1.ElementAt(1).Id);
        }

        /// <summary>
        /// Validates that CollectionRoutingMap correctly identifies overlapping partition key ranges
        /// when using length-aware range comparators.
        /// This test ensures that EPK advanced comparison logic are applied as expected,
        /// and that the routing map's behavior is consistent regardless if the input EPK is fully or partially specified.
        /// The test covers scenarios where input EPKs are partial or fall on range boundaries,
        /// verifying that the correct partition key ranges are returned when using the new LengthAware comparators.
        /// </summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TestCollectionRoutingMapWithLengthAwareRangeComparators(bool isRoutingMapFullySpecified)
        {
            try
            {
                // Arrange: Set useLengthAwareComparer flag to "true" since the default is only true for Preview.
                CollectionRoutingMap routingMap = this.GenerateRoutingMap(isRoutingMapFullySpecified, true);

                // Test scenario 1.1: Input EPK is partial and falls on the boundary between two overlapping ranges.
                // The LengthAware comparators are able to correctly compare partial and full EPK ranges.Routing map is hybrid of fully specified and partially specified EPK ranges.
                // Input Min EPK 06AB34CFE4E482236BCACBBF50E234AB matches (significant bytes) with maxEPK of pkrangeid 1 and minEPK of pkrangeid 2.
                Range<string> inputPkRange = new Range<string>(
                "06AB34CFE4E482236BCACBBF50E234AB",
                "06AB34CFE4E482236BCACBBF50E234ABFF",
                true,
                false);

                // Expected outcome: Only partition key range with id 2 overlaps, as the LengthAware comparator correctly handles the partial EPK.
                IReadOnlyList<PartitionKeyRange> partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                Assert.AreEqual(1, partitionKeyRanges1.Count);
                Assert.AreEqual("2", partitionKeyRanges1[0].Id);

                // Test scenario 1.2: Input EPK falls on a boundary and maxEPK also matches the next range's max.
                // The LengthAware comparator should return only the correct overlapping range.
                inputPkRange = new Range<string>(
                "0BD3FBE846AF75790CE63F78B1A81631",
                "0BD3FBE846AF75790CE63F78B1A81631FF",
                true,
                false);

                partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                Assert.AreEqual(1, partitionKeyRanges1.Count);
                CollectionAssert.AreEquivalent(new[] { "11" }, partitionKeyRanges1.Select(r => r.Id).ToArray());

                inputPkRange = new Range<string>(
                "0D4DC2CD8F49C65A8E0C5306B61B43440D4DC2CD8F49C65A8E0C5306B61B4343",
                "0D4DC2CD8F49C65A8E0C5306B61B43440D4DC2CD8F49C65A8E0C5306B61B4344",
                true,
                false);

                partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                Assert.AreEqual(1, partitionKeyRanges1.Count);
                CollectionAssert.AreEquivalent(new[] { "4" }, partitionKeyRanges1.Select(r => r.Id).ToArray());

                // Test scenario 1.2 (continued): Input EPK falls in boundary and maxEPK also matches the next range's max.
                inputPkRange = new Range<string>(
                "0BD3FBE846AF75790CE63F78B1A81620",
                "0BD3FBE846AF75790CE63F78B1A81631",
                true,
                false);

                partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                Assert.AreEqual(1, partitionKeyRanges1.Count);
                CollectionAssert.AreEquivalent(new[] { "3" }, partitionKeyRanges1.Select(r => r.Id).ToArray());

                // Test scenario 1.3: Input EPK is partial and spans two overlapping ranges.
                /// Input Min EPK 0DCEB8CE51C6BFE84F4BD9409F69B9BB falls in both pkrangeid 4 and pkrangeid 5.
                inputPkRange = new Range<string>(
                "0DCEB8CE51C6BFE84F4BD9409F69B9BB",
                "0DCEB8CE51C6BFE84F4BD9409F69B9BBFF",
                true,
                false);

                partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                Assert.AreEqual(2, partitionKeyRanges1.Count);
                CollectionAssert.AreEquivalent(new[] { "24", "5" }, partitionKeyRanges1.Select(r => r.Id).ToArray());


                ///Test scenario 1.4: Input EPK is partial and falls in a single range in the middle. Routing map is hybrid of fully specified and partially specified ranges.
                inputPkRange = new Range<string>(
                "02559A67F2724111B5E565DFA8711A00",
                "02559A67F2724111B5E565DFA8711A00",
                true,
                true);

                partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                Assert.AreEqual(1, partitionKeyRanges1.Count);
                Assert.AreEqual("0", partitionKeyRanges1[0].Id);


                ///Test scenario 1.5: Input EPK is partial and falls in a single range in the middle. Routing map targeted range has partial EPK values only.
                inputPkRange = new Range<string>(
                "0D4DC2CD8F49C65A8E0C5306B61B4345",
                "0D4DC2CD8F49C65A8E0C5306B61B4345",
                true,
                true);

                partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                Assert.AreEqual(1, partitionKeyRanges1.Count);
                Assert.AreEqual("4", partitionKeyRanges1[0].Id);


                // The following part of the test case verifies the routing map values i.e.backend ranges when they are not fully specified.
                if (!isRoutingMapFullySpecified)
                {
                    // Test scenario 1.6: Input EPK is fully specified and backend range is partially specified.
                    // The LengthAware comparator correctly matches the fully specified input to the partially specified backend range.
                    inputPkRange = new Range<string>(
                    "0D4DC2CD8F49C65A8E0C5306B61B434300000000000000000000000000000000",
                    "0D4EC2CD8F49C65A8E0C5306B61B434300000000000000000000000000000000",
                    true,
                    false);

                    // LengthAware comparator yields only the correct range.
                    partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                    Assert.AreEqual(1, partitionKeyRanges1.Count);
                    CollectionAssert.AreEquivalent(new[] { "4" }, partitionKeyRanges1.Select(r => r.Id).ToArray());
                }
            }
            finally
            {
                // Clean up: Remove the environment variable after the test.
                Environment.SetEnvironmentVariable(ConfigurationManager.UseLengthAwareRangeComparator, null);
            }
        }

        // Test GetOverlappingRanges behavior when the UseLengthAwareRangeComparator environment flag is set to false,
        // which forces the use of legacy Min/Max comparators.
        [TestMethod]
        public void TestLegacyComparatorsUsedWhenLengthAwareComparatorFlagIsFalse()
        {
            try
            {

                // Arrange: Set useLengthAwareComparer to false to force legacy comparator usage.
                CollectionRoutingMap routingMap = this.GenerateRoutingMap(false, false);


                // Test scenario: Input EPK is partial and falls on the boundary between two overlapping ranges.
                // With the environment flag set, the routing map uses legacy Min/Max comparators, which do not distinguish
                // between partial and full EPKs. As a result, both partition key ranges with ids 1 and 2 are considered overlapping.
                // Input Min EPK 06AB34CFE4E482236BCACBBF50E234AB matches (significant bytes) with maxEPK of pkrangeid 1 and minEPK of pkrangeid 2.
                Range<string> inputPkRange = new Range<string>(
                "06AB34CFE4E482236BCACBBF50E234AB",
                "06AB34CFE4E482236BCACBBF50E234ABFF",
                true,
                false);
                IReadOnlyList<PartitionKeyRange> partitionKeyRanges1 = routingMap.GetOverlappingRanges(inputPkRange);
                Assert.AreEqual(2, partitionKeyRanges1.Count);
                CollectionAssert.AreEquivalent(new[] { "1", "2" }, partitionKeyRanges1.Select(r => r.Id).ToArray());
            }
            finally
            {
                Environment.SetEnvironmentVariable(ConfigurationManager.UseLengthAwareRangeComparator, null);
            }
        }

        private CollectionRoutingMap GenerateRoutingMap(bool isFullySpecified, bool useLengthAwareComparer)
        {
            IEnumerable<Tuple<PartitionKeyRange, ServiceIdentity>> partitionKeyRangeTuples = new[]
                {
                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "0",
                            MinInclusive = "",
                            MaxExclusive = "03559A67F2724111B5E565DFA8711A00"
                        },
                        (ServiceIdentity)null),

                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "1",
                            MinInclusive = "03559A67F2724111B5E565DFA8711A00",
                            MaxExclusive = "06AB34CFE4E482236BCACBBF50E234AB00000000000000000000000000000000"
                        },
                        (ServiceIdentity)null),

                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "2",
                            MinInclusive = "06AB34CFE4E482236BCACBBF50E234AB00000000000000000000000000000000",
                            MaxExclusive = "0BD3FBE846AF75790CE63F78B1A81620"
                        },
                        (ServiceIdentity)null),

                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "3",
                            MinInclusive = "0BD3FBE846AF75790CE63F78B1A81620",
                            MaxExclusive = "0BD3FBE846AF75790CE63F78B1A8163100000000000000000000000000000000"
                        },
                        (ServiceIdentity)null),
                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "11",
                            MinInclusive = "0BD3FBE846AF75790CE63F78B1A8163100000000000000000000000000000000",
                            MaxExclusive = "0BD3FBE846AF75790CE63F78B1A81631FF"
                        },
                        (ServiceIdentity)null),
                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "12",
                            MinInclusive = "0BD3FBE846AF75790CE63F78B1A81631FF",
                            MaxExclusive = "0D4DC2CD8F49C65A8E0C5306B61B4343"
                        },
                        (ServiceIdentity)null),

                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "4",
                            MinInclusive = "0D4DC2CD8F49C65A8E0C5306B61B4343",
                            MaxExclusive = "0D4EC2CD8F49C65A8E0C5306B61B4343"
                        },
                        (ServiceIdentity)null),

                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "44",
                            MinInclusive = "0D4EC2CD8F49C65A8E0C5306B61B4343",
                            MaxExclusive = "0D5DC2CD8F49C65A8E0C5306B61B4343"
                        },
                        (ServiceIdentity)null),

                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "24",
                            MinInclusive = "0D5DC2CD8F49C65A8E0C5306B61B4343",
                            MaxExclusive = "0DCEB8CE51C6BFE84F4BD9409F69B9BB2164DEBD78C50C850E0C1E3E3F0579ED"
                        },
                        (ServiceIdentity)null),

                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "5",
                            MinInclusive = "0DCEB8CE51C6BFE84F4BD9409F69B9BB2164DEBD78C50C850E0C1E3E3F0579ED",
                            MaxExclusive = "1080F600C27CF98DC13F8639E94E7676"
                        },
                        (ServiceIdentity)null),
                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "9",
                            MinInclusive = "1080F600C27CF98DC13F8639E94E7676",
                            MaxExclusive = "FF"
                        },
                        (ServiceIdentity)null),
                };

            if (isFullySpecified)
            {
                partitionKeyRangeTuples = partitionKeyRangeTuples
                    .Select(tuple =>
                    {
                        PartitionKeyRange range = tuple.Item1;
                        // Pad right to 64 bytes (128 hex chars) for MinInclusive and MaxExclusive if not empty
                        string PadTo64(string value)
                        {
                            if (string.IsNullOrEmpty(value) || value == "FF")
                                return value;
                            return value.PadRight(64, '0');
                        }
                        return Tuple.Create(
                            new PartitionKeyRange
                            {
                                Id = range.Id,
                                MinInclusive = PadTo64(range.MinInclusive),
                                MaxExclusive = PadTo64(range.MaxExclusive)
                            },
                            tuple.Item2
                        );
                    })
                    .ToList();
            }

            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                partitionKeyRangeTuples,
                string.Empty,
                useLengthAwareComparer);

            return routingMap;
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public void TestInvalidRoutingMap()
        {
            CollectionRoutingMap.TryCreateCompleteRoutingMap(
                new[]
                    {
                        Tuple.Create(new PartitionKeyRange {Id = "1", MinInclusive = "0000000020", MaxExclusive = "0000000030"}, (ServiceIdentity)null),
                        Tuple.Create(new PartitionKeyRange { Id = "2", MinInclusive = "0000000025", MaxExclusive = "0000000035"}, (ServiceIdentity)null),
                    },
                string.Empty, false);
        }

        [TestMethod]
        public void TestIncompleteRoutingMap()
        {
            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                new[]
                    {
                        Tuple.Create(new PartitionKeyRange{ Id = "2", MinInclusive = "", MaxExclusive = "0000000030"}, (ServiceIdentity)null),
                        Tuple.Create(new PartitionKeyRange{ Id = "3", MinInclusive = "0000000031", MaxExclusive = "FF"}, (ServiceIdentity)null),
                    },
                string.Empty, false);

            Assert.IsNull(routingMap);

            routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                new[]
                    {
                        Tuple.Create(new PartitionKeyRange{Id = "2", MinInclusive = "", MaxExclusive = "0000000030"}, (ServiceIdentity)null),
                        Tuple.Create(new PartitionKeyRange{Id = "3", MinInclusive = "0000000030", MaxExclusive = "FF"}, (ServiceIdentity)null),
                    },
                string.Empty, false);

            Assert.IsNotNull(routingMap);
        }

        [TestMethod]
        public void TestGoneRanges()
        {
            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
              new[]
                    {
                        Tuple.Create(new PartitionKeyRange{ Id = "2", MinInclusive = "", MaxExclusive = "0000000030", Parents = new Collection<string>{"1", "0"}}, (ServiceIdentity)null),
                        Tuple.Create(new PartitionKeyRange{ Id = "3", MinInclusive = "0000000030", MaxExclusive = "0000000032", Parents = new Collection<string>{"5"}}, (ServiceIdentity)null),
                        Tuple.Create(new PartitionKeyRange{ Id = "4", MinInclusive = "0000000032", MaxExclusive = "FF"}, (ServiceIdentity)null),
                    },
              string.Empty, false);

            Assert.IsTrue(routingMap.IsGone("1"));
            Assert.IsTrue(routingMap.IsGone("0"));
            Assert.IsTrue(routingMap.IsGone("5"));

            Assert.IsFalse(routingMap.IsGone("2"));
            Assert.IsFalse(routingMap.IsGone("3"));
            Assert.IsFalse(routingMap.IsGone("4"));
            Assert.IsFalse(routingMap.IsGone("100"));
        }

        [TestMethod]
        public void TestTryCombineRanges()
        {
            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                new[]
                    {
                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "2",
                            MinInclusive = "0000000050",
                            MaxExclusive = "0000000070"},
                            (ServiceIdentity)null),

                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "0",
                            MinInclusive = "",
                            MaxExclusive = "0000000030"},
                            (ServiceIdentity)null),

                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "1",
                            MinInclusive = "0000000030",
                            MaxExclusive = "0000000050"},
                            (ServiceIdentity)null),

                         Tuple.Create(
                            new PartitionKeyRange{
                            Id = "3",
                            MinInclusive = "0000000070",
                            MaxExclusive = "FF"},
                            (ServiceIdentity)null),
                    }, string.Empty, false);

            CollectionRoutingMap newRoutingMap = routingMap.TryCombine(
                new[]
                    {
                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "4",
                            Parents = new Collection<string>{"0"},
                            MinInclusive = "",
                            MaxExclusive = "0000000010"},
                            (ServiceIdentity)null),

                         Tuple.Create(
                            new PartitionKeyRange{
                            Id = "5",
                            Parents = new Collection<string>{"0"},
                            MinInclusive = "0000000010",
                            MaxExclusive = "0000000030"},
                            (ServiceIdentity)null),
                    },
                    null, false);

            Assert.IsNotNull(newRoutingMap);

            newRoutingMap = routingMap.TryCombine(
                new[]
                    {
                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "6",
                            Parents = new Collection<string>{"0", "4"},
                            MinInclusive = "",
                            MaxExclusive = "0000000005"},
                            (ServiceIdentity)null),

                         Tuple.Create(
                            new PartitionKeyRange{
                            Id = "7",
                            Parents = new Collection<string>{"0", "4"},
                            MinInclusive = "0000000005",
                            MaxExclusive = "0000000010"},
                            (ServiceIdentity)null),

                         Tuple.Create(
                            new PartitionKeyRange{
                            Id = "8",
                            Parents = new Collection<string>{"0", "5"},
                            MinInclusive = "0000000010",
                            MaxExclusive = "0000000015"},
                            (ServiceIdentity)null),

                         Tuple.Create(
                            new PartitionKeyRange{
                            Id = "9",
                            Parents = new Collection<string>{"0", "5"},
                            MinInclusive = "0000000015",
                            MaxExclusive = "0000000030"},
                            (ServiceIdentity)null),
                    },
                    null, false);

            Assert.IsNotNull(newRoutingMap);

            newRoutingMap = routingMap.TryCombine(
                new[]
                    {
                        Tuple.Create(
                            new PartitionKeyRange{
                            Id = "10",
                            Parents = new Collection<string>{"0", "4", "6"},
                            MinInclusive = "",
                            MaxExclusive = "0000000002"},
                            (ServiceIdentity)null),
                    },
                    null, false);

            Assert.IsNull(newRoutingMap);
        }

        [TestMethod]
        public void TestNumericFastPathActivation()
        {
            // 32-char hex boundaries should activate numeric fast path
            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                new[]
                {
                    Tuple.Create(new PartitionKeyRange { Id = "0", MinInclusive = "", MaxExclusive = "05C1D9CD68C5BA000000000000000000" }, (ServiceIdentity)null),
                    Tuple.Create(new PartitionKeyRange { Id = "1", MinInclusive = "05C1D9CD68C5BA000000000000000000", MaxExclusive = "0F28318A56B2E4000000000000000000" }, (ServiceIdentity)null),
                    Tuple.Create(new PartitionKeyRange { Id = "2", MinInclusive = "0F28318A56B2E4000000000000000000", MaxExclusive = "FF" }, (ServiceIdentity)null),
                },
                string.Empty,
                false);

            Assert.IsNotNull(routingMap);
            Assert.AreEqual("0", routingMap.GetRangeByEffectivePartitionKey("").Id);
            Assert.AreEqual("0", routingMap.GetRangeByEffectivePartitionKey("05C1D9CD68C5B9FFFFFFFFFFFFFF0000").Id);
            Assert.AreEqual("1", routingMap.GetRangeByEffectivePartitionKey("05C1D9CD68C5BA000000000000000000").Id);
            Assert.AreEqual("1", routingMap.GetRangeByEffectivePartitionKey("0A000000000000000000000000000000").Id);
            Assert.AreEqual("2", routingMap.GetRangeByEffectivePartitionKey("0F28318A56B2E4000000000000000000").Id);
        }

        [TestMethod]
        public void TestNumericFastPathFallbackForVariableLengthEpk()
        {
            // Variable-length boundaries should NOT activate numeric fast path, but still work
            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                new[]
                {
                    Tuple.Create(new PartitionKeyRange { Id = "0", MinInclusive = "", MaxExclusive = "0000000030" }, (ServiceIdentity)null),
                    Tuple.Create(new PartitionKeyRange { Id = "1", MinInclusive = "0000000030", MaxExclusive = "FF" }, (ServiceIdentity)null),
                },
                string.Empty,
                false);

            Assert.IsNotNull(routingMap);
            Assert.AreEqual("0", routingMap.GetRangeByEffectivePartitionKey("0000000020").Id);
            Assert.AreEqual("1", routingMap.GetRangeByEffectivePartitionKey("0000000030").Id);
        }

        [TestMethod]
        public void TestTryParseHex32ToUInt128()
        {
            // Valid 32-char hex
            Assert.IsTrue(CollectionRoutingMap.TryParseHex32ToUInt128("00000000000000000000000000000000", out UInt128 zero));
            Assert.AreEqual(UInt128.Create(0, 0), zero);

            Assert.IsTrue(CollectionRoutingMap.TryParseHex32ToUInt128("00000000000000000000000000000001", out UInt128 one));
            Assert.AreEqual(UInt128.Create(1, 0), one);

            Assert.IsTrue(CollectionRoutingMap.TryParseHex32ToUInt128("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", out UInt128 max));
            Assert.AreEqual(UInt128.Create(ulong.MaxValue, ulong.MaxValue), max);

            // Mixed case
            Assert.IsTrue(CollectionRoutingMap.TryParseHex32ToUInt128("05C1D9cd68C5BA000000000000000000", out UInt128 mixed));
            Assert.AreEqual(UInt128.Create(0, 0x05C1D9CD68C5BA00), mixed);

            // Invalid: wrong length
            Assert.IsFalse(CollectionRoutingMap.TryParseHex32ToUInt128("0000000030", out _));
            Assert.IsFalse(CollectionRoutingMap.TryParseHex32ToUInt128("", out _));

            // Invalid: non-hex char
            Assert.IsFalse(CollectionRoutingMap.TryParseHex32ToUInt128("0000000000000000000000000000000G", out _));
        }

        [TestMethod]
        public void TestVariantEquivalence_AllVariantsAgreeOnRandomEpks()
        {
            // Build a V2-hash routing map with N evenly-spaced 128-bit boundaries
            // and assert every FastPathVariant resolves the same range id for the
            // same EPK input. Catches Layer 1 (payload SoA) regressions where the
            // sortedRangePayloads array could fall out of sync with orderedPartitionKeyRanges,
            // and locks behavior for subsequent layers (branchless search, prefetch).

            const int rangeCount = 64;
            CollectionRoutingMap routingMap = BuildV2HashRoutingMap(rangeCount, seed: 0xC051);

            const int sampleCount = 5_000;
            string[] epks = GenerateRandomHex32Epks(sampleCount, seed: 0xBEEF);

            // Capture the baseline (string variant) once.
            CollectionRoutingMap.FastPathVariant savedVariant = CollectionRoutingMap.ActiveVariant;
            try
            {
                CollectionRoutingMap.ActiveVariant = CollectionRoutingMap.FastPathVariant.String;
                string[] baseline = new string[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    baseline[i] = routingMap.GetRangeByEffectivePartitionKey(epks[i]).Id;
                }

                foreach (CollectionRoutingMap.FastPathVariant variant in new[]
                {
                    CollectionRoutingMap.FastPathVariant.UInt128,
                    CollectionRoutingMap.FastPathVariant.BytespanSeq,
                    CollectionRoutingMap.FastPathVariant.BytespanHand,
                    CollectionRoutingMap.FastPathVariant.Soa,
                    CollectionRoutingMap.FastPathVariant.StringSoa,
                })
                {
                    CollectionRoutingMap.ActiveVariant = variant;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        string actual = routingMap.GetRangeByEffectivePartitionKey(epks[i]).Id;
                        Assert.AreEqual(
                            baseline[i],
                            actual,
                            $"Variant {variant} disagreed with String baseline on EPK {epks[i]} (sample {i}).");
                    }
                }
            }
            finally
            {
                CollectionRoutingMap.ActiveVariant = savedVariant;
            }
        }

        [TestMethod]
        public void TestGetOverlappingRangesByBytes_MatchesStringPath()
        {
            const int rangeCount = 64;
            CollectionRoutingMap routingMap = BuildV2HashRoutingMap(rangeCount, seed: 0xC051);

            // 100 random [min, max) sub-ranges, each of length up to ~1/16 of the total
            // 128-bit space, so we hit small / medium / large overlap counts.
            const int sampleCount = 100;
            Random rng = new Random(0xFEED);
            for (int s = 0; s < sampleCount; s++)
            {
                byte[] minBytes = new byte[16];
                byte[] maxBytes = new byte[16];
                rng.NextBytes(minBytes);
                rng.NextBytes(maxBytes);
                if (minBytes[0] == 0xFF) minBytes[0] = 0xFE;
                if (maxBytes[0] == 0xFF) maxBytes[0] = 0xFE;
                if (CompareBE(minBytes, maxBytes) > 0)
                {
                    byte[] tmp = minBytes; minBytes = maxBytes; maxBytes = tmp;
                }
                else if (CompareBE(minBytes, maxBytes) == 0)
                {
                    maxBytes[15] = (byte)(maxBytes[15] ^ 0x01);
                    if (CompareBE(minBytes, maxBytes) > 0)
                    {
                        byte[] tmp = minBytes; minBytes = maxBytes; maxBytes = tmp;
                    }
                }

                string minHex = ToHex32(minBytes);
                string maxHex = ToHex32(maxBytes);

                IReadOnlyList<PartitionKeyRange> stringResult =
                    routingMap.GetOverlappingRanges(new Range<string>(minHex, maxHex, isMinInclusive: true, isMaxInclusive: false));
                IReadOnlyList<PartitionKeyRange> bytesResult =
                    routingMap.GetOverlappingRangesByBytes(minBytes, maxBytes);

                HashSet<string> stringIds = new HashSet<string>(stringResult.Select(r => r.Id));
                HashSet<string> bytesIds = new HashSet<string>(bytesResult.Select(r => r.Id));
                CollectionAssert.AreEquivalent(
                    stringIds.ToList(),
                    bytesIds.ToList(),
                    $"Sample {s}: GetOverlappingRangesByBytes diverged from GetOverlappingRanges.\n" +
                    $"  min = {minHex}\n  max = {maxHex}\n" +
                    $"  string = [{string.Join(",", stringIds)}]\n  bytes  = [{string.Join(",", bytesIds)}]");
            }
        }

        [TestMethod]
        public void TestGetOverlappingRangesByStrings_MatchesStringPath()
        {
            const int rangeCount = 64;
            CollectionRoutingMap routingMap = BuildV2HashRoutingMap(rangeCount, seed: 0xD0E1);

            const int sampleCount = 100;
            Random rng = new Random(0xD0E2);
            for (int s = 0; s < sampleCount; s++)
            {
                byte[] minBytes = new byte[16];
                byte[] maxBytes = new byte[16];
                rng.NextBytes(minBytes);
                rng.NextBytes(maxBytes);
                if (minBytes[0] == 0xFF) minBytes[0] = 0xFE;
                if (maxBytes[0] == 0xFF) maxBytes[0] = 0xFE;
                if (CompareBE(minBytes, maxBytes) > 0)
                {
                    byte[] tmp = minBytes; minBytes = maxBytes; maxBytes = tmp;
                }
                else if (CompareBE(minBytes, maxBytes) == 0)
                {
                    maxBytes[15] = (byte)(maxBytes[15] ^ 0x01);
                    if (CompareBE(minBytes, maxBytes) > 0)
                    {
                        byte[] tmp = minBytes; minBytes = maxBytes; maxBytes = tmp;
                    }
                }

                string minHex = ToHex32(minBytes);
                string maxHex = ToHex32(maxBytes);

                IReadOnlyList<PartitionKeyRange> stringResult =
                    routingMap.GetOverlappingRanges(new Range<string>(minHex, maxHex, isMinInclusive: true, isMaxInclusive: false));
                IReadOnlyList<PartitionKeyRange> soaResult =
                    routingMap.GetOverlappingRangesByStrings(minHex, maxHex);

                HashSet<string> stringIds = new HashSet<string>(stringResult.Select(r => r.Id));
                HashSet<string> soaIds = new HashSet<string>(soaResult.Select(r => r.Id));
                CollectionAssert.AreEquivalent(
                    stringIds.ToList(),
                    soaIds.ToList(),
                    $"Sample {s}: GetOverlappingRangesByStrings diverged from GetOverlappingRanges.\n" +
                    $"  min = {minHex}\n  max = {maxHex}\n" +
                    $"  string = [{string.Join(",", stringIds)}]\n  soa    = [{string.Join(",", soaIds)}]");
            }
        }

        [TestMethod]
        public void TestGetOverlappingRangesByStrings_OnV1HashTopology()
        {
            // V1-hash topology: 10-char zero-padded hex Min boundaries. The byte SoA path
            // (GetOverlappingRangesByBytes) does not work here because hasNumericFastPath
            // is false; the string SoA path must.
            const int rangeCount = 16;
            string[] boundaries = new string[rangeCount - 1];
            uint step = (uint)(uint.MaxValue / rangeCount);
            for (int i = 0; i < rangeCount - 1; i++)
            {
                ulong b = (ulong)(i + 1) * step;
                boundaries[i] = b.ToString("X10");
            }
            Array.Sort(boundaries, StringComparer.Ordinal);

            List<Tuple<PartitionKeyRange, ServiceIdentity>> ranges =
                new List<Tuple<PartitionKeyRange, ServiceIdentity>>(rangeCount);
            for (int i = 0; i < rangeCount; i++)
            {
                string min = i == 0 ? string.Empty : boundaries[i - 1];
                string max = i == rangeCount - 1 ? "FF" : boundaries[i];
                ranges.Add(Tuple.Create(
                    new PartitionKeyRange { Id = i.ToString(), MinInclusive = min, MaxExclusive = max },
                    (ServiceIdentity)null));
            }

            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                ranges, string.Empty, false);
            Assert.IsNotNull(routingMap);

            // 50 random sub-ranges in 10-char hex.
            Random rng = new Random(0xC0DE);
            for (int s = 0; s < 50; s++)
            {
                ulong a = (uint)rng.Next(0, int.MaxValue);
                ulong b = (uint)rng.Next(0, int.MaxValue);
                if (a == b) continue;
                if (a > b) { ulong tmp = a; a = b; b = tmp; }
                string minHex = a.ToString("X10");
                string maxHex = b.ToString("X10");

                IReadOnlyList<PartitionKeyRange> stringResult =
                    routingMap.GetOverlappingRanges(new Range<string>(minHex, maxHex, isMinInclusive: true, isMaxInclusive: false));
                IReadOnlyList<PartitionKeyRange> soaResult =
                    routingMap.GetOverlappingRangesByStrings(minHex, maxHex);

                HashSet<string> stringIds = new HashSet<string>(stringResult.Select(r => r.Id));
                HashSet<string> soaIds = new HashSet<string>(soaResult.Select(r => r.Id));
                CollectionAssert.AreEquivalent(
                    stringIds.ToList(),
                    soaIds.ToList(),
                    $"V1 sample {s}: GetOverlappingRangesByStrings diverged on min={minHex}, max={maxHex}.");
            }
        }

        private static int CompareBE(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i] < b[i] ? -1 : 1;
                }
            }
            return 0;
        }

        /// <summary>
        /// Verifies that the SoA fast-path correctly reflects the post-split topology
        /// after <see cref="CollectionRoutingMap.TryCombine"/> rebuilds the map. PKRanges
        /// are dynamic at runtime (splits / merges); the routing map is immutable per
        /// snapshot but a topology change triggers a full reconstruction. Variant G's
        /// parallel arrays (sortedByteBoundaries / sortedRangePayloads / sortedMaxBytes)
        /// must be regenerated from scratch on every reconstruction.
        /// </summary>
        [TestMethod]
        public void TestSoaFastPath_AfterSplit_ReflectsNewTopology()
        {
            // Start with a 4-range V2-hash map.
            CollectionRoutingMap original = BuildV2HashRoutingMap(rangeCount: 4, seed: 0xA110);
            Assert.AreEqual(4, original.OrderedPartitionKeyRanges.Count);

            // Pick the second range (id "1") and split it in half on a new V2-hash boundary
            // halfway between its [Min, Max). The split children get parents=["1"] so the
            // parent goes into goneRanges of the rebuilt map.
            PartitionKeyRange parent = original.OrderedPartitionKeyRanges
                .First(r => r.Id == "1");
            string splitBoundary = MidpointHex32(parent.MinInclusive, parent.MaxExclusive);

            CollectionRoutingMap splitMap = original.TryCombine(
                new[]
                {
                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "100",
                            Parents = new System.Collections.ObjectModel.Collection<string> { "1" },
                            MinInclusive = parent.MinInclusive,
                            MaxExclusive = splitBoundary,
                        },
                        (ServiceIdentity)null),
                    Tuple.Create(
                        new PartitionKeyRange
                        {
                            Id = "101",
                            Parents = new System.Collections.ObjectModel.Collection<string> { "1" },
                            MinInclusive = splitBoundary,
                            MaxExclusive = parent.MaxExclusive,
                        },
                        (ServiceIdentity)null),
                },
                changeFeedNextIfNoneMatch: string.Empty,
                useLengthAwareComparer: false);

            Assert.IsNotNull(splitMap, "TryCombine must succeed for a clean parent->children split.");
            Assert.AreEqual(5, splitMap.OrderedPartitionKeyRanges.Count, "Parent should be replaced by two children -> 4 - 1 + 2 = 5 ranges.");
            Assert.IsTrue(splitMap.IsGone("1"), "Original parent id should be in goneRanges of the rebuilt map.");

            // Cross-variant equivalence on the post-split map. If the SoA arrays were
            // not rebuilt, variant G would still resolve EPKs that fall in the parent's
            // range to the OLD parent payload, while string / uint128 / bytespan-* would
            // resolve to one of the NEW children. So an equivalence sweep across all
            // variants on the post-split map is a precise dynamism check.
            string[] epks = GenerateRandomHex32Epks(count: 2_000, seed: 0xB22B);

            string original_env = Environment.GetEnvironmentVariable("COSMOS_PKRANGE_VARIANT");
            CollectionRoutingMap.FastPathVariant savedVariant = CollectionRoutingMap.ActiveVariant;
            try
            {
                string[] firstResults = null;
                CollectionRoutingMap.FastPathVariant firstVariant = default;
                bool firstSet = false;

                foreach (CollectionRoutingMap.FastPathVariant variant in new[]
                {
                    CollectionRoutingMap.FastPathVariant.String,
                    CollectionRoutingMap.FastPathVariant.UInt128,
                    CollectionRoutingMap.FastPathVariant.BytespanSeq,
                    CollectionRoutingMap.FastPathVariant.BytespanHand,
                    CollectionRoutingMap.FastPathVariant.Soa,
                    CollectionRoutingMap.FastPathVariant.StringSoa,
                })
                {
                    CollectionRoutingMap.ActiveVariant = variant;

                    string[] hits = epks.Select(epk => splitMap.GetRangeByEffectivePartitionKey(epk).Id).ToArray();

                    if (!firstSet)
                    {
                        firstResults = hits;
                        firstVariant = variant;
                        firstSet = true;
                    }
                    else
                    {
                        for (int i = 0; i < hits.Length; i++)
                        {
                            if (hits[i] != firstResults[i])
                            {
                                Assert.Fail(
                                    $"Variant '{variant}' diverged from '{firstVariant}' on EPK {epks[i]} after split: " +
                                    $"got id={hits[i]}, expected id={firstResults[i]}. " +
                                    "SoA arrays were not rebuilt to reflect the post-split topology.");
                            }
                        }
                    }

                    // Sanity-check: none of the hits should be the goneRanges parent.
                    Assert.IsFalse(hits.Contains("1"), $"Variant '{variant}' returned the gone parent range id '1'.");
                }
            }
            finally
            {
                CollectionRoutingMap.ActiveVariant = savedVariant;
                Environment.SetEnvironmentVariable("COSMOS_PKRANGE_VARIANT", original_env);
            }
        }

        private static string MidpointHex32(string minHex, string maxHex)
        {
            // Compute an interior 32-char hex boundary strictly between minHex and maxHex
            // (treating both as 128-bit big-endian unsigned integers). Handles the
            // empty-min sentinel ("" = 0) and the all-FF max sentinel ("FF" => 1<<128).
            byte[] minBytes = HexToBytesOrZero(minHex);
            byte[] maxBytes = HexToBytesOrAllFF(maxHex);

            // mid = min + (max - min) / 2, computed byte-wise with carry.
            byte[] mid = new byte[16];
            int borrow = 0;
            byte[] diff = new byte[16];
            for (int i = 15; i >= 0; i--)
            {
                int d = maxBytes[i] - minBytes[i] - borrow;
                if (d < 0) { d += 256; borrow = 1; } else { borrow = 0; }
                diff[i] = (byte)d;
            }
            // diff /= 2 (right shift by 1)
            int carry = 0;
            for (int i = 0; i < 16; i++)
            {
                int v = (carry << 8) | diff[i];
                diff[i] = (byte)(v >> 1);
                carry = v & 1;
            }
            // mid = min + diff
            int add = 0;
            for (int i = 15; i >= 0; i--)
            {
                int s = minBytes[i] + diff[i] + add;
                mid[i] = (byte)(s & 0xFF);
                add = s >> 8;
            }

            return ToHex32(mid);
        }

        private static byte[] HexToBytesOrZero(string hex)
        {
            byte[] bytes = new byte[16];
            if (string.IsNullOrEmpty(hex))
            {
                return bytes;
            }
            for (int i = 0; i < 16; i++)
            {
                bytes[i] = (byte)((HexNibble(hex[i * 2]) << 4) | HexNibble(hex[(i * 2) + 1]));
            }
            return bytes;
        }

        private static byte[] HexToBytesOrAllFF(string hex)
        {
            if (hex == "FF")
            {
                byte[] bytes = new byte[16];
                for (int i = 0; i < 16; i++) bytes[i] = 0xFF;
                return bytes;
            }
            return HexToBytesOrZero(hex);
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }

        /// <summary>
        /// Variant H (StringSoa) targets V1 hash / hierarchical PK / variable-length-EPK
        /// collections that cannot use the V2 byte fast path (variant G). This test builds
        /// a V1-style routing map whose Min boundaries are 10-character zero-padded hex
        /// (the same shape used elsewhere in this file's TryCombine fixtures), drives 1,000
        /// random 10-char-hex EPKs through both the baseline string variant and
        /// StringSoa, and asserts they resolve to identical PartitionKeyRange ids on every
        /// sample. Confirms the SoA payload arrays are populated even when
        /// <c>hasNumericFastPath</c> is false (i.e. the unconditional payload-SoA build).
        /// </summary>
        [TestMethod]
        public void TestStringSoa_OnV1HashTopology_MatchesStringBaseline()
        {
            // Build a 16-range V1-style map: 10-char zero-padded hex boundaries spaced
            // evenly across the V1 numeric space [0, 2^32). Pure string-keyed ordering.
            const int rangeCount = 16;
            string[] boundaries = new string[rangeCount - 1];
            uint step = (uint)(uint.MaxValue / rangeCount);
            for (int i = 0; i < rangeCount - 1; i++)
            {
                ulong b = (ulong)(i + 1) * step;
                boundaries[i] = b.ToString("X10");
            }
            Array.Sort(boundaries, StringComparer.Ordinal);

            List<Tuple<PartitionKeyRange, ServiceIdentity>> ranges =
                new List<Tuple<PartitionKeyRange, ServiceIdentity>>(rangeCount);
            for (int i = 0; i < rangeCount; i++)
            {
                string min = i == 0 ? string.Empty : boundaries[i - 1];
                string max = i == rangeCount - 1 ? "FF" : boundaries[i];
                ranges.Add(Tuple.Create(
                    new PartitionKeyRange { Id = i.ToString(), MinInclusive = min, MaxExclusive = max },
                    (ServiceIdentity)null));
            }

            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                ranges, string.Empty, false);
            Assert.IsNotNull(routingMap);

            // Generate 1,000 random 10-char hex EPKs strictly less than "FF" (so they
            // fall inside the addressable V1 space). Cap leading nibble to ensure the
            // string sorts before "FF" under ordinal compare.
            Random rng = new Random(0xD1AD);
            const int sampleCount = 1_000;
            string[] epks = new string[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                ulong v = (uint)rng.Next(0, int.MaxValue);
                epks[i] = v.ToString("X10");
            }

            CollectionRoutingMap.FastPathVariant savedVariant = CollectionRoutingMap.ActiveVariant;
            try
            {
                CollectionRoutingMap.ActiveVariant = CollectionRoutingMap.FastPathVariant.String;
                string[] baseline = epks.Select(e => routingMap.GetRangeByEffectivePartitionKey(e).Id).ToArray();

                CollectionRoutingMap.ActiveVariant = CollectionRoutingMap.FastPathVariant.StringSoa;
                string[] hits = epks.Select(e => routingMap.GetRangeByEffectivePartitionKey(e).Id).ToArray();

                for (int i = 0; i < sampleCount; i++)
                {
                    if (hits[i] != baseline[i])
                    {
                        Assert.Fail(
                            $"StringSoa diverged from String on V1-hash sample {i} epk={epks[i]}: " +
                            $"got id={hits[i]}, expected id={baseline[i]}.");
                    }
                }
            }
            finally
            {
                CollectionRoutingMap.ActiveVariant = savedVariant;
            }
        }

        private static CollectionRoutingMap BuildV2HashRoutingMap(int rangeCount, int seed)
        {
            // Generate rangeCount-1 evenly-spaced random 128-bit boundaries, build
            // ranges [0,b0), [b0,b1), ..., [bN-2,FF].
            Random rng = new Random(seed);
            byte[] bytes = new byte[16];
            string[] boundaries = new string[rangeCount - 1];
            for (int i = 0; i < rangeCount - 1; i++)
            {
                rng.NextBytes(bytes);
                if (bytes[0] == 0xFF)
                {
                    bytes[0] = 0xFE;
                }
                boundaries[i] = ToHex32(bytes);
            }
            Array.Sort(boundaries, StringComparer.Ordinal);

            List<Tuple<PartitionKeyRange, ServiceIdentity>> ranges =
                new List<Tuple<PartitionKeyRange, ServiceIdentity>>(rangeCount);

            for (int i = 0; i < rangeCount; i++)
            {
                string min = i == 0 ? string.Empty : boundaries[i - 1];
                string max = i == rangeCount - 1 ? "FF" : boundaries[i];
                ranges.Add(Tuple.Create(
                    new PartitionKeyRange { Id = i.ToString(), MinInclusive = min, MaxExclusive = max },
                    (ServiceIdentity)null));
            }

            CollectionRoutingMap routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                ranges,
                string.Empty,
                false);
            Assert.IsNotNull(routingMap);
            return routingMap;
        }

        private static string[] GenerateRandomHex32Epks(int count, int seed)
        {
            // PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey is the literal "FF",
            // and GetRangeByEffectivePartitionKey throws when the input compares >= "FF"
            // ordinally. Any 32-char hex EPK starting with "FF" sorts strictly after "FF"
            // (shorter-prefix-sorts-first), so we cap the leading byte to 0xFE to keep
            // every generated sample inside the addressable range.
            Random rng = new Random(seed);
            byte[] bytes = new byte[16];
            string[] result = new string[count];
            for (int i = 0; i < count; i++)
            {
                rng.NextBytes(bytes);
                if (bytes[0] == 0xFF)
                {
                    bytes[0] = 0xFE;
                }
                result[i] = ToHex32(bytes);
            }
            return result;
        }

        private static string ToHex32(byte[] bytes)
        {
            char[] chars = new char[32];
            for (int i = 0; i < 16; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = HexChar(b >> 4);
                chars[i * 2 + 1] = HexChar(b & 0x0F);
            }
            return new string(chars);
        }

        private static char HexChar(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
    }
}