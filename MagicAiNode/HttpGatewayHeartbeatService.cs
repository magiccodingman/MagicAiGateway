using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Contracts;
using SharedMagic.Security;

namespace MagicAiNode;

public sealed class HttpGatewayHeartbeatService(
    IOptions<NodeOptions> nodeOptions,
    NodeCertificateStore certificates,
    NodePairingStateStore pairingState,
    BackendCatalog backends,
    ILogger<HttpGatewayHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var state = pairingState.Load();
            if (state is null || !certificates.IsPaired)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await SendHeartbeatAsync(state, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "HTTP heartbeat to gateway {Gateway} failed.", state.GatewayBaseUri);
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(1, nodeOptions.Value.HeartbeatSeconds)),
                stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SendHeartbeatAsync(NodePairingState state, CancellationToken cancellationToken)
    {
        using var root = certificates.RootCertificate
                         ?? throw new InvalidOperationException("The cluster root certificate is unavailable.");
        using var nodeCertificate = certificates.CurrentServerCertificate;
        using var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                ValidateGatewayCertificate(certificate, root, state.GatewayId)
        };
        handler.ClientCertificates.Add(nodeCertificate);

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(state.GatewayBaseUri.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        var heartbeat = new NodeHeartbeat(
            certificates.Identity.InstanceId,
            nodeOptions.Value.Name,
            nodeOptions.Value.AdvertisedBaseUri ?? "https://localhost:7553",
            DateTimeOffset.UtcNow,
            backends.GetSnapshots());

        using var request = new HttpRequestMessage(HttpMethod.Post, "fabric/v1/heartbeat")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(heartbeat)
        };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        logger.LogDebug("Gateway accepted heartbeat for node {NodeId}.", heartbeat.NodeId);
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
}
