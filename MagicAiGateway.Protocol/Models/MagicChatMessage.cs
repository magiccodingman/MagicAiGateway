using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicAiGateway.Protocol;

/// <summary>One OpenAI-compatible message inside a chat-completions request.</summary>
public sealed record MagicChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MagicToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }

    public static MagicChatMessage System(string content) => FromText("system", content);
    public static MagicChatMessage User(string content) => FromText("user", content);
    public static MagicChatMessage Assistant(string? content) => new()
    {
        Role = "assistant",
        Content = content is null ? null : JsonSerializer.SerializeToElement(content, MagicProtocolJson.Options)
    };

    public static MagicChatMessage Tool(string toolCallId, string content) => new()
    {
        Role = "tool",
        ToolCallId = toolCallId,
        Content = JsonSerializer.SerializeToElement(content, MagicProtocolJson.Options)
    };

    private static MagicChatMessage FromText(string role, string content) => new()
    {
        Role = role,
        Content = JsonSerializer.SerializeToElement(content, MagicProtocolJson.Options)
    };
}

public sealed record MagicToolCall
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required MagicFunctionCall Function { get; init; }
}

public sealed record MagicFunctionCall
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "{}";
}

public sealed record MagicToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required MagicFunctionDefinition Function { get; init; }
}

public sealed record MagicFunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { type = "object" });
}
