# ai-hub-gateway-data-generator

Generates realistic AI usage/telemetry data for the [AI Hub Gateway solution accelerator](https://github.com/Azure-Samples/ai-hub-gateway-solution-accelerator) and writes it to Azure Cosmos DB.

The generator produces usage records that mirror the AI Hub Gateway usage schema (token counts, deployments, backends, gateway metadata, and the extended token-tracking fields) so you can populate dashboards, reports, and cost analytics with lifelike traffic without calling real models.

## What it generates

Each record aligns with the current AI Hub Gateway usage document template:

| Field | Description |
| --- | --- |
| `id` | Unique record id, prefixed with `llm-`. |
| `timestamp` | Business-hours / weekday-weighted timestamp (`M/d/yyyy h:mm:ss tt`). |
| `appId` | Application/subscription id derived from the product (e.g. `LLM-HR-LeaveAgent-Prod-SUB-01`). |
| `productName` | Product following the `LLM-BusinessUnit-Usecase-ENV` convention (partition key). |
| `deploymentName` | Deployment name of the selected model. |
| `backendId` | Routed backend: model (`aif-*`), MCP (`mcp-*`), or agent (`agent-*`). |
| `customDimension1` | Channel (`web`, `mobile`, `api`, `batch`, `teams`). |
| `customDimension2` | Cost center (e.g. `CC-HR-421`). |
| `gatewayName` / `gatewayRegion` | Gateway identity and weighted region. |
| `promptTokens` / `responseTokens` / `totalTokens` | Core token usage. |
| `promptCachedTokens` | Portion of the prompt served from cache. |
| `promptAudioTokens` / `completionAudioTokens` | Audio tokens (audio-capable models only). |
| `completionReasoningTokens` | Reasoning tokens (reasoning-capable models only). |
| `completionAcceptedPredictionTokens` / `completionRejectedPredictionTokens` | Predicted-output (speculative decoding) tokens. |
| `targetService` | `chat.completion`, `embeddings`, `images.generations`, `agent`, or `mcp`. |
| `model` | Model name from the pricing catalog. |
| `aiGatewayId` | Always `managed`. |
| `RequestIp` | Randomized client IP. |
| `operationName` | Human-readable operation description. |

## How the data stays realistic

- **Price-aware traffic** — Models are selected with a weight inversely proportional to their blended token price, so **expensive models generate less traffic** than cheaper ones. Embeddings are weighted higher (high-volume RAG scenarios) and images lower (comparatively rare).
- **Pricing-driven catalog** — Models are loaded from [`model-pricing-generated.json`](model-pricing-generated.json) (only `isActive` models are used). Each model is classified as `chat`, `embedding`, or `image`, with reasoning/audio capability flags.
- **Capability-aware tokens** — Reasoning tokens are only emitted for reasoning-capable models, audio tokens only for audio-capable models, and prompt caching appears on a portion of chat calls. Token counts use right-skewed distributions (most calls small, a few large).
- **Workload mix** — Chat-capable models are routed as direct model calls, agent runs, or MCP tool invocations (configurable mix). Agent calls carry more context; MCP tool calls are smaller.
- **Time patterns** — Timestamps are weighted toward weekday business hours, with weekends heavily reduced.
- **Product weighting** — `Prod` products receive more traffic than `Test` and `Dev`.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- An Azure Cosmos DB (NoSQL) account and its connection string

## Configuration

Configuration is split into two files:

- [`src/AIHubGW.UsageGenerator/settings.json`](src/AIHubGW.UsageGenerator/settings.json) — the committed template with non-sensitive defaults.
- `src/AIHubGW.UsageGenerator/settings.local.json` — an optional, **git-ignored** override for sensitive values (e.g. the Cosmos DB connection string). Any keys here override `settings.json`.

At runtime the generator loads `settings.json` first, then overlays `settings.local.json` if present. Put secrets only in `settings.local.json`.

Example `settings.local.json`:

```json
{
  "CosmosDb": {
    "ConnectionString": "AccountEndpoint=https://<account>.documents.azure.com:443/;AccountKey=<key>;"
  }
}
```

Full `settings.json` template:

```json
{
  "CosmosDb": {
    "ConnectionString": "REPLACE WITH YOUR COSMOS DB CONNECTION STRING",
    "DatabaseName": "ai-usage-db",
    "ContainerName": "ai-usage-container"
  },
  "RecordGeneration": {
    "TotalRecords": 100,
    "DeleteExistingData": false,
    "StartDate": "2024-10-01T00:00:00Z",
    "EndDate": "2024-12-31T23:59:59Z",
    "ModelPricingPath": "",
    "TokenScale": 1.0
  },
  "Output": {
    "Target": "cosmos",
    "JsonlFilePath": "./generated-usage.jsonl"
  },
  "Workload": {
    "AgentPercent": 18,
    "McpPercent": 12
  }
}
```

| Setting | Description |
| --- | --- |
| `CosmosDb.ConnectionString` | Cosmos DB connection string. **Required when writing to Cosmos.** |
| `CosmosDb.DatabaseName` | Database name (created if it does not exist). |
| `CosmosDb.ContainerName` | Container name (created with partition key `/productName` if it does not exist). |
| `RecordGeneration.TotalRecords` | Number of usage records to generate. |
| `RecordGeneration.DeleteExistingData` | If `true`, deletes all existing documents before generating (Cosmos only). |
| `RecordGeneration.StartDate` / `EndDate` | Time window for generated timestamps. |
| `RecordGeneration.ModelPricingPath` | Optional explicit path to the pricing file. If empty, the generator searches upward from the working directory for `model-pricing-generated.json`, and falls back to an embedded catalog if not found. |
| `RecordGeneration.TokenScale` | Global multiplier applied to every generated token count. `1.0` = defaults; `2.0` roughly doubles tokens per record. Values `<= 0` are treated as `1.0`. |
| `Output.Target` | Where to write records: `cosmos` (default), `jsonl` (local file only), or `both`. |
| `Output.JsonlFilePath` | Path to the JSONL output file (created/overwritten). Used when `Target` is `jsonl` or `both`. |
| `Workload.AgentPercent` | Percent of chat-capable calls routed as agent runs. |
| `Workload.McpPercent` | Percent of chat-capable calls routed as MCP tool invocations. |

> The remaining percentage of chat calls are direct model calls. Non-chat models (embeddings, images) are always direct model calls.

### Output options

- **`cosmos`** — Inserts each record into the configured Cosmos DB container (requires a valid connection string).
- **`jsonl`** — Writes records to a local newline-delimited JSON file (one JSON object per line). No Cosmos connection is required, so this is handy for local testing, sample datasets, or bulk import elsewhere.
- **`both`** — Writes to Cosmos DB and the local JSONL file in the same run.

## Model pricing catalog

The in-scope models and prices live in [`model-pricing-generated.json`](model-pricing-generated.json). To add, remove, or reprice a model, edit that file:

```json
{
  "id": "1",
  "model": "gpt-4.1",
  "deploymentName": "gpt-4.1",
  "isActive": true,
  "CostPerInputUnit": 2.00,
  "CostPerOutputUnit": 8.00,
  "CostUnit": 1000000,
  "Currency": "USD",
  "CalculationMethod": "tokens",
  "region": "ALL"
}
```

- Set `isActive` to `false` to exclude a model from generation.
- Model category (chat / embedding / image) and reasoning capability are inferred from the model name.

## Usage

From the repository root:

```powershell
# Restore and build
dotnet build src/AIHubGW.UsageGenerator/AIHubGW.UsageGenerator.csproj -c Debug

# Run the generator
dotnet run --project src/AIHubGW.UsageGenerator/AIHubGW.UsageGenerator.csproj
```

The console logs the loaded model catalog (with selection weights) and progress every 10 inserted records:

```
Loaded model pricing from: D:\Repos\ai-hub-gateway-data-generator\model-pricing-generated.json
Loaded models (model | category | blended $/1M | selection weight):
  gpt-4.1                          chat        10        8.33
  text-embedding-3-large           embedding   0.13      75.19
  ...
10 records inserted...
20 records inserted...
Records generated and inserted successfully. Total records: 100
```

## Regenerating from scratch

To wipe the container and generate a fresh dataset, set `DeleteExistingData` to `true` in `settings.json` and run the generator again.

## Project structure

```
model-pricing-generated.json          # In-scope model catalog + pricing
src/AIHubGW.UsageGenerator/
  Program.cs                          # Generator implementation
  settings.json                       # Cosmos DB + generation settings
  AIHubGW.UsageGenerator.csproj
```
