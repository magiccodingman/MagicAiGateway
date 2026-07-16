using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MagicAiGateway.Client.Protocol;

/// <summary>
/// Compatibility facade for callers compiled against the original client namespace.
/// New code should use MagicAiGateway.Protocol directly.
/// </summary>
[Obsolete("Use MagicAiGateway.Protocol.MagicAiGatewayProtocol.")]
public static class MagicAiGatewayProtocol
{
    public const string PropertyName = global::MagicAiGateway.Protocol.MagicAiGatewayProtocol.PropertyName;
    public const int CurrentVersion = global::MagicAiGateway.Protocol.MagicAiGatewayProtocol.CurrentVersion;
    public const string GatewayInfoPath = global::MagicAiGateway.Protocol.MagicAiGatewayProtocol.GatewayInfoPath;
    public const string ServicesPath = global::MagicAiGateway.Protocol.MagicAiGatewayProtocol.ServicesPath;
}

[Obsolete("Use MagicAiGateway.Protocol.MagicAiGatewayEnvelope.")]
public sealed record MagicAiGatewayEnvelope
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = global::MagicAiGateway.Protocol.MagicAiGatewayProtocol.CurrentVersion;

    [JsonPropertyName("application")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Application { get; init; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Agent { get; init; }

    [JsonPropertyName("service")]
    public required global::MagicAiGateway.Protocol.MagicServiceInvocation Service { get; init; }

    [JsonPropertyName("requested_run_timeout_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestedRunTimeoutSeconds { get; init; }

    [JsonPropertyName("response_mode")]
    public string ResponseMode { get; init; } = global::MagicAiGateway.Protocol.MagicResponseModes.Compatible;
}

[Obsolete("Use MagicAiGateway.Protocol.GatewayInfo.")]
public sealed record GatewayInfo(
    string Name,
    Guid GatewayId,
    Guid ClusterId,
    int ProtocolVersion,
    int MinimumClientProtocolVersion,
    string RootCertificateBase64,
    IReadOnlyList<string> Features);

[Obsolete("Use MagicAiGateway.Protocol.MagicProtocolJson.")]
public static class MagicAiGatewayJson
{
    public static JsonObject Attach(JsonObject request, MagicAiGatewayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);
        var copy = JsonNode.Parse(request.ToJsonString())?.AsObject()
                   ?? throw new JsonException("The request could not be cloned as a JSON object.");
        copy[global::MagicAiGateway.Protocol.MagicAiGatewayProtocol.PropertyName] =
            JsonSerializer.SerializeToNode(envelope, global::MagicAiGateway.Protocol.MagicProtocolJson.Options);
        return copy;
    }

    public static JsonObject Remove(JsonObject request) =>
        global::MagicAiGateway.Protocol.MagicProtocolJson.Remove(request);
}
