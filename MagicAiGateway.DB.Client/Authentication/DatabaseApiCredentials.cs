using System.Net.Http.Headers;
using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.Client.Authentication;

public sealed record DatabaseApiCredentialContext(
    Uri DatabaseApiEndpoint,
    HttpMethod Method,
    Uri RequestUri,
    MagicApplication Application);

public sealed record DatabaseApiCredential(string Scheme, string Parameter)
{
    public static DatabaseApiCredential Bearer(string apiKey) => new("Bearer", apiKey);

    internal void Apply(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, Parameter);
    }
}

public interface IDatabaseApiCredentialProvider
{
    ValueTask<DatabaseApiCredential?> GetCredentialAsync(
        DatabaseApiCredentialContext context,
        CancellationToken cancellationToken = default);
}

public sealed class StaticDatabaseApiCredentialProvider(string? apiKey) : IDatabaseApiCredentialProvider
{
    public ValueTask<DatabaseApiCredential?> GetCredentialAsync(
        DatabaseApiCredentialContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DatabaseApiCredential?>(
            string.IsNullOrWhiteSpace(apiKey) ? null : DatabaseApiCredential.Bearer(apiKey));
    }
}

public sealed class DelegateDatabaseApiCredentialProvider(
    Func<DatabaseApiCredentialContext, CancellationToken, ValueTask<DatabaseApiCredential?>> provider)
    : IDatabaseApiCredentialProvider
{
    public ValueTask<DatabaseApiCredential?> GetCredentialAsync(
        DatabaseApiCredentialContext context,
        CancellationToken cancellationToken = default) =>
        provider(context, cancellationToken);
}
