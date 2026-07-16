using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MagicAiGateway.Protocol;

/// <summary>Canonical names and endpoints for the Magic AI Gateway protocol.</summary>
public static class MagicAiGatewayProtocol
{
    public const string PropertyName = "magic_ai_gateway";
    public const int CurrentVersion = 1;
    public const int MinimumSupportedVersion = 1;
    public const string DefaultApplicationName = "MagicAiGatewayApplication";
    public const string GatewayInfoPath = "/magic-ai-gateway/v1/info";
    public const string ServicesPath = "/magic-ai-gateway/v1/services";
}

public static class MagicServiceNames
{
    public const string ManagedTools = "managed_tools";
}

public static class MagicRunStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

public static class MagicUsageAccuracy
{
    public const string ProviderReported = "provider_reported";
    public const string GatewayCounted = "gateway_counted";
    public const string Estimated = "estimated";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
}

public static class MagicResponseModes
{
    public const string Compatible = "compatible";
    public const string Enriched = "enriched";
}

public sealed record MagicAiGatewayEnvelope
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = MagicAiGatewayProtocol.CurrentVersion;

    [JsonPropertyName("application")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Application { get; init; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Agent { get; init; }

    [JsonPropertyName("service")]
    public required MagicServiceInvocation Service { get; init; }

    [JsonPropertyName("requested_run_timeout_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestedRunTimeoutSeconds { get; init; }

    [JsonPropertyName("response_mode")]
    public string ResponseMode { get; init; } = MagicResponseModes.Compatible;

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record MagicServiceInvocation
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Options { get; init; }

    public static MagicServiceInvocation Create<TOptions>(string name, TOptions options, int version = 1) => new()
    {
        Name = name,
        Version = version,
        Options = JsonSerializer.SerializeToElement(options, MagicProtocolJson.Options)
    };
}

public sealed record GatewayInfo(
    string Name,
    Guid GatewayId,
    Guid ClusterId,
    int ProtocolVersion,
    int MinimumClientProtocolVersion,
    string RootCertificateBase64,
    IReadOnlyList<string> Features);

public static class MagicProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static JsonObject Attach(JsonObject request, MagicAiGatewayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);

        var copy = JsonNode.Parse(request.ToJsonString())?.AsObject()
                   ?? throw new JsonException("The request could not be cloned as a JSON object.");
        copy[MagicAiGatewayProtocol.PropertyName] = JsonSerializer.SerializeToNode(envelope, Options);
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
