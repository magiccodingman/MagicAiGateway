using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MagicAiGateway.Protocol;

public sealed record MagicServiceDescriptor
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("availability")]
    public string Availability { get; init; } = "installed";

    [JsonPropertyName("supported_endpoints")]
    public IReadOnlyList<string> SupportedEndpoints { get; init; } = [];

    [JsonPropertyName("default_run_timeout_seconds")]
    public int DefaultRunTimeoutSeconds { get; init; }

    [JsonPropertyName("maximum_run_timeout_seconds")]
    public int MaximumRunTimeoutSeconds { get; init; }

    [JsonPropertyName("options_schema")]
    public JsonObject OptionsSchema { get; init; } = new();

    [JsonPropertyName("response_schema")]
    public JsonObject ResponseSchema { get; init; } = new();

    [JsonPropertyName("streaming_events")]
    public IReadOnlyList<string> StreamingEvents { get; init; } = [];
}

public sealed record MagicServiceCatalog
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "magic.service.list";

    [JsonPropertyName("data")]
    public IReadOnlyList<MagicServiceDescriptor> Data { get; init; } = [];
}

public sealed record MagicRunMetadata
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = MagicAiGatewayProtocol.CurrentVersion;

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("finish_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; init; }

    [JsonPropertyName("model_calls")]
    public int ModelCalls { get; init; }

    [JsonPropertyName("tool_calls")]
    public int ToolCalls { get; init; }

    [JsonPropertyName("usage_accuracy")]
    public string UsageAccuracy { get; init; } = MagicUsageAccuracy.Unavailable;

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicTokenUsage? Usage { get; init; }

    [JsonPropertyName("model_call_usage")]
    public IReadOnlyList<MagicModelCallUsage> ModelCallUsage { get; init; } = [];

    [JsonPropertyName("service_result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ServiceResult { get; init; }
}

public sealed record MagicModelCallUsage
{
    [JsonPropertyName("sequence")]
    public int Sequence { get; init; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("usage")]
    public required MagicTokenUsage Usage { get; init; }

    [JsonPropertyName("accuracy")]
    public string Accuracy { get; init; } = MagicUsageAccuracy.ProviderReported;
}

public sealed record MagicRunError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }
}

public sealed record ManagedToolsOptions
{
    [JsonPropertyName("mcp_profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Description("Configured MCP profile used for tool discovery and execution.")]
    public string? McpProfile { get; init; }

    [JsonPropertyName("maximum_rounds")]
    [Description("Maximum model/tool continuation rounds for this run.")]
    public int MaximumRounds { get; init; } = 16;

    [JsonPropertyName("maximum_tool_calls")]
    [Description("Maximum total tool calls permitted during this run.")]
    public int MaximumToolCalls { get; init; } = 64;

    [JsonPropertyName("include_reasoning")]
    [Description("Expose normalized reasoning events when enriched streaming is requested.")]
    public bool IncludeReasoning { get; init; }
}

public sealed record MagicReasoningSegment
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("sequence")]
    public int Sequence { get; init; }

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }
}

public sealed record MagicModelTurn
{
    [JsonPropertyName("assistant_message")]
    public required MagicChatMessage AssistantMessage { get; init; }

    [JsonPropertyName("reasoning")]
    public IReadOnlyList<MagicReasoningSegment> Reasoning { get; init; } = [];

    [JsonPropertyName("finish_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; init; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicTokenUsage? Usage { get; init; }

    [JsonPropertyName("provider_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ProviderMetadata { get; init; }
}
