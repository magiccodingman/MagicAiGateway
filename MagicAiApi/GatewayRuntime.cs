using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Contracts;
using SharedMagic.Routing;
using SharedMagic.Security;

namespace MagicAiApi;

public sealed record GatewayNodeTarget(Guid NodeId, string Name, string BaseUri, bool IsHealthy) : IRouteTarget
{
    public string Id => NodeId.ToString("N");
}

public sealed class GatewayNodeRegistry
{
    private readonly ConcurrentDictionary<Guid, NodeSnapshot> _nodes = [];
    private readonly IRequestScheduler<GatewayNodeTarget> _scheduler;
    private readonly GatewayOptions _options;
    private readonly object _routeSync = new();
    private HashSet<string> _knownModels = new(StringComparer.Ordinal);

    public GatewayNodeRegistry(IRequestScheduler<GatewayNodeTarget> scheduler, IOptions<GatewayOptions> options)
    {
        _scheduler = scheduler;
        _options = options.Value;
    }

    public IReadOnlyCollection<NodeSnapshot> GetNodes() => _nodes.Values.OrderBy(static x => x.Name).ToArray();

    public IReadOnlyCollection<ModelDescriptor> GetModels() => _nodes.Values
        .Where(static x => x.Online)
        .SelectMany(static x => x.Backends)
        .Where(static x => x.Healthy)
        .SelectMany(static x => x.Models)
        .GroupBy(static x => x.Id, StringComparer.Ordinal)
        .Select(static x => x.First() with { BackendId = null })
        .OrderBy(static x => x.Id, StringComparer.Ordinal)
        .ToArray();

    public void Update(NodeHeartbeat heartbeat)
    {
        if (!Uri.TryCreate(heartbeat.BaseUri, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new InvalidOperationException("Nodes must advertise an HTTPS URI unless they are using loopback.");
        }

        _nodes[heartbeat.NodeId] = new NodeSnapshot(
            heartbeat.NodeId,
            heartbeat.Name,
            uri.ToString().TrimEnd('/'),
            true,
            DateTimeOffset.UtcNow,
            heartbeat.Backends);
        RebuildRoutes();
    }

    public void ExpireStaleNodes()
    {
        var threshold = DateTimeOffset.UtcNow.AddSeconds(-_options.HeartbeatLeaseSeconds);
        foreach (var pair in _nodes)
        {
            if (pair.Value.Online && pair.Value.LastSeenAt < threshold)
            {
                _nodes[pair.Key] = pair.Value with { Online = false };
            }
        }
        RebuildRoutes();
    }

    public void MarkOffline(Guid nodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            _nodes[nodeId] = node with { Online = false };
            RebuildRoutes();
        }
    }

    public GatewayNodeTarget? FindAnyForModel(string model) => _nodes.Values
        .Where(x => x.Online && x.Backends.Any(b => b.Healthy && b.Models.Any(m => string.Equals(m.Id, model, StringComparison.Ordinal))))
        .Select(static x => new GatewayNodeTarget(x.NodeId, x.Name, x.BaseUri, true))
        .FirstOrDefault();

    private void RebuildRoutes()
    {
        lock (_routeSync)
        {
            var currentModels = _nodes.Values
                .SelectMany(static n => n.Backends)
                .SelectMany(static b => b.Models)
                .Select(static m => m.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var model in _knownModels.Union(currentModels, StringComparer.Ordinal))
            {
                var targets = _nodes.Values
                    .Where(n => n.Online && n.Backends.Any(b => b.Healthy && b.Models.Any(m => string.Equals(m.Id, model, StringComparison.Ordinal))))
                    .Select(static n => new GatewayNodeTarget(n.NodeId, n.Name, n.BaseUri, true));
                _scheduler.ReplaceTargets(model, targets);
            }
            _knownModels = currentModels;
        }
    }
}

public sealed class NodeLeaseMonitorService(GatewayNodeRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) registry.ExpireStaleNodes();
    }
}


public sealed class StaticNodeMonitorService(
    IOptions<GatewayOptions> options,
    GatewayNodeRegistry registry,
    GatewayNodeClient client,
    ILogger<StaticNodeMonitorService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = options.Value.StaticNodes
            .Where(static x => x.Enabled && x.NodeId != Guid.Empty)
            .Select(node => MonitorAsync(node, stoppingToken));
        return Task.WhenAll(tasks);
    }

    private async Task MonitorAsync(StaticNodeOptions node, CancellationToken cancellationToken)
    {
        var target = new GatewayNodeTarget(node.NodeId, node.Name, node.BaseUri.TrimEnd('/'), true);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync(target, "/internal/v1/status", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var backends = await response.Content.ReadFromJsonAsync<IReadOnlyList<BackendSnapshot>>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
                registry.Update(new NodeHeartbeat(node.NodeId, node.Name, target.BaseUri, DateTimeOffset.UtcNow, backends));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                registry.MarkOffline(node.NodeId);
                logger.LogDebug(exception, "Static node {NodeId} is unavailable at {BaseUri}.", node.NodeId, node.BaseUri);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, node.PollSeconds)), cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class GatewayFabricHub(GatewayNodeRegistry registry) : Hub
{
    public Task Heartbeat(NodeHeartbeat heartbeat)
    {
        var certificateId = Context.User?.FindFirst(FabricAuthenticationDefaults.PeerIdClaim)?.Value;
        if (Guid.TryParse(certificateId, out var peerId) && peerId != heartbeat.NodeId)
        {
            throw new HubException("The heartbeat node ID does not match the authenticated certificate.");
        }
        registry.Update(heartbeat);
        return Task.CompletedTask;
    }
}

public sealed class PairingChallengeStore(GatewayCertificateAuthority authority, IOptions<GatewayOptions> options)
{
    private sealed record Challenge(Guid NodeId, byte[] Nonce, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<Guid, Challenge> _challenges = [];

    public PairingChallengeResponse Create(Guid nodeId)
    {
        RemoveExpired();
        var challengeId = Guid.NewGuid();
        var challenge = new Challenge(nodeId, RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(2));
        _challenges[challengeId] = challenge;
        return new PairingChallengeResponse(
            challengeId,
            Convert.ToBase64String(challenge.Nonce),
            authority.Identity.InstanceId,
            authority.Identity.ClusterId,
            options.Value.Name,
            challenge.ExpiresAt);
    }

    public bool Validate(Guid nodeId, Guid? challengeId, string? proofBase64, string? enrollmentToken)
    {
        if (challengeId is null || string.IsNullOrWhiteSpace(proofBase64) || string.IsNullOrWhiteSpace(enrollmentToken)) return false;
        if (!_challenges.TryRemove(challengeId.Value, out var challenge) || challenge.NodeId != nodeId || challenge.ExpiresAt < DateTimeOffset.UtcNow) return false;

        byte[] supplied;
        try { supplied = Convert.FromBase64String(proofBase64); }
        catch (FormatException) { return false; }

        var expected = PairingProof.Compute(
            enrollmentToken,
            challengeId.Value,
            challenge.Nonce,
            nodeId,
            authority.Identity.InstanceId,
            authority.Identity.ClusterId,
            options.Value.Name);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _challenges)
        {
            if (pair.Value.ExpiresAt < now) _challenges.TryRemove(pair.Key, out _);
        }
    }
}

public sealed record PendingNode(Guid NodeId, string Name, string AdvertisedBaseUri, DateTimeOffset RequestedAt, bool Approved);

public sealed class GatewayPairingRegistry
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<Guid, PendingNode> _nodes;

    public GatewayPairingRegistry(GatewayCertificateAuthority authority, IHostEnvironment environment, IOptions<FabricSecurityOptions> options)
    {
        var directory = FabricStateFiles.ResolveDirectory(options.Value.StateDirectory, environment.ContentRootPath);
        _path = Path.Combine(directory, "peers.json");
        _nodes = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<Guid, PendingNode>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public bool IsApproved(Guid nodeId)
    {
        lock (_sync) return _nodes.TryGetValue(nodeId, out var node) && node.Approved;
    }

    public PendingNode AddOrUpdatePending(PairingRequest request)
    {
        lock (_sync)
        {
            if (_nodes.TryGetValue(request.NodeId, out var current) && current.Approved) return current;
            var pending = new PendingNode(request.NodeId, request.NodeName, request.AdvertisedBaseUri, DateTimeOffset.UtcNow, false);
            _nodes[request.NodeId] = pending;
            Save();
            return pending;
        }
    }

    public bool Approve(Guid nodeId)
    {
        lock (_sync)
        {
            if (!_nodes.TryGetValue(nodeId, out var node)) return false;
            _nodes[nodeId] = node with { Approved = true };
            Save();
            return true;
        }
    }

    public IReadOnlyCollection<PendingNode> GetAll()
    {
        lock (_sync) return _nodes.Values.OrderByDescending(static x => x.RequestedAt).ToArray();
    }

    private void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_nodes, new JsonSerializerOptions { WriteIndented = true }));
        FabricStateFiles.TryRestrictFile(_path);
    }
}

public sealed class GatewayPeerTrustProvider(
    GatewayCertificateAuthority authority,
    GatewayPairingRegistry registry,
    IOptions<FabricSecurityOptions> options) : IFabricPeerTrustProvider
{
    public X509Certificate2 RootCertificate => authority.RootCertificate;
    public string ExpectedPeerRole => "node";
    public string? LoopbackToken => options.Value.LoopbackToken;
    public bool AllowInsecureLoopback => options.Value.AllowInsecureLoopback;
    public ValueTask<bool> IsPeerAllowedAsync(Guid peerId, CancellationToken cancellationToken) => ValueTask.FromResult(registry.IsApproved(peerId));
}
