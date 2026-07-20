// Data template
// {
//     "id": "llm-126d96c6-575c-4d98-b6c6-006a2431db86",
//     "timestamp": "7/19/2026 11:00:00 AM",
//     "appId": "LLM-Testing-UniversalLLMAllModels-DEV-SUB-01",
//     "productName": "LLM Testing UniversalLLMAllModels DEV",
//     "deploymentName": "text-embedding-3-large",
//     "backendId": "aif-dvfwtaj5al46e-1",
//     "customDimension1": "",
//     "customDimension2": "",
//     "gatewayName": "apim-dvfwtaj5al46e",
//     "gatewayRegion": "Sweden Central",
//     "promptTokens": "10",
//     "responseTokens": "0",
//     "totalTokens": "10",
//     "completionAcceptedPredictionTokens": "0",
//     "completionAudioTokens": "0",
//     "completionReasoningTokens": "0",
//     "completionRejectedPredictionTokens": "0",
//     "promptAudioTokens": "0",
//     "promptCachedTokens": "0",
//     "targetService": "NA",
//     "model": "text-embedding-3-large",
//     "aiGatewayId": "managed",
//     "RequestIp": "NA",
//     "operationName": "NA"
// }

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

class Program
{
    static async Task Main(string[] args)
    {
        // Load configuration. settings.json is the committed template; settings.local.json
        // (git-ignored) overrides it and is where you keep sensitive values like connection strings.
        // Use the app base directory (where the files are copied) so it works regardless of the
        // current working directory (e.g. when launched via `dotnet run` from the repo root).
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("settings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("settings.local.json", optional: true, reloadOnChange: false)
            .Build();

        var cosmosDbConfig = configuration.GetSection("CosmosDb");
        var recordGenerationConfig = configuration.GetSection("RecordGeneration");
        var workloadConfig = configuration.GetSection("Workload");
        var outputConfig = configuration.GetSection("Output");
        var modelPricingConfig = configuration.GetSection("ModelPricing");

        string connectionString = cosmosDbConfig["ConnectionString"];
        string databaseName = cosmosDbConfig["DatabaseName"];
        string containerName = cosmosDbConfig["ContainerName"];

        // Model pricing container seeding (upsert-if-exists) configuration.
        bool seedPricingContainer = bool.TryParse(modelPricingConfig["SeedPricingContainer"], out var seedFlag) && seedFlag;
        string pricingContainerName = string.IsNullOrWhiteSpace(modelPricingConfig["PricingContainerName"])
            ? "model-pricing"
            : modelPricingConfig["PricingContainerName"];
        string pricingSourcePath = modelPricingConfig["PricingSourcePath"] ?? string.Empty;
        int totalRecords = int.Parse(recordGenerationConfig["TotalRecords"]);
        DateTime startDate = DateTime.Parse(recordGenerationConfig["StartDate"]);
        DateTime endDate = DateTime.Parse(recordGenerationConfig["EndDate"]);
        bool deleteExistingData = bool.Parse(recordGenerationConfig["DeleteExistingData"]);
        string modelPricingPath = recordGenerationConfig["ModelPricingPath"] ?? string.Empty;

        // Global multiplier applied to every generated token count. Increase to produce
        // heavier token usage per record (e.g. 2.0 roughly doubles tokens). Defaults to 1.0.
        double tokenScale = ParseDoubleOrDefault(recordGenerationConfig["TokenScale"], 1.0);
        if (tokenScale <= 0) tokenScale = 1.0;

        // Output target: "cosmos" (default), "jsonl", or "both".
        string outputTarget = (outputConfig["Target"] ?? "cosmos").Trim().ToLowerInvariant();
        string jsonlFilePath = string.IsNullOrWhiteSpace(outputConfig["JsonlFilePath"])
            ? "./generated-usage.jsonl"
            : outputConfig["JsonlFilePath"];
        bool writeToCosmos = outputTarget is "cosmos" or "both";
        bool writeToJsonl = outputTarget is "jsonl" or "both";
        if (!writeToCosmos && !writeToJsonl)
        {
            Console.WriteLine($"Unknown Output:Target '{outputTarget}'. Use 'cosmos', 'jsonl', or 'both'. Aborting.");
            return;
        }

        // Workload mix (percentages) for chat-capable models. Non-chat models are always "model" workload.
        double agentPercent = ParseDoubleOrDefault(workloadConfig["AgentPercent"], 18);
        double mcpPercent = ParseDoubleOrDefault(workloadConfig["McpPercent"], 12);

        var random = new Random();

        // -------- Model catalog (driven by model-pricing-generated.json, in-scope + active only) --------
        var models = LoadModelCatalog(modelPricingPath);
        if (models.Count == 0)
        {
            Console.WriteLine("No active models were found in the pricing catalog. Aborting.");
            return;
        }

        // Selection weights: cheaper models get more traffic (inverse of blended token price),
        // scaled by a category popularity factor (embeddings are high-volume, images are rare).
        var modelWeights = models.Select(m => m.SelectionWeight).ToList();

        Console.WriteLine("Loaded models (model | category | blended $/1M | selection weight):");
        foreach (var m in models)
        {
            Console.WriteLine($"  {m.Model,-32} {m.Category,-9} {(m.InputPrice + m.OutputPrice),8:0.###}   {m.SelectionWeight:0.##}");
        }

        // -------- Products following "LLM-BusinessUnit-Usecase-ENV" --------
        var products = BuildProducts();
        var productWeights = products.Select(p => p.Weight).ToList();

        // -------- Backends --------
        string[] modelBackends = { "aif-dvfwtaj5al46e-1", "aif-dvfwtaj5al46e-2", "aif-dvfwtaj5al46e-3" };
        string[] mcpBackends = { "mcp-dvfwtaj5al46e-1", "mcp-dvfwtaj5al46e-2" };
        string[] agentsBackends = { "agent-dvfwtaj5al46e-1", "agent-dvfwtaj5al46e-2" };

        // -------- Gateway --------
        const string gatewayName = "apim-dvfwtaj5al46e";
        string[] gatewayRegions = { "Sweden Central", "West Europe", "East US 2", "UAE North" };
        double[] gatewayRegionWeights = { 0.5, 0.2, 0.2, 0.1 };

        string[] channels = { "web", "mobile", "api", "batch", "teams" };
        double[] channelWeights = { 0.35, 0.2, 0.25, 0.1, 0.1 };

        try
        {
            // Initialize Cosmos client when writing usage records or seeding the pricing container.
            Microsoft.Azure.Cosmos.Container container = null;
            if (writeToCosmos || seedPricingContainer)
            {
                CosmosClient client = new CosmosClient(connectionString);
                Database database = await client.CreateDatabaseIfNotExistsAsync(databaseName);

                // Upsert model pricing into its own container (create-if-not-exists, then upsert each item).
                if (seedPricingContainer)
                {
                    await SeedModelPricingAsync(database, pricingContainerName, pricingSourcePath);
                }

                if (writeToCosmos)
                {
                    container = await database.CreateContainerIfNotExistsAsync(containerName, "/productName");

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
                }
            }

            // Open the JSONL writer only when writing to a local file.
            StreamWriter jsonlWriter = null;
            if (writeToJsonl)
            {
                var fullPath = Path.GetFullPath(jsonlFilePath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                jsonlWriter = new StreamWriter(fullPath, append: false);
                Console.WriteLine($"Writing JSONL output to: {fullPath}");
            }

            try
            {
                int recordCount = 0;

                for (int i = 0; i < totalRecords; i++)
                {
                    var product = PickWeighted(products, productWeights, random);
                    var model = PickWeighted(models, modelWeights, random);

                    // Decide the workload type. Only chat-capable models can be routed as agent/mcp calls.
                    string workload = "model";
                    if (model.Category == "chat")
                    {
                        double roll = random.NextDouble() * 100.0;
                        if (roll < agentPercent) workload = "agent";
                        else if (roll < agentPercent + mcpPercent) workload = "mcp";
                    }

                    string backendId;
                    string targetService;
                    string operationName;
                    switch (workload)
                    {
                        case "agent":
                            backendId = agentsBackends[random.Next(agentsBackends.Length)];
                            targetService = "agent";
                            operationName = "Agent run execution";
                            break;
                        case "mcp":
                            backendId = mcpBackends[random.Next(mcpBackends.Length)];
                            targetService = "mcp";
                            operationName = "MCP tool invocation";
                            break;
                        default:
                            backendId = modelBackends[random.Next(modelBackends.Length)];
                            (targetService, operationName) = ModelWorkloadInfo(model.Category);
                            break;
                    }

                    var usage = GenerateTokenUsage(model, workload, random, tokenScale);

                    var record = new
                    {
                        id = "llm-" + Guid.NewGuid().ToString(),
                        timestamp = RandomTimestamp(startDate, endDate, random),
                        appId = $"{product.Name}-SUB-{random.Next(1, 6):00}",
                        productName = product.Name,
                        deploymentName = model.DeploymentName,
                        backendId = backendId,
                        customDimension1 = PickWeighted(channels, channelWeights, random),
                        customDimension2 = $"CC-{product.BusinessUnit}-{random.Next(100, 1000)}",
                        gatewayName = gatewayName,
                        gatewayRegion = PickWeighted(gatewayRegions, gatewayRegionWeights, random),
                        promptTokens = usage.PromptTokens,
                        responseTokens = usage.ResponseTokens,
                        totalTokens = usage.TotalTokens,
                        completionAcceptedPredictionTokens = usage.CompletionAcceptedPredictionTokens,
                        completionAudioTokens = usage.CompletionAudioTokens,
                        completionReasoningTokens = usage.CompletionReasoningTokens,
                        completionRejectedPredictionTokens = usage.CompletionRejectedPredictionTokens,
                        promptAudioTokens = usage.PromptAudioTokens,
                        promptCachedTokens = usage.PromptCachedTokens,
                        targetService = targetService,
                        model = model.Model,
                        aiGatewayId = "managed",
                        RequestIp = $"{random.Next(1, 256)}.{random.Next(0, 256)}.{random.Next(0, 256)}.{random.Next(1, 256)}",
                        operationName = operationName
                    };

                    if (writeToCosmos)
                    {
                        await container.CreateItemAsync(record, new PartitionKey(record.productName));
                    }

                    if (writeToJsonl)
                    {
                        await jsonlWriter.WriteLineAsync(JsonConvert.SerializeObject(record));
                    }

                    recordCount++;

                    // Update console for every 10 inserted records
                    if (recordCount % 10 == 0)
                    {
                        Console.WriteLine($"{recordCount} records generated...");
                    }
                }

                Console.WriteLine($"Records generated successfully. Total records: {recordCount}");
            }
            finally
            {
                if (jsonlWriter != null)
                {
                    await jsonlWriter.FlushAsync();
                    jsonlWriter.Dispose();
                }
            }
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

    // ---------------------------------------------------------------------
    // Model catalog
    // ---------------------------------------------------------------------

    static List<ModelInfo> LoadModelCatalog(string configuredPath)
    {
        var pricing = LoadPricing(configuredPath);
        var catalog = new List<ModelInfo>();

        foreach (var p in pricing.Where(p => p.isActive))
        {
            var (category, reasoning, audio) = Classify(p.model);
            double blended = p.CostPerInputUnit + p.CostPerOutputUnit;
            // Free/preview models (blended == 0) still consume compute; give them a nominal price
            // so they don't dominate the weighted selection.
            if (blended <= 0) blended = 8.0;

            double categoryFactor = category switch
            {
                "embedding" => 1.6,   // very high volume in RAG scenarios
                "chat" => 1.0,
                "image" => 0.3,       // image generation is comparatively rare
                _ => 1.0
            };

            // Cheaper models => higher weight (expensive models generate less traffic).
            double selectionWeight = categoryFactor * (100.0 / (blended + 2.0));

            catalog.Add(new ModelInfo
            {
                Model = p.model,
                DeploymentName = string.IsNullOrWhiteSpace(p.deploymentName) ? p.model : p.deploymentName,
                Category = category,
                SupportsReasoning = reasoning,
                SupportsAudio = audio,
                InputPrice = p.CostPerInputUnit,
                OutputPrice = p.CostPerOutputUnit,
                SelectionWeight = selectionWeight
            });
        }

        return catalog;
    }

    static List<ModelPricing> LoadPricing(string configuredPath, string defaultFileName = "model-pricing-generated.json")
    {
        string path = ResolvePricingPath(configuredPath, defaultFileName);
        if (path != null && File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var items = JsonConvert.DeserializeObject<List<ModelPricing>>(json);
                if (items != null && items.Count > 0)
                {
                    Console.WriteLine($"Loaded model pricing from: {path}");
                    return items;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read pricing file '{path}': {ex.Message}. Falling back to embedded catalog.");
            }
        }
        else
        {
            Console.WriteLine($"{defaultFileName} not found. Falling back to embedded catalog.");
        }

        return EmbeddedPricing();
    }

    static string ResolvePricingPath(string configuredPath, string defaultFileName = "model-pricing-generated.json")
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        // Walk up from the working directory looking for the pricing file at the repo root.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, defaultFileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Model pricing container seeding (upsert-if-exists / create-if-missing)
    // ---------------------------------------------------------------------

    static async Task SeedModelPricingAsync(Database database, string containerName, string configuredPath)
    {
        // The extended pricing file is the source of truth for the pricing container.
        var pricing = LoadPricing(configuredPath, "model-pricing-generated-extended.json");
        if (pricing.Count == 0)
        {
            Console.WriteLine("No model pricing entries found to seed. Skipping pricing container upsert.");
            return;
        }

        var pricingContainer = await database.CreateContainerIfNotExistsAsync(containerName, "/model");
        Console.WriteLine($"Seeding model pricing into container '{containerName}' (upsert)...");

        int upserted = 0;
        foreach (var p in pricing)
        {
            // Cosmos requires an 'id'; fall back to the model name if the source omits it.
            if (string.IsNullOrWhiteSpace(p.id))
            {
                p.id = p.model;
            }

            await pricingContainer.Container.UpsertItemAsync(p, new PartitionKey(p.model));
            upserted++;
        }

        Console.WriteLine($"Model pricing upsert complete. {upserted} entries written to '{containerName}'.");
    }

    // Categorization + capability flags for the in-scope models.
    static (string category, bool reasoning, bool audio) Classify(string model)
    {
        string m = model.ToLowerInvariant();

        if (m.Contains("embedding")) return ("embedding", false, false);
        if (m.Contains("image") || m.StartsWith("flux") || m.Contains("dall")) return ("image", false, false);

        // Reasoning-capable chat models.
        bool reasoning = m.StartsWith("gpt-5") || m.Contains("gpt-oss") || m.Contains("claude-sonnet");
        // Audio-capable chat models (e.g. realtime / audio-native models such as gpt-realtime-1.5).
        bool audio = m.Contains("realtime") || m.Contains("audio");

        return ("chat", reasoning, audio);
    }

    // Fallback catalog mirroring model-pricing-generated.json (used only if the file is missing).
    static List<ModelPricing> EmbeddedPricing() => new()
    {
        new ModelPricing { model = "gpt-4.1", deploymentName = "gpt-4.1", isActive = true, CostPerInputUnit = 2.00, CostPerOutputUnit = 8.00, CostUnit = 1000000 },
        new ModelPricing { model = "gpt-image-1.5", deploymentName = "gpt-image-1.5", isActive = true, CostPerInputUnit = 5.00, CostPerOutputUnit = 40.00, CostUnit = 1000000 },
        new ModelPricing { model = "MAI-Image-2.5-Flash", deploymentName = "MAI-Image-2.5-Flash", isActive = true, CostPerInputUnit = 0, CostPerOutputUnit = 0, CostUnit = 1000000 },
        new ModelPricing { model = "FLUX.2-pro", deploymentName = "FLUX.2-pro", isActive = true, CostPerInputUnit = 0, CostPerOutputUnit = 0, CostUnit = 1000000 },
        new ModelPricing { model = "text-embedding-3-large", deploymentName = "text-embedding-3-large", isActive = true, CostPerInputUnit = 0.13, CostPerOutputUnit = 0, CostUnit = 1000000 },
        new ModelPricing { model = "Mistral-Large-3", deploymentName = "Mistral-Large-3", isActive = true, CostPerInputUnit = 2.00, CostPerOutputUnit = 6.00, CostUnit = 1000000 },
        new ModelPricing { model = "gpt-5.4-mini", deploymentName = "gpt-5.4-mini", isActive = true, CostPerInputUnit = 0.25, CostPerOutputUnit = 2.00, CostUnit = 1000000 },
        new ModelPricing { model = "Phi-4", deploymentName = "Phi-4", isActive = true, CostPerInputUnit = 0.125, CostPerOutputUnit = 0.50, CostUnit = 1000000 },
        new ModelPricing { model = "gpt-5.2", deploymentName = "gpt-5.2", isActive = true, CostPerInputUnit = 1.25, CostPerOutputUnit = 10.00, CostUnit = 1000000 }//,
        // new ModelPricing { model = "global.amazon.nova-2-lite-v1:0", deploymentName = "global.amazon.nova-2-lite-v1:0", isActive = true, CostPerInputUnit = 0.06, CostPerOutputUnit = 0.24, CostUnit = 1000000 },
        // new ModelPricing { model = "amazon.nova-lite-v1:0", deploymentName = "amazon.nova-lite-v1:0", isActive = true, CostPerInputUnit = 0.06, CostPerOutputUnit = 0.24, CostUnit = 1000000 },
        // new ModelPricing { model = "gemini-2.5-flash-lite", deploymentName = "gemini-2.5-flash-lite", isActive = true, CostPerInputUnit = 0.10, CostPerOutputUnit = 0.40, CostUnit = 1000000 },
        // new ModelPricing { model = "gemini-2.5-flash", deploymentName = "gemini-2.5-flash", isActive = true, CostPerInputUnit = 0.30, CostPerOutputUnit = 2.50, CostUnit = 1000000 },
        // new ModelPricing { model = "claude-sonnet-4-6", deploymentName = "claude-sonnet-4-6", isActive = true, CostPerInputUnit = 3.00, CostPerOutputUnit = 15.00, CostUnit = 1000000 },
        // new ModelPricing { model = "claude-haiku-4-5", deploymentName = "claude-haiku-4-5", isActive = true, CostPerInputUnit = 1.00, CostPerOutputUnit = 5.00, CostUnit = 1000000 },
        // new ModelPricing { model = "openai.gpt-oss-120b", deploymentName = "openai.gpt-oss-120b", isActive = true, CostPerInputUnit = 0.15, CostPerOutputUnit = 0.60, CostUnit = 1000000 },
        // new ModelPricing { model = "openai.gpt-oss-20b", deploymentName = "openai.gpt-oss-20b", isActive = true, CostPerInputUnit = 0.05, CostPerOutputUnit = 0.20, CostUnit = 1000000 },
    };

    // ---------------------------------------------------------------------
    // Products: "LLM-BusinessUnit-Usecase-ENV"
    // ---------------------------------------------------------------------

    static List<ProductInfo> BuildProducts()
    {
        // Prod products carry more traffic than Test/Dev.
        var raw = new (string bu, string usecase, string env)[]
        {
            ("HR", "LeaveAgent", "Prod"),
            ("HR", "PolicyBot", "Dev"),
            ("Finance", "InvoiceAssistant", "Prod"),
            ("Finance", "ForecastBot", "Test"),
            ("Legal", "ContractReview", "Prod"),
            ("Legal", "ComplianceBot", "Dev"),
            ("Retail", "CustomerSupport", "Prod"),
            ("Retail", "ProductSearch", "Prod"),
            ("Marketing", "CampaignWriter", "Test"),
            ("Marketing", "ContentGen", "Dev"),
            ("IT", "CodeAssistant", "Prod"),
            ("IT", "Helpdesk", "Test"),
            ("Sales", "LeadQualifier", "Prod"),
            ("Ops", "DocSummarizer", "Dev"),
            ("Support", "KnowledgeBase", "Prod"),
        };

        return raw.Select(r => new ProductInfo
        {
            Name = $"LLM-{r.bu}-{r.usecase}-{r.env}",
            BusinessUnit = r.bu,
            Environment = r.env,
            Weight = r.env switch { "Prod" => 3.0, "Test" => 1.5, _ => 1.0 }
        }).ToList();
    }

    static (string targetService, string operationName) ModelWorkloadInfo(string category) => category switch
    {
        "embedding" => ("embeddings", "Creates an embedding vector representing the input text"),
        "image" => ("images.generations", "Creates an image given a prompt"),
        _ => ("chat.completion", "Creates a completion for the chat message"),
    };

    // ---------------------------------------------------------------------
    // Token usage generation (realistic, category & capability aware)
    // ---------------------------------------------------------------------

    static TokenUsage GenerateTokenUsage(ModelInfo model, string workload, Random r, double tokenScale)
    {
        var u = new TokenUsage();

        if (model.Category == "embedding")
        {
            u.PromptTokens = Scale(Skewed(r, 8, 4000, 2.0), tokenScale);
            u.ResponseTokens = 0;
            u.TotalTokens = u.PromptTokens;
            return u;
        }

        if (model.Category == "image")
        {
            u.PromptTokens = Scale(Skewed(r, 8, 250, 1.5), tokenScale);      // text prompt tokens
            u.ResponseTokens = Scale(Skewed(r, 400, 6000, 1.4), tokenScale); // rendered image tokens
            u.TotalTokens = u.PromptTokens + u.ResponseTokens;
            return u;
        }

        // Chat-style workloads (direct model, agent, or MCP tool call).
        // Agents carry more context; MCP tool calls are typically smaller.
        // The workload factor and the global token scale are combined.
        double scale = (workload switch { "agent" => 1.4, "mcp" => 0.55, _ => 1.0 }) * tokenScale;

        int promptTokens = (int)(Skewed(r, 120, 8000, 1.9) * scale);
        int visibleOutput = (int)(Skewed(r, 40, 1600, 1.7) * scale);

        int reasoningTokens = 0;
        if (model.SupportsReasoning && r.NextDouble() < 0.7)
        {
            reasoningTokens = (int)(Skewed(r, 128, 4000, 1.6) * scale);
        }

        // Prompt caching: a portion of the prompt is served from cache on repeat calls.
        int cachedTokens = 0;
        if (r.NextDouble() < 0.45)
        {
            cachedTokens = (int)(promptTokens * (0.2 + r.NextDouble() * 0.6));
            if (cachedTokens > promptTokens) cachedTokens = promptTokens;
        }

        // Predicted outputs (speculative decoding) show up occasionally on chat calls.
        int acceptedPrediction = 0;
        int rejectedPrediction = 0;
        if (workload == "model" && r.NextDouble() < 0.12)
        {
            acceptedPrediction = (int)(Skewed(r, 10, 400, 1.5) * scale);
            rejectedPrediction = (int)(Skewed(r, 0, 120, 1.5) * scale);
        }

        // Audio tokens for audio-capable models. Audio-native models (e.g. gpt-realtime-1.5)
        // carry audio on the vast majority of calls, and audio dominates both the prompt and
        // the response. Audio tokens are a component of the prompt/response totals, so they
        // are derived as a large fraction of the visible token counts.
        int promptAudio = 0;
        int completionAudio = 0;
        if (model.SupportsAudio && r.NextDouble() < 0.85)
        {
            promptAudio = (int)(promptTokens * (0.6 + r.NextDouble() * 0.35));
            if (promptAudio > promptTokens) promptAudio = promptTokens;

            completionAudio = (int)(visibleOutput * (0.6 + r.NextDouble() * 0.35));
            if (completionAudio > visibleOutput) completionAudio = visibleOutput;
        }

        u.PromptTokens = promptTokens;
        u.PromptCachedTokens = cachedTokens;
        u.PromptAudioTokens = promptAudio;
        u.ResponseTokens = visibleOutput + reasoningTokens; // completion tokens include reasoning tokens
        u.CompletionReasoningTokens = reasoningTokens;
        u.CompletionAudioTokens = completionAudio;
        u.CompletionAcceptedPredictionTokens = acceptedPrediction;
        u.CompletionRejectedPredictionTokens = rejectedPrediction;
        u.TotalTokens = u.PromptTokens + u.ResponseTokens;

        return u;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    // Skewed random integer in [min, max) biased toward min when power > 1.
    static int Skewed(Random r, int min, int max, double power)
    {
        if (max <= min) return min;
        double u = Math.Pow(r.NextDouble(), power);
        return min + (int)((max - min) * u);
    }

    // Applies the global token multiplier to a token count.
    static int Scale(int value, double factor) => (int)(value * factor);

    static T PickWeighted<T>(IList<T> items, IList<double> weights, Random r)
    {
        double total = 0;
        for (int i = 0; i < weights.Count; i++) total += weights[i];
        double roll = r.NextDouble() * total;
        double cumulative = 0;
        for (int i = 0; i < items.Count; i++)
        {
            cumulative += weights[i];
            if (roll <= cumulative) return items[i];
        }
        return items[items.Count - 1];
    }

    static double ParseDoubleOrDefault(string value, double fallback)
        => double.TryParse(value, out var parsed) ? parsed : fallback;

    // Realistic timestamp: weekdays and business hours are far more likely than nights/weekends.
    static string RandomTimestamp(DateTime start, DateTime end, Random random)
    {
        int rangeDays = Math.Max(1, (end - start).Days);

        DateTime day = default;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            day = start.AddDays(random.Next(rangeDays)).Date;
            bool weekend = day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday;
            // Accept weekdays always; accept weekends only ~25% of the time.
            if (!weekend || random.NextDouble() < 0.25) break;
        }

        int hour = PickBusinessHour(random);
        DateTime ts = day.AddHours(hour).AddMinutes(random.Next(60)).AddSeconds(random.Next(60));
        return ts.ToString("M/d/yyyy h:mm:ss tt");
    }

    // Relative weight per hour of day (0-23) approximating an office workload curve.
    static readonly double[] HourWeights =
    {
        0.3, 0.2, 0.2, 0.2, 0.3, 0.5, // 00-05
        1.0, 2.0, 4.0, 6.0, 7.0, 6.5, // 06-11
        5.0, 6.0, 7.0, 6.5, 5.5, 4.0, // 12-17
        2.5, 1.5, 1.0, 0.8, 0.5, 0.4  // 18-23
    };

    static int PickBusinessHour(Random random)
    {
        double total = 0;
        foreach (var w in HourWeights) total += w;
        double roll = random.NextDouble() * total;
        double cumulative = 0;
        for (int h = 0; h < HourWeights.Length; h++)
        {
            cumulative += HourWeights[h];
            if (roll <= cumulative) return h;
        }
        return 12;
    }
}

// ---------------------------------------------------------------------
// Supporting types
// ---------------------------------------------------------------------

class ModelPricing
{
    public string id { get; set; }
    public string model { get; set; }
    public string deploymentName { get; set; }
    public bool isActive { get; set; }
    public double CostPerInputUnit { get; set; }
    public double CostPerOutputUnit { get; set; }
    public double CostPerCachedInputUnit { get; set; }
    public double CostPerAudioInputUnit { get; set; }
    public double CostPerCachedAudioInputUnit { get; set; }
    public double CostPerAudioOutputUnit { get; set; }
    public double CostPerReasoningOutputUnit { get; set; }
    public double CostPerImageInputUnit { get; set; }
    public double CostPerCachedImageInputUnit { get; set; }
    public double CostUnit { get; set; }
    public double BaseCost { get; set; }
    public string Currency { get; set; }
    public string CalculationMethod { get; set; }
    public string region { get; set; }
}

class ModelInfo
{
    public string Model { get; set; }
    public string DeploymentName { get; set; }
    public string Category { get; set; }
    public bool SupportsReasoning { get; set; }
    public bool SupportsAudio { get; set; }
    public double InputPrice { get; set; }
    public double OutputPrice { get; set; }
    public double SelectionWeight { get; set; }
}

class ProductInfo
{
    public string Name { get; set; }
    public string BusinessUnit { get; set; }
    public string Environment { get; set; }
    public double Weight { get; set; }
}

class TokenUsage
{
    public int PromptTokens { get; set; }
    public int ResponseTokens { get; set; }
    public int TotalTokens { get; set; }
    public int CompletionAcceptedPredictionTokens { get; set; }
    public int CompletionAudioTokens { get; set; }
    public int CompletionReasoningTokens { get; set; }
    public int CompletionRejectedPredictionTokens { get; set; }
    public int PromptAudioTokens { get; set; }
    public int PromptCachedTokens { get; set; }
}
