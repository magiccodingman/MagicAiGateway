using System.Buffers;
using System.Text;
using System.Text.Json;
using MagicAiGateway.Protocol;

namespace SharedMagic.Proxy;

public sealed record OpenAiRequestInspection(
    string? Model,
    bool Stream,
    bool HasMagicGatewayObject,
    bool HasNullMagicGateway,
    bool LooksLikeOpenAiEnvelope,
    JsonElement? MagicGatewayEnvelope = null,
    bool HasInvalidMagicGateway = false);

public static class OpenAiRequestInspector
{
    public static async ValueTask<OpenAiRequestInspection> InspectAsync(
        Stream body,
        int maximumBytes = 64 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (!body.CanSeek)
        {
            throw new InvalidOperationException("The request body must be buffered before inspection.");
        }

        var originalPosition = body.Position;
        try
        {
            using var memory = new MemoryStream();
            var rented = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                var total = 0;
                int read;
                while ((read = await body.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maximumBytes)
                    {
                        return new(null, false, false, false, false);
                    }

                    await memory.WriteAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            if (memory.Length == 0)
            {
                return new(null, false, false, false, false);
            }

            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(memory.GetBuffer(), 0, checked((int)memory.Length)));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new(null, false, false, false, false);
            }

            var root = document.RootElement;
            var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : null;
            var stream = root.TryGetProperty("stream", out var streamElement) && streamElement.ValueKind == JsonValueKind.True;
            var hasMagic = root.TryGetProperty(MagicAiGatewayProtocol.PropertyName, out var magicElement);
            var magicObject = hasMagic && magicElement.ValueKind == JsonValueKind.Object;
            var magicNull = hasMagic && magicElement.ValueKind == JsonValueKind.Null;
            var magicInvalid = hasMagic && !magicObject && !magicNull;
            var looksOpenAi = model is not null &&
                (root.ContainsProperty("messages") || root.ContainsProperty("input") || root.ContainsProperty("prompt") || root.ContainsProperty("tools"));

            return new(model, stream, magicObject, magicNull, looksOpenAi,
                magicObject ? magicElement.Clone() : null,
                magicInvalid);
        }
        catch (JsonException)
        {
            return new(null, false, false, false, false);
        }
        finally
        {
            body.Position = originalPosition;
        }
    }

    public static async ValueTask<byte[]?> RemoveNullMagicGatewayAsync(
        Stream body,
        int maximumBytes = 64 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (!body.CanSeek)
        {
            throw new InvalidOperationException("The request body must be buffered before rewriting.");
        }

        var originalPosition = body.Position;
        try
        {
            using var source = new MemoryStream();
            await body.CopyToAsync(source, cancellationToken).ConfigureAwait(false);
            if (source.Length == 0 || source.Length > maximumBytes) return null;

            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(source.GetBuffer(), 0, checked((int)source.Length)));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(MagicAiGatewayProtocol.PropertyName, out var magic) ||
                magic.ValueKind != JsonValueKind.Null)
            {
                return null;
            }

            using var destination = new MemoryStream();
            using (var writer = new Utf8JsonWriter(destination))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals(MagicAiGatewayProtocol.PropertyName)) continue;
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            return destination.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            body.Position = originalPosition;
        }
    }

    private static bool ContainsProperty(this JsonElement element, string name) => element.TryGetProperty(name, out _);
}

public sealed record DetectedToolCall(string? Id, string Name, string Arguments, int ChoiceIndex, int ToolIndex);

public sealed record MagicToolExecutionContext(string Model, Guid RequestId);
public sealed record MagicToolResult(bool Success, JsonElement? Output = null, string? Error = null);

public interface IMagicToolDefinition
{
    string Name { get; }
    ValueTask<MagicToolResult> ExecuteAsync(
        JsonElement arguments,
        MagicToolExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IMagicToolRegistry
{
    IReadOnlyCollection<IMagicToolDefinition> Tools { get; }
    bool Contains(string toolName);
    bool TryGet(string toolName, out IMagicToolDefinition? tool);
}

public sealed class EmptyMagicToolRegistry : IMagicToolRegistry
{
    public IReadOnlyCollection<IMagicToolDefinition> Tools => [];
    public bool Contains(string toolName) => false;

    public bool TryGet(string toolName, out IMagicToolDefinition? tool)
    {
        tool = null;
        return false;
    }
}

public sealed record ModelResponseObservation(
    bool ContainsToolCalls,
    bool ContainsGatewayOwnedToolCalls,
    IReadOnlyList<DetectedToolCall> ToolCalls);

public sealed class StreamingToolCallAccumulator
{
    private sealed class Builder
    {
        public string? Id;
        public string Name = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }

    private readonly Dictionary<(int Choice, int Tool), Builder> _builders = [];
    private readonly Dictionary<string, Builder> _responsesCalls = new(StringComparer.Ordinal);

    public void ObserveJson(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            Observe(document.RootElement);
        }
        catch (JsonException)
        {
            // A streaming fragment is allowed to be incomplete; the original bytes still pass through untouched.
        }
    }

    public void Observe(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                var choiceIndex = choice.TryGetProperty("index", out var choiceIndexElement) && choiceIndexElement.TryGetInt32(out var parsedChoice)
                    ? parsedChoice
                    : 0;

                JsonElement calls = default;
                var found = false;
                if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("tool_calls", out calls))
                {
                    found = true;
                }
                else if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("tool_calls", out calls))
                {
                    found = true;
                }

                if (!found || calls.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var fallbackIndex = 0;
                foreach (var call in calls.EnumerateArray())
                {
                    var toolIndex = call.TryGetProperty("index", out var toolIndexElement) && toolIndexElement.TryGetInt32(out var parsedTool)
                        ? parsedTool
                        : fallbackIndex;
                    fallbackIndex++;
                    var builder = GetBuilder(choiceIndex, toolIndex);
                    if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        builder.Id ??= id.GetString();
                    }

                    if (call.TryGetProperty("function", out var function))
                    {
                        if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            builder.Name += name.GetString();
                        }

                        if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                        {
                            builder.Arguments.Append(arguments.GetString());
                        }
                    }
                }
            }
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray()) ObserveResponseOutputItem(item);
        }

        ObserveResponsesApi(root);
    }

    private void ObserveResponsesApi(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var type = typeElement.GetString();
        if (type == "response.output_item.added" && root.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var itemType) && itemType.GetString() == "function_call")
        {
            var id = item.TryGetProperty("call_id", out var callId) ? callId.GetString() : null;
            var key = id ?? Guid.NewGuid().ToString("N");
            var builder = _responsesCalls.GetValueOrDefault(key) ?? new Builder { Id = id };
            if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                builder.Name = name.GetString() ?? string.Empty;
            }
            _responsesCalls[key] = builder;
        }
        else if (type == "response.function_call_arguments.delta" &&
                 root.TryGetProperty("call_id", out var callId) && callId.ValueKind == JsonValueKind.String)
        {
            var key = callId.GetString()!;
            var builder = _responsesCalls.GetValueOrDefault(key) ?? new Builder { Id = key };
            if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
            {
                builder.Arguments.Append(delta.GetString());
            }
            _responsesCalls[key] = builder;
        }
        else if (type == "response.output_item.done" && root.TryGetProperty("item", out var completedItem))
        {
            ObserveResponseOutputItem(completedItem);
        }
    }

    private void ObserveResponseOutputItem(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var itemType) || itemType.GetString() != "function_call") return;
        var id = item.TryGetProperty("call_id", out var callId) && callId.ValueKind == JsonValueKind.String
            ? callId.GetString()
            : item.TryGetProperty("id", out var itemId) && itemId.ValueKind == JsonValueKind.String
                ? itemId.GetString()
                : null;
        var key = id ?? Guid.NewGuid().ToString("N");
        var builder = _responsesCalls.GetValueOrDefault(key) ?? new Builder { Id = id };
        if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            builder.Name = name.GetString() ?? string.Empty;
        }
        if (item.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
        {
            builder.Arguments.Clear();
            builder.Arguments.Append(arguments.GetString());
        }
        _responsesCalls[key] = builder;
    }

    public IReadOnlyList<DetectedToolCall> GetToolCalls()
    {
        var chatCalls = _builders
            .OrderBy(static x => x.Key.Choice)
            .ThenBy(static x => x.Key.Tool)
            .Where(static x => !string.IsNullOrWhiteSpace(x.Value.Name))
            .Select(static x => new DetectedToolCall(x.Value.Id, x.Value.Name, x.Value.Arguments.ToString(), x.Key.Choice, x.Key.Tool));

        var responseCalls = _responsesCalls.Values
            .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(static (x, index) => new DetectedToolCall(x.Id, x.Name, x.Arguments.ToString(), 0, index));

        return chatCalls.Concat(responseCalls).ToArray();
    }

    private Builder GetBuilder(int choice, int tool)
    {
        var key = (choice, tool);
        if (!_builders.TryGetValue(key, out var builder))
        {
            builder = new Builder();
            _builders[key] = builder;
        }

        return builder;
    }
}

public static class OpenAiToolCallParser
{
    public static IReadOnlyList<DetectedToolCall> Parse(ReadOnlySpan<byte> utf8Json)
    {
        var accumulator = new StreamingToolCallAccumulator();
        accumulator.ObserveJson(utf8Json);
        return accumulator.GetToolCalls();
    }
}
