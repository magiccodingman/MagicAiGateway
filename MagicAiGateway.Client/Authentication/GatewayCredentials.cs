using System.Net.Http.Headers;

namespace MagicAiGateway.Client.Authentication;

public sealed record GatewayCredential(
    string Scheme,
    string Parameter,
    IReadOnlyDictionary<string, string>? AdditionalHeaders = null)
{
    public static GatewayCredential Bearer(string token) => new("Bearer", token);

    internal void Apply(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Headers.Authorization is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, Parameter);
        }

        if (AdditionalHeaders is null) return;
        foreach (var header in AdditionalHeaders)
        {
            if (!request.Headers.Contains(header.Key))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }
}

public sealed record GatewayCredentialContext(
    Uri GatewayEndpoint,
    HttpMethod Method,
    Uri RequestUri);

public interface IGatewayCredentialProvider
{
    ValueTask<GatewayCredential?> GetCredentialAsync(
        GatewayCredentialContext context,
        CancellationToken cancellationToken = default);
}

public sealed class AnonymousGatewayCredentialProvider : IGatewayCredentialProvider
{
    public static AnonymousGatewayCredentialProvider Instance { get; } = new();

    private AnonymousGatewayCredentialProvider()
    {
    }

    public ValueTask<GatewayCredential?> GetCredentialAsync(
        GatewayCredentialContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<GatewayCredential?>(null);
    }
}

public sealed class StaticApiKeyCredentialProvider : IGatewayCredentialProvider
{
    private readonly string _apiKey;

    public StaticApiKeyCredentialProvider(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("A non-empty API key is required.", nameof(apiKey));
        }

        _apiKey = apiKey;
    }

    public ValueTask<GatewayCredential?> GetCredentialAsync(
        GatewayCredentialContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<GatewayCredential?>(GatewayCredential.Bearer(_apiKey));
    }
}

public sealed class DelegateGatewayCredentialProvider(
    Func<GatewayCredentialContext, CancellationToken, ValueTask<GatewayCredential?>> callback)
    : IGatewayCredentialProvider
{
    private readonly Func<GatewayCredentialContext, CancellationToken, ValueTask<GatewayCredential?>> _callback =
        callback ?? throw new ArgumentNullException(nameof(callback));

    public ValueTask<GatewayCredential?> GetCredentialAsync(
        GatewayCredentialContext context,
        CancellationToken cancellationToken = default) =>
        _callback(context, cancellationToken);
}

internal static class GatewayCredentialProviderFactory
{
    public static IGatewayCredentialProvider Create(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey)
            ? AnonymousGatewayCredentialProvider.Instance
            : new StaticApiKeyCredentialProvider(apiKey);
}
