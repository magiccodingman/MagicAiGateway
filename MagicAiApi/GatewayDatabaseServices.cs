using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MagicAiGateway.DB.Client.Connection;
using MagicAiGateway.DB.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Security;

namespace MagicAiApi;

public static class FabricPeerRoles
{
    public const string Node = "node";
    public const string DatabaseApi = "database-api";
}

public sealed record GatewayFabricPeer(
    Guid PeerId,
    string Role,
    string? ApplicationId,
    DateTimeOffset RecordedAt);

public sealed class GatewayFabricPeerRegistry
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<Guid, GatewayFabricPeer> _peers;

    public GatewayFabricPeerRegistry(
        IHostEnvironment environment,
        IOptions<FabricSecurityOptions> options)
    {
        var directory = FabricStateFiles.ResolveDirectory(
            options.Value.StateDirectory,
            environment.ContentRootPath);
        _path = Path.Combine(directory, "fabric-peer-roles.json");
        _peers = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<Guid, GatewayFabricPeer>>(
                  File.ReadAllText(_path)) ?? []
            : [];
    }

    public void Record(Guid peerId, string role, string? applicationId)
    {
        lock (_sync)
        {
            _peers[peerId] = new GatewayFabricPeer(
                peerId,
                role,
                applicationId,
                DateTimeOffset.UtcNow);
            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(_peers, new JsonSerializerOptions { WriteIndented = true }));
            FabricStateFiles.TryRestrictFile(_path);
        }
    }

    public GatewayFabricPeer? Find(Guid peerId)
    {
        lock (_sync)
        {
            return _peers.GetValueOrDefault(peerId);
        }
    }
}

public sealed record FabricPeerRoleRequirement(string Role) : IAuthorizationRequirement;

public sealed class FabricPeerRoleAuthorizationHandler(
    GatewayFabricPeerRegistry peers,
    GatewayPairingRegistry pairing) : AuthorizationHandler<FabricPeerRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FabricPeerRoleRequirement requirement)
    {
        var value = context.User.FindFirst(FabricAuthenticationDefaults.PeerIdClaim)?.Value;
        if (!Guid.TryParse(value, out var peerId)) return Task.CompletedTask;

        var recorded = peers.Find(peerId);
        var role = recorded?.Role;
        if (role is null && pairing.IsApproved(peerId)) role = FabricPeerRoles.Node;
        if (string.Equals(role, requirement.Role, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

public static class GatewayFabricPolicies
{
    public const string Node = "MagicFabric.Node";
    public const string DatabaseApi = "MagicFabric.DatabaseApi";

    public static void AddPolicies(AuthorizationOptions options)
    {
        Add(options, Node, FabricPeerRoles.Node);
        Add(options, DatabaseApi, FabricPeerRoles.DatabaseApi);
    }

    private static void Add(AuthorizationOptions options, string name, string role)
    {
        options.AddPolicy(name, policy =>
        {
            policy.AddAuthenticationSchemes(FabricAuthenticationDefaults.Scheme);
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new FabricPeerRoleRequirement(role));
        });
    }
}

public sealed class GatewayDatabaseServiceOptions
{
    public const string SectionName = "DatabaseApi";
    public string? ApiKey { get; set; }
    public Uri? EndpointOverride { get; set; }
    public List<string> StaticEndpoints { get; set; } = [];
    public Guid? StaticPeerId { get; set; }
    public string? StaticRootCertificateBase64 { get; set; }
    public int RefreshSeconds { get; set; } = 15;
    public int LeaseSeconds { get; set; } = 20;
}

public sealed class GatewayFabricServiceRegistry(
    IOptions<GatewayOptions> gatewayOptions,
    IOptions<GatewayDatabaseServiceOptions> databaseOptions,
    GatewayCertificateAuthority authority)
{
    private readonly ConcurrentDictionary<(string Service, Guid Instance), FabricServiceDescriptor> _services = [];

    public void Update(FabricServiceHeartbeat heartbeat)
    {
        if (!string.Equals(
                heartbeat.GatewayName,
                gatewayOptions.Value.Name,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The service heartbeat targets a different gateway name.");
        }
        if (heartbeat.Endpoints.Count == 0)
        {
            throw new InvalidOperationException("A fabric service must advertise at least one endpoint.");
        }
        foreach (var endpoint in heartbeat.Endpoints)
        {
            if (!Uri.TryCreate(endpoint.BaseUri, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            {
                throw new InvalidOperationException(
                    "Fabric services must advertise HTTPS endpoints unless they are loopback.");
            }
        }

        var lastSeen = DateTimeOffset.UtcNow;
        _services[(heartbeat.ServiceName, heartbeat.InstanceId)] = new FabricServiceDescriptor(
            heartbeat.PeerId,
            heartbeat.InstanceId,
            heartbeat.ApplicationId,
            heartbeat.ServiceName,
            heartbeat.GatewayName,
            heartbeat.Endpoints,
            heartbeat.Version,
            heartbeat.Health,
            lastSeen,
            lastSeen.AddSeconds(Math.Max(5, databaseOptions.Value.LeaseSeconds)),
            Dynamic: true,
            Convert.ToBase64String(authority.RootCertificate.Export(X509ContentType.Cert)));
    }

    public FabricServiceDescriptor? Find(string serviceName)
    {
        Expire();
        var dynamic = _services.Values
            .Where(service =>
                string.Equals(service.ServiceName, serviceName, StringComparison.Ordinal) &&
                service.LeaseExpiresAt > DateTimeOffset.UtcNow)
            .OrderBy(service => service.Health switch
            {
                FabricServiceHealth.Ready => 0,
                FabricServiceHealth.Degraded => 1,
                FabricServiceHealth.Starting => 2,
                _ => 3
            })
            .ThenByDescending(service => service.LastSeenAt)
            .FirstOrDefault();
        if (dynamic is not null) return dynamic;

        if (!string.Equals(serviceName, MagicFabricServices.Database, StringComparison.Ordinal) ||
            databaseOptions.Value.StaticEndpoints.Count == 0)
        {
            return null;
        }

        var endpoints = databaseOptions.Value.StaticEndpoints
            .Select((value, index) => ToStaticEndpoint(value, index))
            .ToArray();
        return new FabricServiceDescriptor(
            databaseOptions.Value.StaticPeerId ?? Guid.Empty,
            databaseOptions.Value.StaticPeerId ?? Guid.Empty,
            MagicApplication.DatabaseApi.ToString(),
            MagicFabricServices.Database,
            gatewayOptions.Value.Name,
            endpoints,
            "configured",
            FabricServiceHealth.Ready,
            DateTimeOffset.UtcNow,
            DateTimeOffset.MaxValue,
            Dynamic: false,
            databaseOptions.Value.StaticRootCertificateBase64);
    }

    public void Expire()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _services)
        {
            if (pair.Value.LeaseExpiresAt <= now)
            {
                _services.TryRemove(pair.Key, out _);
            }
        }
    }

    private static FabricServiceEndpoint ToStaticEndpoint(string value, int priority)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new InvalidOperationException(
                "Configured non-loopback database endpoints must use HTTPS.");
        }
        return new FabricServiceEndpoint(
            uri.ToString().TrimEnd('/'),
            uri.IsLoopback ? FabricEndpointScope.Loopback : FabricEndpointScope.Public,
            priority);
    }
}

public sealed class FabricServiceLeaseMonitorService(
    GatewayFabricServiceRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            registry.Expire();
        }
    }
}

public sealed class LocalGatewayDatabaseApiEndpointResolver(
    GatewayFabricServiceRegistry registry,
    IOptions<GatewayDatabaseServiceOptions> options) : IDatabaseApiEndpointResolver
{
    public Task<DatabaseApiEndpointSnapshot> ResolveAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Value.EndpointOverride is { } endpointOverride)
        {
            return Task.FromResult(new DatabaseApiEndpointSnapshot(
                Normalize(endpointOverride),
                options.Value.StaticPeerId,
                options.Value.StaticRootCertificateBase64,
                DateTimeOffset.MaxValue,
                null));
        }

        var descriptor = registry.Find(MagicFabricServices.Database)
                         ?? throw new InvalidOperationException(
                             "No healthy database API is registered or statically configured.");
        var endpoint = descriptor.Endpoints
            .OrderBy(item => item.Priority)
            .Select(item => new Uri(item.BaseUri, UriKind.Absolute))
            .First();
        return Task.FromResult(new DatabaseApiEndpointSnapshot(
            Normalize(endpoint),
            descriptor.PeerId == Guid.Empty ? null : descriptor.PeerId,
            descriptor.RootCertificateBase64,
            descriptor.LeaseExpiresAt,
            descriptor));
    }

    public void Invalidate()
    {
        // The in-process registry is already the authoritative live view.
    }

    private static Uri Normalize(Uri uri) => new(uri.ToString().TrimEnd('/') + "/");
}
