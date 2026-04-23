using System.Diagnostics;
using System.Net.Http;
using Microsoft.Azure.Cosmos;

const string endpoint = "https://localhost:8901/";
const string key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
const string dbName = "testdb";
const string containerName = "testcoll";

Console.WriteLine("=== Cosmos Mock Gateway E2E Test ===");

var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
};

var options = new CosmosClientOptions
{
    ConnectionMode = ConnectionMode.Gateway,
    HttpClientFactory = () => new HttpClient(handler),
};

Console.WriteLine($"Connecting to {endpoint}...");
var sw = Stopwatch.StartNew();

using var client = new CosmosClient(endpoint, key, options);
var container = client.GetContainer(dbName, containerName);

Console.WriteLine($"Client created in {sw.ElapsedMilliseconds}ms");
Console.WriteLine("Reading item (pk=test-pk, id=item-1)...");

sw.Restart();
try
{
    var response = await container.ReadItemAsync<dynamic>("item-1", new Microsoft.Azure.Cosmos.PartitionKey("test-pk"));
    sw.Stop();
    Console.WriteLine($"ReadItem succeeded in {sw.ElapsedMilliseconds}ms");
    Console.WriteLine($"  Status: {response.StatusCode}");
    Console.WriteLine($"  RequestCharge: {response.RequestCharge}");
    Console.WriteLine($"  ActivityId: {response.ActivityId}");
    Console.WriteLine($"  Item: {response.Resource}");
    Console.WriteLine("\n✅ E2E TEST PASSED");
}
catch (CosmosException ex)
{
    sw.Stop();
    Console.WriteLine($"❌ CosmosException: {ex.StatusCode} - {ex.Message}");
    Console.WriteLine($"  ActivityId: {ex.ActivityId}");
    Console.WriteLine($"  Diagnostics: {ex.Diagnostics}");
    Console.WriteLine("\n❌ E2E TEST FAILED");
    Environment.Exit(1);
}
catch (Exception ex)
{
    sw.Stop();
    Console.WriteLine($"❌ Exception: {ex.GetType().Name} - {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
    Console.WriteLine("\n❌ E2E TEST FAILED");
    Environment.Exit(1);
}
