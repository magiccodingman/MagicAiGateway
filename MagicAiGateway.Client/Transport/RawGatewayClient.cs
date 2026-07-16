using System.Net.Http.Json;
using MagicAiGateway.Client.Authentication;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Connection;

namespace MagicAiGateway.Client.Transport;

public interface IRawGatewayClient
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> GetAsync(string relativePath, CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PostJsonAsync<T>(
        string relativePath,
        T value,
        CancellationToken cancellationToken = default);

    Task<GatewayResponseStream> SendStreamingAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default);
}

public sealed class GatewayResponseStream : IAsyncDisposable, IDisposable
{
    private readonly CancellationTokenSource? _requestCancellation;
    private bool _disposed;

    internal GatewayResponseStream(
        HttpResponseMessage response,
        Stream stream,
        CancellationTokenSource? requestCancellation = null)
    {
        Response = response;
        Stream = stream;
        _requestCancellation = requestCancellation;
    }

    public HttpResponseMessage Response { get; }
    public Stream Stream { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stream.Dispose();
        Response.Dispose();
        _requestCancellation?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Stream.DisposeAsync().ConfigureAwait(false);
        Response.Dispose();
        _requestCancellation?.Dispose();
    }
}

public sealed class RawGatewayClient(
    GatewayConnection connection,
    IGatewayCredentialProvider credentialProvider,
    MagicAiGatewayClientOptions options) : IRawGatewayClient
{
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default) =>
        SendWithTimeoutAsync(request, completionOption, options.StandardRequestTimeout, cancellationToken);

    internal async Task<HttpResponseMessage> SendWithTimeoutAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var requestCancellation = CreateRequestCancellation(timeout, cancellationToken);
        return await SendCoreAsync(
            request,
            completionOption,
            requestCancellation.Token).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> GetAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        return await SendAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> PostJsonAsync<T>(
        string relativePath,
        T value,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = JsonContent.Create(value)
        };
        return await SendAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<GatewayResponseStream> SendStreamingAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default) =>
        SendStreamingWithTimeoutAsync(request, options.StandardRequestTimeout, cancellationToken);

    internal async Task<GatewayResponseStream> SendStreamingWithTimeoutAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var requestCancellation = CreateRequestCancellation(timeout, cancellationToken);
        try
        {
            var response = await SendCoreAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token).ConfigureAwait(false);
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token).ConfigureAwait(false);
                return new GatewayResponseStream(response, stream, requestCancellation);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch
        {
            requestCancellation.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var relativeUri = request.RequestUri;
        ValidateRequestUri(relativeUri);

        var client = await connection.GetHttpClientAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = connection.Current
                       ?? throw new InvalidOperationException("The gateway endpoint was not resolved.");
        var absoluteUri = new Uri(endpoint.BaseUri, relativeUri!);
        request.RequestUri = absoluteUri;

        var credential = await credentialProvider.GetCredentialAsync(
            new GatewayCredentialContext(endpoint.BaseUri, request.Method, absoluteUri),
            cancellationToken).ConfigureAwait(false);
        credential?.Apply(request);

        try
        {
            return await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            connection.Invalidate(exception);
            throw;
        }
    }

    private static CancellationTokenSource CreateRequestCancellation(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan) source.CancelAfter(timeout);
        return source;
    }

    private static void ValidateRequestUri(Uri? requestUri)
    {
        if (requestUri is null)
        {
            throw new InvalidOperationException("A relative gateway request URI is required.");
        }

        if (requestUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                "Raw gateway requests must use relative paths. Absolute external URLs are intentionally rejected.");
        }
    }
}
