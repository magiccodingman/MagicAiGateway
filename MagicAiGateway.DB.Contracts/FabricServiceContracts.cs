namespace MagicAiGateway.DB.Contracts;

public enum FabricEndpointScope
{
    Loopback = 0,
    Lan = 1,
    Public = 2
}

public enum FabricServiceHealth
{
    Starting = 0,
    Ready = 1,
    Degraded = 2,
    Unavailable = 3
}

public sealed record FabricServiceEndpoint(
    string BaseUri,
    FabricEndpointScope Scope,
    int Priority = 0);

public sealed record FabricServiceHeartbeat(
    Guid PeerId,
    Guid InstanceId,
    string ApplicationId,
    string ServiceName,
    string GatewayName,
    IReadOnlyList<FabricServiceEndpoint> Endpoints,
    string Version,
    FabricServiceHealth Health,
    DateTimeOffset SentAt);

public sealed record FabricServiceDescriptor(
    Guid PeerId,
    Guid InstanceId,
    string ApplicationId,
    string ServiceName,
    string GatewayName,
    IReadOnlyList<FabricServiceEndpoint> Endpoints,
    string Version,
    FabricServiceHealth Health,
    DateTimeOffset LastSeenAt,
    DateTimeOffset LeaseExpiresAt,
    bool Dynamic,
    string? RootCertificateBase64 = null);

public static class MagicFabricServices
{
    public const string Database = "database";
}
