using System.Net.Http.Json;
using MagicAiGateway.Client.Authentication;
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
    private bool _disposed;

    internal GatewayResponseStream(HttpResponseMessage response, Stream stream)
    {
        Response = response;
        Stream = stream;
    }

    public HttpResponseMessage Response { get; }
    public Stream Stream { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stream.Dispose();
        Response.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Stream.DisposeAsync().ConfigureAwait(false);
        Response.Dispose();
    }
}

public sealed class RawGatewayClient(
    GatewayConnection connection,
    IGatewayCredentialProvider credentialProvider) : IRawGatewayClient
{
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
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

    public async Task<GatewayResponseStream> SendStreamingAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new GatewayResponseStream(response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
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
