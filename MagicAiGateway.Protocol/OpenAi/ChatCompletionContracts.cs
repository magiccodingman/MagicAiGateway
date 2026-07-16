using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicAiGateway.Protocol;

public sealed record MagicChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<MagicChatMessage> Messages { get; init; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stream { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MagicToolDefinition>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ToolChoice { get; init; }

    [JsonPropertyName(MagicAiGatewayProtocol.PropertyName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicAiGatewayEnvelope? MagicAiGateway { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record MagicChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("choices")]
    public IReadOnlyList<MagicChatChoice> Choices { get; init; } = [];

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicTokenUsage? Usage { get; init; }

    [JsonPropertyName(MagicAiGatewayProtocol.PropertyName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicRunMetadata? MagicAiGateway { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record MagicChatChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("message")]
    public required MagicChatMessage Message { get; init; }

    [JsonPropertyName("finish_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record MagicChatCompletionChunk
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion.chunk";

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("choices")]
    public IReadOnlyList<MagicChatChunkChoice> Choices { get; init; } = [];

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicTokenUsage? Usage { get; init; }

    [JsonPropertyName(MagicAiGatewayProtocol.PropertyName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicRunMetadata? MagicAiGateway { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record MagicChatChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("delta")]
    public MagicChatDelta Delta { get; init; } = new();

    [JsonPropertyName("finish_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; init; }
}

public sealed record MagicChatDelta
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MagicToolCall>? ToolCalls { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record MagicTokenUsage
{
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("completion_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MagicCompletionTokenDetails? CompletionTokenDetails { get; init; }

    public static MagicTokenUsage operator +(MagicTokenUsage left, MagicTokenUsage right) => new()
    {
        PromptTokens = left.PromptTokens + right.PromptTokens,
        CompletionTokens = left.CompletionTokens + right.CompletionTokens,
        TotalTokens = left.TotalTokens + right.TotalTokens,
        CompletionTokenDetails = new MagicCompletionTokenDetails
        {
            ReasoningTokens = (left.CompletionTokenDetails?.ReasoningTokens ?? 0) +
                              (right.CompletionTokenDetails?.ReasoningTokens ?? 0)
        }
    };
}

public sealed record MagicCompletionTokenDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public long ReasoningTokens { get; init; }
}
