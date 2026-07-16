using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Discovery;
using MagicAiGateway.Client.Protocol;
using MagicAiGateway.Client.Security;
using Microsoft.Extensions.Logging;

namespace MagicAiGateway.Client.Connection;

public enum GatewayConnectionState
{
    Disconnected,
    Resolving,
    Connected,
    Faulted
}

public sealed record ResolvedGatewayEndpoint(
    Uri BaseUri,
    GatewayInfo Gateway,
    GatewayCandidateKind CandidateKind,
    bool UsesApplicationTrust);

public sealed record GatewayConnectionSnapshot(
    GatewayConnectionState State,
    ResolvedGatewayEndpoint? Endpoint,
    Exception? LastFailure);

public interface IGatewayConnection
{
    GatewayConnectionState State { get; }
    ResolvedGatewayEndpoint? Current { get; }
    Exception? LastFailure { get; }

    Task<ResolvedGatewayEndpoint> ConnectAsync(CancellationToken cancellationToken = default);
    Task ResetTrustAsync(CancellationToken cancellationToken = default);
    GatewayConnectionSnapshot GetSnapshot();
}

public sealed class GatewayConnection : IGatewayConnection, IAsyncDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MagicAiGatewayClientOptions _options;
    private readonly IGatewayTrustStore _trustStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GatewayConnection> _logger;
    private readonly SemaphoreSlim _resolutionGate = new(1, 1);
    private HttpClient? _client;
    private ResolvedGatewayEndpoint? _current;
    private Exception? _lastFailure;
    private GatewayConnectionState _state;

    public GatewayConnection(
        MagicAiGatewayClientOptions options,
        IGatewayTrustStore trustStore,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _options.Validate();
        _trustStore = trustStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GatewayConnection>();
    }

    public GatewayConnectionState State => _state;
    public ResolvedGatewayEndpoint? Current => _current;
    public Exception? LastFailure => _lastFailure;

    public async Task<ResolvedGatewayEndpoint> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_current is not null && _client is not null) return _current;

        await _resolutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is not null && _client is not null) return _current;

            _state = GatewayConnectionState.Resolving;
            _lastFailure = null;
            var trust = await _trustStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var failures = new List<Exception>();

            await foreach (var candidate in FindCandidatesAsync(trust, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var connected = await ConnectCandidateAsync(candidate, trust, cancellationToken).ConfigureAwait(false);
                    _client = connected.Client;
                    _current = connected.Endpoint;
                    _state = GatewayConnectionState.Connected;
                    _logger.LogInformation(
                        "Connected Magic AI Gateway client to {GatewayName} ({GatewayId}) at {Endpoint}.",
                        connected.Endpoint.Gateway.Name,
                        connected.Endpoint.Gateway.GatewayId,
                        connected.Endpoint.BaseUri);
                    return connected.Endpoint;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    _logger.LogDebug(exception, "Gateway candidate {Endpoint} was not usable.", candidate.BaseUri);
                }
            }

            _lastFailure = failures.Count switch
            {
                0 => new InvalidOperationException("No Magic AI Gateway endpoint candidates were available."),
                1 => failures[0],
                _ => new AggregateException("No Magic AI Gateway endpoint could be connected securely.", failures)
            };
            _state = GatewayConnectionState.Faulted;
            throw _lastFailure;
        }
        finally
        {
            _resolutionGate.Release();
        }
    }

    public async Task ResetTrustAsync(CancellationToken cancellationToken = default)
    {
        await _resolutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeClient();
            _current = null;
            _lastFailure = null;
            _state = GatewayConnectionState.Disconnected;
            await _trustStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resolutionGate.Release();
        }
    }

    public GatewayConnectionSnapshot GetSnapshot() => new(_state, _current, _lastFailure);

    internal async Task<HttpClient> GetHttpClientAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _client ?? throw new InvalidOperationException("The gateway HTTP client was not initialized.");
    }

    internal void Invalidate(Exception exception)
    {
        DisposeClient();
        _current = null;
        _lastFailure = exception;
        _state = GatewayConnectionState.Faulted;
    }

    private async IAsyncEnumerable<GatewayCandidate> FindCandidatesAsync(
        GatewayTrustRecord? trust,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_options.EndpointOverride is not null)
        {
            yield return new GatewayCandidate(Normalize(_options.EndpointOverride), GatewayCandidateKind.Configured, "endpoint override");
            yield break;
        }

        var localSources = new List<IGatewayEndpointSource>();
        var remoteSources = new List<IGatewayEndpointSource>();

        if (_options.Discovery.EnableLoopback)
        {
            localSources.Add(new LoopbackGatewayEndpointSource());
        }

        if (trust is not null && Uri.TryCreate(trust.LastKnownBaseUri, UriKind.Absolute, out var lastKnown))
        {
            localSources.Add(new ConfiguredGatewayEndpointSource([lastKnown], 10));
        }

        if (_options.Discovery.EnableMdns)
        {
            localSources.Add(new MdnsGatewayEndpointSource(
                _options.Discovery.ServiceType,
                _options.ExpectedGatewayName,
                _options.Discovery.Timeout,
                _loggerFactory.CreateLogger<MdnsGatewayEndpointSource>()));
        }

        if (_options.Discovery.FallbackEndpoints.Count > 0)
        {
            remoteSources.Add(new ConfiguredGatewayEndpointSource(_options.Discovery.FallbackEndpoints));
        }

        IEnumerable<IGatewayEndpointSource> sources = _options.Discovery.Mode switch
        {
            GatewayDiscoveryMode.ConfiguredOnly => remoteSources,
            GatewayDiscoveryMode.RemoteFirst => remoteSources.Concat(localSources),
            _ => localSources.Concat(remoteSources)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.OrderBy(source => source.Order))
        {
            await foreach (var candidate in source.FindAsync(cancellationToken).ConfigureAwait(false))
            {
                var normalized = Normalize(candidate.BaseUri);
                if (!seen.Add(normalized.AbsoluteUri)) continue;
                yield return candidate with { BaseUri = normalized };
            }
        }
    }

    private async Task<ConnectedCandidate> ConnectCandidateAsync(
        GatewayCandidate candidate,
        GatewayTrustRecord? trust,
        CancellationToken cancellationToken)
    {
        if (candidate.BaseUri.Scheme != Uri.UriSchemeHttps && !candidate.BaseUri.IsLoopback)
        {
            throw new AuthenticationException("Non-loopback Magic AI Gateway endpoints must use HTTPS.");
        }

        if (trust is not null)
        {
            using var root = GatewayCertificateValidator.LoadRoot(trust.RootCertificateBase64);
            var handler = CreatePinnedHandler(root, trust.GatewayId);
            var client = CreateClient(handler, candidate.BaseUri);
            try
            {
                var info = await ReadGatewayInfoAsync(client, cancellationToken).ConfigureAwait(false);
                ValidateGatewayInfo(info, trust);
                return new ConnectedCandidate(
                    client,
                    new ResolvedGatewayEndpoint(candidate.BaseUri, info, candidate.Kind, UsesApplicationTrust: true));
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        if (_options.Security.TrustMode == GatewayTrustMode.PinnedCertificateAuthority)
        {
            using var root = GatewayCertificateValidator.LoadRoot(_options.Security.PinnedRootCertificateBase64!);
            X509Certificate2? observed = null;
            var handler = CreateRootPinnedBootstrapHandler(root, certificate => observed = Clone(certificate));
            using var bootstrap = CreateClient(handler, candidate.BaseUri);
            var info = await ReadGatewayInfoAsync(bootstrap, cancellationToken).ConfigureAwait(false);
            ValidateGatewayInfo(info, expectedTrust: null);
            ValidateObservedCertificate(observed, root, info.GatewayId);
            await _trustStore.SaveAsync(GatewayCertificateValidator.CreateRecord(info, candidate.BaseUri), cancellationToken).ConfigureAwait(false);
            observed?.Dispose();
            return CreateTrustedCandidate(candidate, info, root);
        }

        try
        {
            var systemClient = CreateClient(new HttpClientHandler(), candidate.BaseUri);
            try
            {
                var info = await ReadGatewayInfoAsync(systemClient, cancellationToken).ConfigureAwait(false);
                ValidateGatewayInfo(info, expectedTrust: null);
                return new ConnectedCandidate(
                    systemClient,
                    new ResolvedGatewayEndpoint(candidate.BaseUri, info, candidate.Kind, UsesApplicationTrust: false));
            }
            catch
            {
                systemClient.Dispose();
                throw;
            }
        }
        catch (Exception systemFailure) when (CanBootstrap(candidate))
        {
            _logger.LogDebug(systemFailure, "System certificate validation failed for {Endpoint}; attempting allowed application trust bootstrap.", candidate.BaseUri);
        }

        X509Certificate2? bootstrapCertificate = null;
        using (var bootstrapClient = CreateClient(
                   CreateBootstrapHandler(certificate => bootstrapCertificate = Clone(certificate)),
                   candidate.BaseUri))
        {
            var info = await ReadGatewayInfoAsync(bootstrapClient, cancellationToken).ConfigureAwait(false);
            ValidateGatewayInfo(info, expectedTrust: null);

            using var root = GatewayCertificateValidator.LoadRoot(info.RootCertificateBase64);
            ValidateObservedCertificate(bootstrapCertificate, root, info.GatewayId);
            await _trustStore.SaveAsync(GatewayCertificateValidator.CreateRecord(info, candidate.BaseUri), cancellationToken).ConfigureAwait(false);
            bootstrapCertificate?.Dispose();
            return CreateTrustedCandidate(candidate, info, root);
        }
    }

    private ConnectedCandidate CreateTrustedCandidate(GatewayCandidate candidate, GatewayInfo info, X509Certificate2 root)
    {
        var client = CreateClient(CreatePinnedHandler(root, info.GatewayId), candidate.BaseUri);
        return new ConnectedCandidate(
            client,
            new ResolvedGatewayEndpoint(candidate.BaseUri, info, candidate.Kind, UsesApplicationTrust: true));
    }

    private bool CanBootstrap(GatewayCandidate candidate) => _options.Security.TrustMode switch
    {
        GatewayTrustMode.TrustOnFirstUse => true,
        GatewayTrustMode.InsecureDevelopment => !_options.Security.AllowInsecureLoopbackOnly || candidate.BaseUri.IsLoopback,
        GatewayTrustMode.SystemOrLocalTrustOnFirstUse => candidate.IsLocal,
        _ => false
    };

    private HttpClient CreateClient(HttpMessageHandler handler, Uri baseUri)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = Normalize(baseUri),
            Timeout = _options.RequestTimeout
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", _options.ApiKey);
        }
        return client;
    }

    private static HttpClientHandler CreatePinnedHandler(X509Certificate2 root, Guid gatewayId)
    {
        var rootBytes = root.Export(X509ContentType.Cert);
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var trustedRoot = X509CertificateLoader.LoadCertificate(rootBytes);
                return GatewayCertificateValidator.Validate(certificate, trustedRoot, gatewayId, out _);
            }
        };
    }

    private static HttpClientHandler CreateRootPinnedBootstrapHandler(
        X509Certificate2 root,
        Action<X509Certificate2> observe)
    {
        var rootBytes = root.Export(X509ContentType.Cert);
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                observe(certificate);
                using var trustedRoot = X509CertificateLoader.LoadCertificate(rootBytes);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            }
        };
    }

    private static HttpClientHandler CreateBootstrapHandler(Action<X509Certificate2> observe) => new()
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            observe(certificate);
            return true;
        }
    };

    private async Task<GatewayInfo> ReadGatewayInfoAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(MagicAiGatewayProtocol.GatewayInfoPath, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayInfo>(JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The gateway identity endpoint returned an empty response.");
    }

    private void ValidateGatewayInfo(GatewayInfo info, GatewayTrustRecord? expectedTrust)
    {
        if (!string.Equals(info.Name, _options.ExpectedGatewayName, StringComparison.Ordinal))
        {
            throw new AuthenticationException($"Expected gateway '{_options.ExpectedGatewayName}' but found '{info.Name}'.");
        }

        if (info.MinimumClientProtocolVersion > MagicAiGatewayProtocol.CurrentVersion)
        {
            throw new NotSupportedException(
                $"Gateway protocol {info.MinimumClientProtocolVersion} or newer is required; this client supports {MagicAiGatewayProtocol.CurrentVersion}.");
        }

        if (expectedTrust is not null &&
            (info.GatewayId != expectedTrust.GatewayId || info.ClusterId != expectedTrust.ClusterId))
        {
            throw new AuthenticationException("The discovered endpoint does not match the previously trusted gateway identity.");
        }
    }

    private static void ValidateObservedCertificate(
        X509Certificate2? observed,
        X509Certificate2 root,
        Guid gatewayId)
    {
        if (observed is null)
        {
            throw new AuthenticationException("The gateway did not present a server certificate.");
        }

        if (!GatewayCertificateValidator.Validate(observed, root, gatewayId, out var error))
        {
            throw new AuthenticationException(error ?? "The gateway certificate is not trusted.");
        }
    }

    private static X509Certificate2 Clone(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadCertificate(certificate.RawData);

    private static Uri Normalize(Uri endpoint) => new(endpoint.ToString().TrimEnd('/') + "/");

    private void DisposeClient()
    {
        _client?.Dispose();
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        DisposeClient();
        _resolutionGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record ConnectedCandidate(HttpClient Client, ResolvedGatewayEndpoint Endpoint);
}
