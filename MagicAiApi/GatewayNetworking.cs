using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using SharedMagic.Security;

namespace MagicAiApi;

public sealed class GatewayProxyInvoker : IDisposable
{
    private readonly GatewayCertificateAuthority _authority;
    private readonly ConcurrentDictionary<Guid, HttpMessageInvoker> _invokers = [];

    public GatewayProxyInvoker(GatewayCertificateAuthority authority) => _authority = authority;

    public HttpMessageInvoker Get(GatewayNodeTarget target) =>
        _invokers.GetOrAdd(target.NodeId, Create);

    private HttpMessageInvoker Create(Guid expectedNodeId)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { _authority.ServerCertificate };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            ValidateNodeCertificate(certificate, expectedNodeId);
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    private bool ValidateNodeCertificate(X509Certificate? certificate, Guid expectedNodeId)
    {
        if (certificate is null) return false;
        var ownsCertificate = certificate is not X509Certificate2;
        var peer = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            return FabricCertificateValidation.Validate(peer, _authority.RootCertificate, out _) &&
                   Guid.TryParse(peer.GetNameInfo(X509NameType.SimpleName, forIssuer: false), out var peerId) &&
                   peerId == expectedNodeId;
        }
        finally
        {
            if (ownsCertificate) peer.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var invoker in _invokers.Values) invoker.Dispose();
        _invokers.Clear();
    }
}

public sealed class GatewayNodeClient(GatewayProxyInvoker invokers)
{
    public async Task<HttpResponseMessage> GetAsync(GatewayNodeTarget target, string relativePath, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(target.BaseUri.TrimEnd('/') + "/"), relativePath.TrimStart('/')));
        return await invokers.Get(target).SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
