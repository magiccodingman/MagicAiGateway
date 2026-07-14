using System.Net.Http.Json;
using System.Threading.Channels;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Contracts;
using SharedMagic.Discovery;
using SharedMagic.Security;

namespace MagicAiNode;

public sealed record NodePairingState(Guid GatewayId, Guid ClusterId, string GatewayName, string GatewayBaseUri);

public sealed class NodePairingStateStore
{
    private readonly string _path;
    private readonly object _sync = new();

    public NodePairingStateStore(string directory) => _path = Path.Combine(directory, "gateway.json");

    public NodePairingState? Load()
    {
        lock (_sync)
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<NodePairingState>(File.ReadAllText(_path)) : null;
        }
    }

    public void Save(NodePairingState state)
    {
        lock (_sync)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            FabricStateFiles.TryRestrictFile(_path);
        }
    }
}

public sealed class NodePeerTrustProvider(
    NodeCertificateStore certificates,
    NodePairingStateStore pairingState,
    IOptions<FabricSecurityOptions> options) : IFabricPeerTrustProvider
{
    public X509Certificate2? RootCertificate => certificates.RootCertificate;
    public string ExpectedPeerRole => "gateway";
    public string? LoopbackToken => options.Value.LoopbackToken;
    public bool AllowInsecureLoopback => options.Value.AllowInsecureLoopback;

    public ValueTask<bool> IsPeerAllowedAsync(Guid peerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(pairingState.Load()?.GatewayId == peerId);
}

public sealed class GatewayConnectionService(
    IOptions<NodeOptions> nodeOptions,
    IOptions<FabricSecurityOptions> securityOptions,
    IOptions<DiscoveryOptions> discoveryOptions,
    NodeCertificateStore certificates,
    NodePairingStateStore pairingState,
    BackendCatalog backends,
    ILoggerFactory loggerFactory,
    ILogger<GatewayConnectionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MdnsBrowser? browser = null;
        var candidates = Channel.CreateUnbounded<Uri>();
        foreach (var configured in nodeOptions.Value.StaticGateways)
        {
            if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)) candidates.Writer.TryWrite(uri);
        }

        if (discoveryOptions.Value.Enabled)
        {
            browser = new MdnsBrowser(discoveryOptions.Value.GatewayServiceType, nodeOptions.Value.GatewayName, loggerFactory.CreateLogger<MdnsBrowser>());
            browser.ServiceDiscovered += (_, service) => candidates.Writer.TryWrite(service.ToHttpsUri());
            browser.Start();
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var state = pairingState.Load();
                if (state is null || !certificates.IsPaired)
                {
                    var candidate = await candidates.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                    try
                    {
                        state = await TryPairAsync(candidate, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Pairing with gateway candidate {Gateway} failed.", candidate);
                        state = null;
                    }
                    if (state is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                        candidates.Writer.TryWrite(candidate);
                        continue;
                    }
                }

                try
                {
                    await RunHubConnectionAsync(state, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "The gateway control connection failed; reconnecting.");
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            browser?.Dispose();
        }
    }

    private async Task<NodePairingState?> TryPairAsync(Uri gateway, CancellationToken cancellationToken)
    {
        if (!string.Equals(gateway.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !gateway.IsLoopback)
        {
            logger.LogWarning("Ignoring insecure non-loopback gateway candidate {Gateway}.", gateway);
            return null;
        }

        using var handler = new HttpClientHandler();
        if (securityOptions.Value.PairingServerCertificateMode == PairingServerCertificateMode.TrustOnFirstUse)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        using var client = new HttpClient(handler) { BaseAddress = new Uri(gateway.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        Guid? challengeId = null;
        string? enrollmentProof = null;
        if (!string.IsNullOrWhiteSpace(securityOptions.Value.EnrollmentToken))
        {
            using var challengeResponse = await client.PostAsJsonAsync(
                "fabric/v1/pair/challenge",
                new PairingChallengeRequest(certificates.Identity.InstanceId, nodeOptions.Value.GatewayName),
                cancellationToken).ConfigureAwait(false);
            if (challengeResponse.IsSuccessStatusCode)
            {
                var challenge = await challengeResponse.Content.ReadFromJsonAsync<PairingChallengeResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (challenge is not null && string.Equals(challenge.GatewayName, nodeOptions.Value.GatewayName, StringComparison.Ordinal))
                {
                    var nonce = Convert.FromBase64String(challenge.NonceBase64);
                    var proof = PairingProof.Compute(
                        securityOptions.Value.EnrollmentToken,
                        challenge.ChallengeId,
                        nonce,
                        certificates.Identity.InstanceId,
                        challenge.GatewayId,
                        challenge.ClusterId,
                        challenge.GatewayName);
                    challengeId = challenge.ChallengeId;
                    enrollmentProof = Convert.ToBase64String(proof);
                }
            }
        }

        var request = new PairingRequest(
            certificates.Identity.InstanceId,
            nodeOptions.Value.Name,
            nodeOptions.Value.GatewayName,
            nodeOptions.Value.AdvertisedBaseUri ?? "https://localhost:7553",
            certificates.CreateCsrBase64(),
            challengeId,
            enrollmentProof);

        using var response = await client.PostAsJsonAsync("fabric/v1/pair", request, cancellationToken).ConfigureAwait(false);
        var pairing = await response.Content.ReadFromJsonAsync<PairingResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (pairing is null || !string.Equals(pairing.GatewayName, nodeOptions.Value.GatewayName, StringComparison.Ordinal))
        {
            logger.LogWarning("Gateway candidate {Gateway} did not return the expected identity.", gateway);
            return null;
        }

        if (!response.IsSuccessStatusCode || pairing.Status != "paired" || pairing.CertificateBase64 is null || pairing.RootCertificateBase64 is null)
        {
            logger.LogInformation("Pairing with {Gateway} is {Status}: {Message}", gateway, pairing.Status, pairing.Message);
            return null;
        }

        var issuedCertificate = Convert.FromBase64String(pairing.CertificateBase64);
        var rootCertificate = Convert.FromBase64String(pairing.RootCertificateBase64);
        if (challengeId is { } completedChallenge && !string.IsNullOrWhiteSpace(securityOptions.Value.EnrollmentToken))
        {
            if (string.IsNullOrWhiteSpace(pairing.GatewayProofBase64))
            {
                logger.LogWarning("Gateway {Gateway} did not authenticate its enrollment response.", gateway);
                return null;
            }

            byte[] suppliedProof;
            try { suppliedProof = Convert.FromBase64String(pairing.GatewayProofBase64); }
            catch (FormatException) { return null; }
            var expectedProof = PairingProof.ComputeGatewayResponse(
                securityOptions.Value.EnrollmentToken,
                completedChallenge,
                certificates.Identity.InstanceId,
                pairing.GatewayId,
                pairing.ClusterId,
                pairing.GatewayName,
                issuedCertificate,
                rootCertificate);
            if (suppliedProof.Length != expectedProof.Length || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(suppliedProof, expectedProof))
            {
                logger.LogWarning("Gateway {Gateway} returned an invalid enrollment response proof.", gateway);
                return null;
            }
        }

        certificates.Install(pairing.ClusterId, issuedCertificate, rootCertificate);
        var state = new NodePairingState(pairing.GatewayId, pairing.ClusterId, pairing.GatewayName, gateway.ToString().TrimEnd('/'));
        pairingState.Save(state);
        logger.LogInformation("Paired node {NodeId} with gateway {GatewayId}.", certificates.Identity.InstanceId, pairing.GatewayId);
        return state;
    }

    private static bool ValidateGatewayCertificate(
        X509Certificate? certificate,
        X509Certificate2 root,
        Guid expectedGatewayId)
    {
        if (certificate is null) return false;
        var ownsCertificate = certificate is not X509Certificate2;
        var peer = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            return FabricCertificateValidation.Validate(peer, root, out _) &&
                   Guid.TryParse(peer.GetNameInfo(X509NameType.SimpleName, forIssuer: false), out var peerId) &&
                   peerId == expectedGatewayId;
        }
        finally
        {
            if (ownsCertificate) peer.Dispose();
        }
    }

    private async Task RunHubConnectionAsync(NodePairingState state, CancellationToken cancellationToken)
    {
        var root = certificates.RootCertificate ?? throw new InvalidOperationException("The cluster root certificate is unavailable.");
        var nodeCertificate = certificates.CurrentServerCertificate;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(state.GatewayBaseUri.TrimEnd('/') + "/"), "fabric/v1/hub"), options =>
            {
                options.ClientCertificates.Add(nodeCertificate);
                options.WebSocketConfiguration = webSocket =>
                {
                    webSocket.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        ValidateGatewayCertificate(certificate, root, state.GatewayId);
                };
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler)
                    {
                        clientHandler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                            ValidateGatewayCertificate(certificate, root, state.GatewayId);
                    }
                    return handler;
                };
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
            .WithStatefulReconnect()
            .Build();

        await using (connection)
        {
            await connection.StartAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Connected to gateway {GatewayName} ({GatewayId}).", state.GatewayName, state.GatewayId);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(nodeOptions.Value.HeartbeatSeconds));
            do
            {
                var heartbeat = new NodeHeartbeat(
                    certificates.Identity.InstanceId,
                    nodeOptions.Value.Name,
                    nodeOptions.Value.AdvertisedBaseUri ?? "https://localhost:7553",
                    DateTimeOffset.UtcNow,
                    backends.GetSnapshots());
                await connection.InvokeAsync("Heartbeat", heartbeat, cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
    }
}
