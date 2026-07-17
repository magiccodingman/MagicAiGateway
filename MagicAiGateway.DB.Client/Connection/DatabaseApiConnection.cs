using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MagicAiGateway.DB.Client.Configuration;

namespace MagicAiGateway.DB.Client.Connection;

public interface IDatabaseApiConnection : IAsyncDisposable
{
    DatabaseApiEndpointSnapshot? Current { get; }
    Task<(HttpClient Client, DatabaseApiEndpointSnapshot Endpoint)> ConnectAsync(
        CancellationToken cancellationToken = default);
    void Invalidate(Exception? failure = null);
}

public sealed class DatabaseApiConnection(
    IDatabaseApiEndpointResolver resolver,
    DatabaseApiClientOptions options) : IDatabaseApiConnection
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpClient? _client;
    private DatabaseApiEndpointSnapshot? _current;

    public DatabaseApiEndpointSnapshot? Current => _current;

    public async Task<(HttpClient Client, DatabaseApiEndpointSnapshot Endpoint)> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        var resolved = await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (_client is not null && _current is not null && SameEndpoint(_current, resolved))
        {
            return (_client, _current);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            resolved = await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
            if (_client is not null && _current is not null && SameEndpoint(_current, resolved))
            {
                return (_client, _current);
            }

            ValidateEndpoint(resolved.BaseUri);
            var replacement = new HttpClient(CreateHandler(resolved))
            {
                BaseAddress = resolved.BaseUri,
                Timeout = Timeout.InfiniteTimeSpan
            };
            var previous = Interlocked.Exchange(ref _client, replacement);
            previous?.Dispose();
            _current = resolved;
            return (replacement, resolved);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(Exception? failure = null)
    {
        resolver.Invalidate();
        var previous = Interlocked.Exchange(ref _client, null);
        previous?.Dispose();
        _current = null;
    }

    private static HttpMessageHandler CreateHandler(DatabaseApiEndpointSnapshot endpoint)
    {
        if (endpoint.BaseUri.Scheme != Uri.UriSchemeHttps)
        {
            return new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
        }

        if (string.IsNullOrWhiteSpace(endpoint.RootCertificateBase64))
        {
            return new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
        }

        var rootBytes = Convert.FromBase64String(endpoint.RootCertificateBase64);
        var expectedPeerId = endpoint.PeerId;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            ValidateCertificate(certificate, rootBytes, expectedPeerId);
        return handler;
    }

    private static bool ValidateCertificate(
        X509Certificate? certificate,
        byte[] rootBytes,
        Guid? expectedPeerId)
    {
        if (certificate is null) return false;
        var ownsCertificate = certificate is not X509Certificate2;
        var peer = certificate as X509Certificate2
                   ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            using var root = X509CertificateLoader.LoadCertificate(rootBytes);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (!chain.Build(peer)) return false;
            if (expectedPeerId is null) return true;
            return Guid.TryParse(
                       peer.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                       out var observedPeerId) &&
                   observedPeerId == expectedPeerId;
        }
        finally
        {
            if (ownsCertificate) peer.Dispose();
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
        {
            throw new AuthenticationException(
                "Non-loopback database API endpoints must use HTTPS.");
        }
    }

    private static bool SameEndpoint(
        DatabaseApiEndpointSnapshot left,
        DatabaseApiEndpointSnapshot right) =>
        left.BaseUri == right.BaseUri &&
        left.PeerId == right.PeerId &&
        string.Equals(
            left.RootCertificateBase64,
            right.RootCertificateBase64,
            StringComparison.Ordinal);

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
