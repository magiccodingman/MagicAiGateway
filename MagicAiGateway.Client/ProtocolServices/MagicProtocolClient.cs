using System.Net.Http.Json;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Transport;
using MagicAiGateway.Protocol;

namespace MagicAiGateway.Client.ProtocolServices;

public interface IMagicProtocolClient
{
    Task<MagicServiceCatalog> GetServicesAsync(CancellationToken cancellationToken = default);
    Task<MagicServiceDescriptor> GetServiceAsync(
        string name,
        int version = 1,
        CancellationToken cancellationToken = default);

    MagicAiGatewayEnvelope CreateEnvelope<TOptions>(
        string serviceName,
        TOptions options,
        string? agent = null,
        int serviceVersion = 1,
        TimeSpan? requestedRunTimeout = null,
        string responseMode = MagicResponseModes.Compatible);
}

public sealed class MagicProtocolClient(
    IRawGatewayClient raw,
    MagicAiGatewayClientOptions options) : IMagicProtocolClient
{
    public async Task<MagicServiceCatalog> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await raw.GetAsync(MagicAiGatewayProtocol.ServicesPath, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MagicServiceCatalog>(
                   MagicProtocolJson.Options,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The gateway returned an empty service catalog.");
    }

    public async Task<MagicServiceDescriptor> GetServiceAsync(
        string name,
        int version = 1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A service name is required.", nameof(name));
        using var response = await raw.GetAsync(
                $"{MagicAiGatewayProtocol.ServicesPath}/{Uri.EscapeDataString(name)}?version={version}",
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MagicServiceDescriptor>(
                   MagicProtocolJson.Options,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The gateway returned an empty service descriptor.");
    }

    public MagicAiGatewayEnvelope CreateEnvelope<TOptions>(
        string serviceName,
        TOptions serviceOptions,
        string? agent = null,
        int serviceVersion = 1,
        TimeSpan? requestedRunTimeout = null,
        string responseMode = MagicResponseModes.Compatible)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) throw new ArgumentException("A service name is required.", nameof(serviceName));
        if (requestedRunTimeout is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedRunTimeout));
        }

        return new MagicAiGatewayEnvelope
        {
            Application = options.ApplicationId,
            Agent = agent,
            Service = MagicServiceInvocation.Create(serviceName, serviceOptions, serviceVersion),
            RequestedRunTimeoutSeconds = requestedRunTimeout is null
                ? null
                : checked((int)Math.Ceiling(requestedRunTimeout.Value.TotalSeconds)),
            ResponseMode = responseMode
        };
    }
}
