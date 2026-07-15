using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MagicAiGateway.Client.Protocol;

public static class MagicAiGatewayProtocol
{
    public const string PropertyName = "magic_ai_gateway";
    public const int CurrentVersion = 1;
    public const string GatewayInfoPath = "/magic-ai-gateway/v1/info";
}

public sealed record MagicAiGatewayEnvelope
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = MagicAiGatewayProtocol.CurrentVersion;

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Options { get; init; }
}

public sealed record GatewayInfo(
    string Name,
    Guid GatewayId,
    Guid ClusterId,
    int ProtocolVersion,
    int MinimumClientProtocolVersion,
    string RootCertificateBase64,
    IReadOnlyList<string> Features);

public static class MagicAiGatewayJson
{
    public static JsonObject Attach(JsonObject request, MagicAiGatewayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);

        var copy = JsonNode.Parse(request.ToJsonString())?.AsObject()
                   ?? throw new JsonException("The request could not be cloned as a JSON object.");
        copy[MagicAiGatewayProtocol.PropertyName] = JsonSerializer.SerializeToNode(envelope);
        return copy;
    }

    public static JsonObject Remove(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var copy = JsonNode.Parse(request.ToJsonString())?.AsObject()
                   ?? throw new JsonException("The request could not be cloned as a JSON object.");
        copy.Remove(MagicAiGatewayProtocol.PropertyName);
        return copy;
    }
}
