//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Performance.Tests.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;
    using Microsoft.Azure.Cosmos.Performance.Tests.Data;
    using Microsoft.Azure.Cosmos.Performance.Tests.Mocks;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;

    /// <summary>
    /// Internal-scenario point-read benchmark that bypasses every public SDK abstraction
    /// (<see cref="Container"/>, <see cref="ItemRequestOptions"/>, <see cref="ResponseMessage"/>,
    /// <see cref="Cosmos.PartitionKey"/> wrapping, retry policies, diagnostics, …) and drives
    /// the read by hand-constructing a <see cref="DocumentServiceRequest"/> and invoking
    /// <c>DocumentClient.ProcessRequestAsync</c> — which calls <c>GetStoreProxy(request)</c>
    /// and <c>IStoreModel.ProcessMessageAsync</c> directly.
    ///
    /// Mocking, address-cache priming and PK distribution are identical to
    /// <see cref="DirectModeRoutingBenchmark"/>, so this benchmark isolates the cost of the
    /// routing-map lookup on the absolute hottest path that production callers can plausibly
    /// invoke (e.g. internal services that already have their own retry / diagnostics layer).
    /// The partition-key value is published exclusively via
    /// <c>HttpConstants.HttpHeaders.PartitionKey</c>, which routes through the JSON-PK branch
    /// of <see cref="Microsoft.Azure.Cosmos.Routing.AddressResolver"/> — i.e. the path where
    /// the producer-side string-EPK bypass (PR #9 / variant F) actually fires.
    /// </summary>
    [Config(typeof(DirectModeRoutingBenchmarkConfig))]
    public class DirectModeRoutingRawDsrBenchmark
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

        // Name-based document path. Same shape DocumentClient.ReadDocumentAsync would build,
        // hoisted to a static so the per-iteration cost only includes header + DSR construction.
        private static readonly string DocumentLink =
            $"dbs/{DatabaseName}/colls/{ContainerName}/docs/{CannedOkDocumentId}";

        private CosmosClient client;
        private DocumentClient documentClient;
        private PkRangeMetadataHandler handler;
        private DirectStubTransport transport;
        private string[] pkPool;

        [GlobalSetup]
        public void GlobalSetup()
        {
            string exeDir = Path.GetDirectoryName(typeof(DirectModeRoutingRawDsrBenchmark).Assembly.Location);
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
            this.documentClient = this.client.DocumentClient;

            // BenchmarkDotNet's warmup phase iterates [Benchmark] many times before measurement,
            // which both populates the collection cache (first ResolveCollectionAsync call) and
            // walks the PK pool to populate the GatewayAddressCache for every PKRange touched.
            // The measured iterations therefore observe steady-state routing — no I/O.
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            this.client?.Dispose();
        }

        /// <summary>
        /// Hand-build a read DSR + invoke the inner store proxy directly. Returns the status
        /// code so the JIT cannot elide the call.
        /// </summary>
        [Benchmark]
        public async Task<int> ReadViaRawDsr()
        {
            int i = Random.Shared.Next(this.pkPool.Length);
            string pkJson = "[\"" + this.pkPool[i] + "\"]";

            INameValueCollection headers = new RequestNameValueCollection();
            headers.Set(HttpConstants.HttpHeaders.PartitionKey, pkJson);

            using DocumentServiceRequest request = DocumentServiceRequest.Create(
                OperationType.Read,
                ResourceType.Document,
                DocumentLink,
                AuthorizationTokenType.PrimaryMasterKey,
                headers);

            DocumentServiceResponse response = await this.documentClient.ProcessRequestAsync(
                request, retryPolicyInstance: null, cancellationToken: CancellationToken.None);
            return (int)response.StatusCode;
        }
    }
}
