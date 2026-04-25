//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;
    using Microsoft.Azure.Cosmos.Performance.Tests.Data;
    using Microsoft.Azure.Cosmos.Performance.Tests.Mocks;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// End-to-end Direct-mode point-read benchmark that exercises the real production SDK
    /// from <see cref="Container.ReadItemStreamAsync(string, Cosmos.PartitionKey, ItemRequestOptions, System.Threading.CancellationToken)"/>
    /// down to (but not through) the RNTBD transport, against a realistic
    /// <see cref="PkRangeRoutingFactory.ExpectedRowCount"/>-PKRange routing map.
    ///
    /// Designed to isolate the routing-map lookup cost that
    /// <see href="https://github.com/kirankumarkolli/ThinClient/issues/1">issue #1</see>
    /// optimizes: gateway HTTP and TCP are mocked at the <see cref="HttpMessageHandler"/>
    /// and <see cref="TransportClient"/> seams, and the address cache is pre-warmed so the
    /// measured iteration performs no I/O — only in-process routing, serialization, and
    /// SDK bookkeeping.
    /// </summary>
    [MemoryDiagnoser]
    public class DirectModeRoutingBenchmark
    {
        private const string AccountName = "bench";
        private const string RegionEndpoint = "https://bench-eastus.documents.azure.com";
        private const string DatabaseName = "bench-db";
        private const string DatabaseRid = "ccZ1AA==";
        private const string ContainerName = "bench-coll";
        private const string ContainerRid = "ccZ1ANCszwk=";
        private const string TsvPath = "Data/shared_conversations_pkranges.tsv";
        private static readonly string CannedOkDocumentId = MockedItemBenchmarkHelper.ExistingItemId;
        private const int PkPoolSize = 65536;
        private const int PkSeed = 42;

        private CosmosClient client;
        private Container container;
        private PkRangeMetadataHandler handler;
        private DirectStubTransport transport;
        private string[] pkPool;

        [GlobalSetup]
        public void GlobalSetup()
        {
            // MockRequestHelper's static ctor reads samplepayload.json from CWD;
            // under BenchmarkDotNet the working directory points at the host project, so
            // re-anchor it at the benchmark assembly's output directory before anything
            // touches that helper.
            string exeDir = Path.GetDirectoryName(typeof(DirectModeRoutingBenchmark).Assembly.Location);
            if (!string.IsNullOrEmpty(exeDir))
            {
                Directory.SetCurrentDirectory(exeDir);
            }
            Environment.SetEnvironmentVariable("COSMOS_DISABLE_IMDS_ACCESS", "true");

            IReadOnlyList<PartitionKeyRange> ranges = PkRangeRoutingFactory.LoadFromTsv(TsvPath);
            this.pkPool = PkRangeRoutingFactory.GenerateRandomPartitionKeyStrings(PkPoolSize, PkSeed);

            this.handler = new PkRangeMetadataHandler(
                AccountName, RegionEndpoint, DatabaseName, DatabaseRid, ContainerName, ContainerRid, ranges);
            this.transport = new DirectStubTransport();

            string fakeKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));

            CosmosClientOptions options = new CosmosClientOptions()
            {
                ConnectionMode = ConnectionMode.Direct,
                ConsistencyLevel = Cosmos.ConsistencyLevel.Session,
                HttpClientFactory = () => new HttpClient(this.handler, disposeHandler: false),
                TransportClientHandlerFactory = _ => this.transport,
            };

            this.client = new CosmosClient(RegionEndpoint + "/", fakeKey, options);
            this.container = this.client.GetContainer(DatabaseName, ContainerName);

            // Address-cache population is delegated to BenchmarkDotNet's warmup phase:
            // BDN runs many warmup invocations of [Benchmark] before measurement, which
            // cycles through the PK pool and populates the GatewayAddressCache for every
            // resolved PKRange. The measured iterations therefore observe steady-state
            // routing with no gateway HTTP I/O.
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            this.client?.Dispose();
        }

        /// <summary>
        /// One point-read per iteration, picking a uniformly-random partition key from the
        /// deterministic PK pool so the routing-map lookup sees varied effective partition
        /// keys (not a single hot PKR) without a shared cross-iteration counter.
        /// Returns a status code to prevent the JIT from eliding the call.
        /// </summary>
        [Benchmark]
        public async Task<int> ReadItemStream()
        {
            int i = Random.Shared.Next(this.pkPool.Length);
            using ResponseMessage response = await this.container.ReadItemStreamAsync(
                CannedOkDocumentId, new Cosmos.PartitionKey(this.pkPool[i]));
            return (int)response.StatusCode;
        }
    }
}
