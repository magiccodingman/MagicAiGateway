using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicAiGateway.Protocol;

public static class MagicStreamEventTypes
{
    public const string RunStarted = "magic.run.started";
    public const string ToolStarted = "magic.tool.started";
    public const string ToolCompleted = "magic.tool.completed";
    public const string ReasoningDelta = "magic.reasoning.delta";
    public const string RunCompleted = "magic.run.completed";
    public const string RunFailed = "magic.run.failed";
}

public abstract record MagicChatStreamUpdate;

public sealed record MagicContentDelta(string Text) : MagicChatStreamUpdate;

public sealed record MagicReasoningDelta(string Text) : MagicChatStreamUpdate;

public sealed record MagicToolProgress(
    string EventType,
    string? ToolCallId,
    string? ToolName,
    JsonElement? Payload) : MagicChatStreamUpdate;

public sealed record MagicOpenAiChunkUpdate(MagicChatCompletionChunk Chunk) : MagicChatStreamUpdate;

public sealed record MagicRunCompletedUpdate(MagicChatCompletionSummary Summary) : MagicChatStreamUpdate;

public sealed record MagicChatCompletionSummary
{
    public string? Id { get; init; }
    public string? Model { get; init; }
    public string? FinishReason { get; init; }
    public string Content { get; init; } = string.Empty;
    public string Reasoning { get; init; } = string.Empty;
    public MagicTokenUsage? Usage { get; init; }
    public MagicRunMetadata? MagicRun { get; init; }
}

public sealed record MagicSseEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    [JsonPropertyName(MagicAiGatewayProtocol.PropertyName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicRunMetadata? MagicAiGateway { get; init; }
}
