using System.Collections.Concurrent;
using System.Net.Security;
using Yarp.ReverseProxy.Forwarder;
using SharedMagic.Configuration;

namespace MagicAiNode;

public sealed class BackendProxyInvokerPool : IDisposable
{
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _invokers = new(StringComparer.Ordinal);

    public HttpMessageInvoker Get(BackendOptions options) => _invokers.GetOrAdd(options.Id, _ => Create(options));

    private static HttpMessageInvoker Create(BackendOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        if (options.AllowInvalidServerCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public void Dispose()
    {
        foreach (var invoker in _invokers.Values) invoker.Dispose();
        _invokers.Clear();
    }
}

public sealed class BackendTransformer(BackendOptions options) : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);
        proxyRequest.Headers.Remove("X-Magic-Model");
        proxyRequest.Headers.Remove("X-Magic-Loopback-Token");
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            proxyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }
}
