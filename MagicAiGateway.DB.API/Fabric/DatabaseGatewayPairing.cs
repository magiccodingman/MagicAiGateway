using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using MagicAiGateway.Client.Discovery;
using MagicAiGateway.DB.API.Configuration;
using MagicAiGateway.DB.API.Database;
using MagicAiGateway.DB.Contracts;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Contracts;
using SharedMagic.Security;

namespace MagicAiGateway.DB.API.Fabric;

public sealed record DatabaseGatewayPairingState(
    Guid GatewayId,
    Guid ClusterId,
    string GatewayName,
    string GatewayBaseUri);

public sealed class DatabaseGatewayPairingStateStore
{
    private readonly string _path;
    private readonly object _sync = new();

    public DatabaseGatewayPairingStateStore(string directory) =>
        _path = Path.Combine(directory, "gateway.json");

    public DatabaseGatewayPairingState? Load()
    {
        lock (_sync)
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<DatabaseGatewayPairingState>(File.ReadAllText(_path))
                : null;
        }
    }

    public void Save(DatabaseGatewayPairingState state)
    {
        lock (_sync)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            FabricStateFiles.TryRestrictFile(_path);
        }
    }
}

public sealed class DatabaseGatewayPeerTrustProvider(
    NodeCertificateStore certificates,
    DatabaseGatewayPairingStateStore pairingState,
    IOptions<FabricSecurityOptions> options) : IFabricPeerTrustProvider
{
    public X509Certificate2? RootCertificate => certificates.RootCertificate;
    public string ExpectedPeerRole => "gateway";
    public string? LoopbackToken => options.Value.LoopbackToken;
    public bool AllowInsecureLoopback => options.Value.AllowInsecureLoopback;
    public ValueTask<bool> IsPeerAllowedAsync(Guid peerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(pairingState.Load()?.GatewayId == peerId);
}

public sealed class DatabaseFabricRegistrationService(
    IOptions<DatabaseApiOptions> databaseApiOptions,
    IOptions<FabricSecurityOptions> securityOptions,
    IOptions<DiscoveryOptions> discoveryOptions,
    NodeCertificateStore certificates,
    DatabaseGatewayPairingStateStore pairingState,
    DatabaseReadinessState readiness,
    ILoggerFactory loggerFactory,
    ILogger<DatabaseFabricRegistrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MdnsBrowser? browser = null;
        var candidates = Channel.CreateUnbounded<Uri>();
        foreach (var configured in databaseApiOptions.Value.StaticGateways)
        {
            if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)) candidates.Writer.TryWrite(uri);
        }

        if (discoveryOptions.Value.Enabled)
        {
            browser = new MdnsBrowser(
                discoveryOptions.Value.GatewayServiceType,
                databaseApiOptions.Value.GatewayName,
                loggerFactory.CreateLogger<MdnsBrowser>());
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
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Pairing DB API with gateway candidate {Gateway} failed.", candidate);
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
                    await SendHeartbeatAsync(state, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "DB API heartbeat to gateway {Gateway} failed.", state.GatewayBaseUri);
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, databaseApiOptions.Value.HeartbeatSeconds)),
                    stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            browser?.Dispose();
        }
    }

    private async Task<DatabaseGatewayPairingState?> TryPairAsync(Uri gateway, CancellationToken cancellationToken)
    {
        if (!string.Equals(gateway.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !gateway.IsLoopback)
        {
            return null;
        }

        using var handler = new HttpClientHandler();
        if (securityOptions.Value.PairingServerCertificateMode == PairingServerCertificateMode.TrustOnFirstUse)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(gateway.ToString().TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        Guid? challengeId = null;
        string? enrollmentProof = null;
        if (!string.IsNullOrWhiteSpace(securityOptions.Value.EnrollmentToken))
        {
            using var challengeResponse = await client.PostAsJsonAsync(
                "fabric/v1/pair/challenge",
                new PairingChallengeRequest(certificates.Identity.InstanceId, databaseApiOptions.Value.GatewayName),
                cancellationToken).ConfigureAwait(false);
            if (challengeResponse.IsSuccessStatusCode)
            {
                var challenge = await challengeResponse.Content.ReadFromJsonAsync<PairingChallengeResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (challenge is not null)
                {
                    var proof = PairingProof.Compute(
                        securityOptions.Value.EnrollmentToken,
                        challenge.ChallengeId,
                        Convert.FromBase64String(challenge.NonceBase64),
                        certificates.Identity.InstanceId,
                        challenge.GatewayId,
                        challenge.ClusterId,
                        challenge.GatewayName);
                    challengeId = challenge.ChallengeId;
                    enrollmentProof = Convert.ToBase64String(proof);
                }
            }
        }

        var advertised = databaseApiOptions.Value.AdvertisedEndpoints.FirstOrDefault()
                         ?? throw new InvalidOperationException("DatabaseApi:AdvertisedEndpoints requires at least one endpoint.");
        var request = new PairingRequest(
            certificates.Identity.InstanceId,
            databaseApiOptions.Value.Name,
            databaseApiOptions.Value.GatewayName,
            advertised,
            certificates.CreateCsrBase64(),
            challengeId,
            enrollmentProof,
            PeerRole: "database-api",
            ApplicationId: MagicApplication.DatabaseApi.ToString());

        using var response = await client.PostAsJsonAsync("fabric/v1/pair", request, cancellationToken).ConfigureAwait(false);
        var pairing = await response.Content.ReadFromJsonAsync<PairingResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (pairing is null ||
            !response.IsSuccessStatusCode ||
            pairing.Status != "paired" ||
            pairing.CertificateBase64 is null ||
            pairing.RootCertificateBase64 is null ||
            !string.Equals(pairing.GatewayName, databaseApiOptions.Value.GatewayName, StringComparison.Ordinal))
        {
            return null;
        }

        var issued = Convert.FromBase64String(pairing.CertificateBase64);
        var root = Convert.FromBase64String(pairing.RootCertificateBase64);
        if (challengeId is { } completedChallenge && !string.IsNullOrWhiteSpace(securityOptions.Value.EnrollmentToken))
        {
            var expected = PairingProof.ComputeGatewayResponse(
                securityOptions.Value.EnrollmentToken,
                completedChallenge,
                certificates.Identity.InstanceId,
                pairing.GatewayId,
                pairing.ClusterId,
                pairing.GatewayName,
                issued,
                root);
            if (string.IsNullOrWhiteSpace(pairing.GatewayProofBase64)) return null;
            var supplied = Convert.FromBase64String(pairing.GatewayProofBase64);
            if (supplied.Length != expected.Length || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(supplied, expected)) return null;
        }

        certificates.Install(pairing.ClusterId, issued, root);
        var state = new DatabaseGatewayPairingState(
            pairing.GatewayId,
            pairing.ClusterId,
            pairing.GatewayName,
            gateway.ToString().TrimEnd('/'));
        pairingState.Save(state);
        logger.LogInformation("Paired DB API {PeerId} with gateway {GatewayId}.", certificates.Identity.InstanceId, pairing.GatewayId);
        return state;
    }

    private async Task SendHeartbeatAsync(DatabaseGatewayPairingState state, CancellationToken cancellationToken)
    {
        using var root = certificates.RootCertificate
                         ?? throw new InvalidOperationException("The cluster root certificate is unavailable.");
        using var peerCertificate = certificates.CurrentServerCertificate;
        using var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                ValidateGatewayCertificate(certificate, root, state.GatewayId)
        };
        handler.ClientCertificates.Add(peerCertificate);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(state.GatewayBaseUri.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        var heartbeat = new FabricServiceHeartbeat(
            certificates.Identity.InstanceId,
            certificates.Identity.InstanceId,
            MagicApplication.DatabaseApi.ToString(),
            MagicFabricServices.Database,
            databaseApiOptions.Value.GatewayName,
            databaseApiOptions.Value.AdvertisedEndpoints.Select(ToEndpoint).ToArray(),
            typeof(DatabaseFabricRegistrationService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            readiness.IsReady ? FabricServiceHealth.Ready : FabricServiceHealth.Starting,
            DateTimeOffset.UtcNow);
        using var response = await client.PostAsJsonAsync(
            "fabric/v1/services/heartbeat",
            heartbeat,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static FabricServiceEndpoint ToEndpoint(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        var scope = uri.IsLoopback
            ? FabricEndpointScope.Loopback
            : uri.HostNameType is UriHostNameType.Dns && !uri.Host.Contains('.')
                ? FabricEndpointScope.Lan
                : FabricEndpointScope.Lan;
        return new FabricServiceEndpoint(uri.ToString().TrimEnd('/'), scope);
    }

    private static bool ValidateGatewayCertificate(X509Certificate? certificate, X509Certificate2 root, Guid expectedGatewayId)
    {
        if (certificate is null) return false;
        var ownsCertificate = certificate is not X509Certificate2;
        var peer = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            return FabricCertificateValidation.Validate(peer, root, out _) &&
                   Guid.TryParse(peer.GetNameInfo(X509NameType.SimpleName, false), out var peerId) &&
                   peerId == expectedGatewayId;
        }
        finally
        {
            if (ownsCertificate) peer.Dispose();
        }
    }
}
