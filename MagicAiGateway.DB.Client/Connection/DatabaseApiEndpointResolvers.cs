using System.Net.Http.Json;
using MagicAiGateway.Client.Connection;
using MagicAiGateway.Client.Transport;
using MagicAiGateway.DB.Client.Configuration;
using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.Client.Connection;

public sealed record DatabaseApiEndpointSnapshot(
    Uri BaseUri,
    Guid? PeerId,
    string? RootCertificateBase64,
    DateTimeOffset ValidUntil,
    FabricServiceDescriptor? Descriptor);

public interface IDatabaseApiEndpointResolver
{
    Task<DatabaseApiEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken = default);
    void Invalidate();
}

public sealed class GatewayDirectoryDatabaseApiEndpointResolver(
    IRawGatewayClient gateway,
    IGatewayConnection gatewayConnection,
    DatabaseApiClientOptions options) : IDatabaseApiEndpointResolver
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DatabaseApiEndpointSnapshot? _cached;

    public async Task<DatabaseApiEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (options.EndpointOverride is not null)
        {
            return new DatabaseApiEndpointSnapshot(
                Normalize(options.EndpointOverride),
                options.ExpectedPeerId,
                options.PinnedRootCertificateBase64,
                DateTimeOffset.MaxValue,
                null);
        }

        if (_cached is { } cached && cached.ValidUntil > DateTimeOffset.UtcNow) return cached;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } current && current.ValidUntil > DateTimeOffset.UtcNow) return current;
            using var response = await gateway.GetAsync(
                $"/v1/fabric/services/{Uri.EscapeDataString(MagicFabricServices.Database)}",
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var descriptor = await response.Content.ReadFromJsonAsync<FabricServiceDescriptor>(cancellationToken: cancellationToken).ConfigureAwait(false)
                             ?? throw new InvalidOperationException("The gateway returned an empty database service descriptor.");
            if (descriptor.Health is not FabricServiceHealth.Ready and not FabricServiceHealth.Degraded)
            {
                throw new InvalidOperationException($"The database service is {descriptor.Health}.");
            }

            var gatewayIsLoopback = gatewayConnection.Current?.BaseUri.IsLoopback == true;
            var endpoint = descriptor.Endpoints
                .Where(endpoint => gatewayIsLoopback || endpoint.Scope != FabricEndpointScope.Loopback)
                .OrderBy(endpoint => endpoint.Priority)
                .ThenBy(endpoint => endpoint.Scope)
                .Select(endpoint => Uri.TryCreate(endpoint.BaseUri, UriKind.Absolute, out var uri) ? uri : null)
                .FirstOrDefault(static uri => uri is not null)
                ?? throw new InvalidOperationException("The database service did not advertise a usable endpoint.");
            var refreshAt = Min(
                descriptor.LeaseExpiresAt.AddSeconds(-2),
                DateTimeOffset.UtcNow.Add(options.RefreshInterval));
            _cached = new DatabaseApiEndpointSnapshot(
                Normalize(endpoint),
                descriptor.PeerId,
                descriptor.RootCertificateBase64,
                refreshAt,
                descriptor);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
    private static Uri Normalize(Uri uri) => new(uri.ToString().TrimEnd('/') + "/");
}
