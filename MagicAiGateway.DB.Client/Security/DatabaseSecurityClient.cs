using System.Net.Http.Json;
using MagicAiGateway.DB.Client.Transport;
using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.Client.Security;

public interface IDatabaseSecurityClient
{
    Task<SecurityStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ApplicationAuthorizationDecision> EvaluateAsync(
        ApplicationAuthorizationRequest request,
        CancellationToken cancellationToken = default);
    Task<InitializeApplicationCredentialsResponse> InitializeApplicationCredentialsAsync(
        InitializeApplicationCredentialsRequest request,
        string? bootstrapToken = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiKeySummary>> ListApiKeysAsync(CancellationToken cancellationToken = default);
    Task<CreateApiKeyResponse> CreateApiKeyAsync(
        CreateApiKeyRequest request,
        CancellationToken cancellationToken = default);
    Task RevokeApiKeyAsync(Guid apiKeyId, CancellationToken cancellationToken = default);
}

public sealed class DatabaseSecurityClient(IRawDatabaseApiClient raw)
    : IDatabaseSecurityClient, IApplicationAuthorizationEvaluator
{
    public async Task<SecurityStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await raw.GetAsync("/v1/security/applications/status", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<SecurityStatusResponse>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ApplicationAuthorizationDecision> EvaluateAsync(
        ApplicationAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await raw.PostJsonAsync(
                "/v1/security/application-authorizations/evaluate",
                request,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<ApplicationAuthorizationDecision>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<InitializeApplicationCredentialsResponse> InitializeApplicationCredentialsAsync(
        InitializeApplicationCredentialsRequest request,
        string? bootstrapToken = null,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/security/application-credentials/initialize")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrWhiteSpace(bootstrapToken))
        {
            message.Headers.TryAddWithoutValidation(
                MagicAuthorizationHeaders.BootstrapToken,
                bootstrapToken);
        }
        using var response = await raw.SendAsync(message, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<InitializeApplicationCredentialsResponse>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ApiKeySummary>> ListApiKeysAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await raw.GetAsync("/v1/security/api-keys", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<List<ApiKeySummary>>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CreateApiKeyResponse> CreateApiKeyAsync(
        CreateApiKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await raw.PostJsonAsync(
                "/v1/security/api-keys",
                request,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<CreateApiKeyResponse>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RevokeApiKeyAsync(
        Guid apiKeyId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/v1/security/api-keys/{apiKeyId:D}");
        using var response = await raw.SendAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException(
            $"The database API returned an empty {typeof(T).Name} response.");
}
