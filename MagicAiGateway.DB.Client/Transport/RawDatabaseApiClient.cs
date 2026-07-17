using System.Net.Http.Json;
using MagicAiGateway.DB.Client.Authentication;
using MagicAiGateway.DB.Client.Configuration;
using MagicAiGateway.DB.Client.Connection;
using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.Client.Transport;

public interface IRawDatabaseApiClient
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> GetAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PostJsonAsync<T>(
        string relativePath,
        T value,
        CancellationToken cancellationToken = default);

    Task<DatabaseApiResponseStream> SendStreamingAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseApiResponseStream : IAsyncDisposable, IDisposable
{
    private readonly CancellationTokenSource _requestCancellation;
    private bool _disposed;

    internal DatabaseApiResponseStream(
        HttpResponseMessage response,
        Stream stream,
        CancellationTokenSource requestCancellation)
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
        _requestCancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Stream.DisposeAsync().ConfigureAwait(false);
        Response.Dispose();
        _requestCancellation.Dispose();
    }
}

public sealed class RawDatabaseApiClient(
    IDatabaseApiConnection connection,
    IDatabaseApiCredentialProvider credentialProvider,
    DatabaseApiClientOptions options) : IRawDatabaseApiClient
{
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        using var requestCancellation = CreateRequestCancellation(cancellationToken);
        return await SendCoreAsync(request, completionOption, requestCancellation.Token)
            .ConfigureAwait(false);
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

    public async Task<DatabaseApiResponseStream> SendStreamingAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var requestCancellation = CreateRequestCancellation(cancellationToken);
        try
        {
            var response = await SendCoreAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token).ConfigureAwait(false);
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token)
                    .ConfigureAwait(false);
                return new DatabaseApiResponseStream(response, stream, requestCancellation);
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
        if (request.RequestUri is null || request.RequestUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                "Database API requests must use a relative URI.");
        }

        var (client, endpoint) = await connection.ConnectAsync(cancellationToken)
            .ConfigureAwait(false);
        var absoluteUri = new Uri(endpoint.BaseUri, request.RequestUri);
        request.RequestUri = absoluteUri;
        request.Headers.TryAddWithoutValidation(
            MagicAuthorizationHeaders.Application,
            options.Application.ToString());
        var credential = await credentialProvider.GetCredentialAsync(
            new DatabaseApiCredentialContext(
                endpoint.BaseUri,
                request.Method,
                absoluteUri,
                options.Application),
            cancellationToken).ConfigureAwait(false);
        credential?.Apply(request);

        try
        {
            return await client.SendAsync(request, completionOption, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            connection.Invalidate(exception);
            throw;
        }
    }

    private CancellationTokenSource CreateRequestCancellation(
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(options.RequestTimeout);
        }
        return source;
    }
}
