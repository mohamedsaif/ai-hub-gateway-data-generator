// Data template
//{
//    "id": "chatcmpl-9qx6I4RVJeFr2J0XkU8fC2sYYsBQO", //GUID
//    "timestamp": "7/31/2024 6:27:50 AM",
//    "appId": "ed699654-a120-4af1-a986-ba34feaa7068", //Entra App ID
//    "subscriptionId": "master", //is driven from the associated product name below to be in the format productName-sub-001
//    "productName": "AI-HR", //Can be OAI-HR-Assistant, OAI-Retail-CustomerSupport, OAI-Retail-Commerce, SEARCH-Retail-Knowledge, SEARCH-HR-PolicyKB
//    "targetService": "chat.completion",
//    "model": "gpt-35-turbo", // can be text-embedding-ada-002, gpt-35-turbo, gpt-4o-2024-05-13, Hybrid-Search, DALLE
//    "gatewayName": "apim-7pg4fleh6wgj6.azure-api.net",
//    "gatewayRegion": "East US",
//    "aiGatewayId": "managed",
//    "RequestIp": "20.121.82.216",
//    "operationName": "Creates a completion for the chat message",
//    "sessionId": "NA",
//    "endUserId": "NA",
//    "backendId": "openai-backend-2", // Can be openai-backend-0, openai-backend-1, openai-backend-2, aisearch-backend-0, aisearch-backend-1
//    "routeLocation": "eastus2", //Can be EastUS2, NorthUS, EastUS
//    "routeName": "EastUS2", //Can be EastUS2, NorthUS, EastUS
//    "deploymentName": "chat", //Can be chat, gpt-4o, dalle, Retail-Index, HR-Policies-Index
//    "promptTokens": 38, //(number between 50 and 500)
//    "responseTokens": 222,// (number between 300 and 1500)
//    "totalTokens": 260 //(is the sum of promptTokens and response tokens)
//}

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class Program
{
    static async Task Main(string[] args)
    {
        // Load configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("settings.json")
            .Build();

        var cosmosDbConfig = configuration.GetSection("CosmosDb");
        var recordGenerationConfig = configuration.GetSection("RecordGeneration");

        string connectionString = cosmosDbConfig["ConnectionString"];
        string databaseName = cosmosDbConfig["DatabaseName"];
        string containerName = cosmosDbConfig["ContainerName"];
        int totalRecords = int.Parse(recordGenerationConfig["TotalRecords"]);
        DateTime startDate = DateTime.Parse(recordGenerationConfig["StartDate"]);
        DateTime endDate = DateTime.Parse(recordGenerationConfig["EndDate"]);
        bool deleteExistingData = bool.Parse(recordGenerationConfig["DeleteExistingData"]);

        try
        {
            // Initialize Cosmos client
            CosmosClient client = new CosmosClient(connectionString);
            Database database = await client.CreateDatabaseIfNotExistsAsync(databaseName);
            Microsoft.Azure.Cosmos.Container container = await database.CreateContainerIfNotExistsAsync(containerName, "/productName");

            // Delete existing data if the flag is true
            if (deleteExistingData)
            {
                Console.WriteLine("Deleting existing data...");
                var query = new QueryDefinition("SELECT * FROM c");
                using (FeedIterator<dynamic> resultSetIterator = container.GetItemQueryIterator<dynamic>(query))
                {
                    while (resultSetIterator.HasMoreResults)
                    {
                        FeedResponse<dynamic> response = await resultSetIterator.ReadNextAsync();
                        foreach (var item in response)
                        {
                            await container.DeleteItemAsync<dynamic>(item.id.ToString(), new PartitionKey(item.productName.ToString()));
                        }
                    }
                }
                Console.WriteLine("Existing data deleted.");
            }

            // Generate and insert records
            Random random = new Random();
            string[] productNames = { "OAI-HR-Assistant", "OAI-Retail-CustomerSupport", "OAI-Retail-Commerce", "SEARCH-Retail-Knowledge", "SEARCH-HR-PolicyKB" };
            string[] models = { "text-embedding-ada-002", "gpt-35-turbo", "gpt-4o-2024-05-13", "Hybrid-Search", "DALLE" };
            string[] openaiBackends = { "openai-backend-0", "openai-backend-1", "openai-backend-2" };
            string[] aisearchBackends = { "aisearch-backend-0", "aisearch-backend-1" };
            string[] openaiDeploymentNames = { "embedding", "chat", "gpt-4o", "dalle" };
            string[] aisearchDeploymentNames = { "Retail-Index", "HR-Policies-Index" };
            string[] routeLocations = { "EastUS2", "NorthUS", "EastUS" };

            int recordCount = 0;

            for (int i = 0; i < totalRecords; i++)
            {
                var promptTokens = random.Next(500, 5001);
                var responseTokens = random.Next(3000, 15001);
                var productName = productNames[random.Next(productNames.Length)];
                string backendId;
                string deploymentName;

                if (productName.StartsWith("OAI"))
                {
                    backendId = openaiBackends[random.Next(openaiBackends.Length)];
                    deploymentName = openaiDeploymentNames[random.Next(openaiDeploymentNames.Length)];
                }
                else
                {
                    backendId = aisearchBackends[random.Next(aisearchBackends.Length)];
                    deploymentName = aisearchDeploymentNames[random.Next(aisearchDeploymentNames.Length)];
                }

                var record = new
                {
                    id = Guid.NewGuid().ToString(),
                    timestamp = RandomDate(startDate, endDate, random),
                    appId = Guid.NewGuid().ToString(),
                    subscriptionId = $"{productName}-sub-001",
                    productName = productName,
                    targetService = "chat.completion",
                    model = models[random.Next(models.Length)],
                    gatewayName = "apim-7pg4fleh6wgj6.azure-api.net",
                    gatewayRegion = "East US",
                    aiGatewayId = "managed",
                    RequestIp = $"{random.Next(1, 256)}.{random.Next(1, 256)}.{random.Next(1, 256)}.{random.Next(1, 256)}",
                    operationName = "Creates a completion for the chat message",
                    sessionId = "NA",
                    endUserId = "NA",
                    backendId = backendId,
                    routeLocation = routeLocations[random.Next(routeLocations.Length)],
                    routeName = routeLocations[random.Next(routeLocations.Length)],
                    deploymentName = deploymentName,
                    promptTokens = promptTokens,
                    responseTokens = responseTokens,
                    totalTokens = promptTokens + responseTokens
                };

                await container.CreateItemAsync(record, new PartitionKey(record.productName));
                recordCount++;

                // Update console for every 10 inserted records
                if (recordCount % 10 == 0)
                {
                    Console.WriteLine($"{recordCount} records inserted...");
                }
            }

            Console.WriteLine($"Records generated and inserted successfully. Total records: {recordCount}");
        }
        catch (CosmosException ex)
        {
            Console.WriteLine($"CosmosException: {ex.Message}");
            // Additional logging or error handling
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex.Message}");
            // Additional logging or error handling
        }
    }

    static string RandomDate(DateTime start, DateTime end, Random random)
    {
        int range = (end - start).Days;
        DateTime randomDate = start.AddDays(random.Next(range)).AddSeconds(random.Next(86400));
        return randomDate.ToString("MM/dd/yyyy h:mm:ss tt");
    }
}
