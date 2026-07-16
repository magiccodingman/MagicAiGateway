using MagicAiGateway.Client.Authentication;
using MagicAiGateway.Client.Chat;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Connection;
using MagicAiGateway.Client.ProtocolServices;
using MagicAiGateway.Client.Security;
using MagicAiGateway.Client.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MagicAiGateway.Client.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMagicAiGatewayClient(
        this IServiceCollection services,
        Action<MagicAiGatewayClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MagicAiGatewayClientOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IGatewayTrustStore, FileGatewayTrustStore>();
        services.TryAddSingleton<IGatewayCredentialProvider>(
            _ => GatewayCredentialProviderFactory.Create(options.ApiKey));
        services.AddSingleton(provider => new GatewayConnection(
            provider.GetRequiredService<MagicAiGatewayClientOptions>().CloneWithoutCredentials(),
            provider.GetRequiredService<IGatewayTrustStore>(),
            provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
        services.AddSingleton<IGatewayConnection>(provider => provider.GetRequiredService<GatewayConnection>());
        services.AddSingleton<RawGatewayClient>();
        services.AddSingleton<IRawGatewayClient>(provider => provider.GetRequiredService<RawGatewayClient>());
        services.AddSingleton<MagicChatClient>();
        services.AddSingleton<IMagicChatClient>(provider => provider.GetRequiredService<MagicChatClient>());
        services.AddSingleton<MagicProtocolClient>();
        services.AddSingleton<IMagicProtocolClient>(provider => provider.GetRequiredService<MagicProtocolClient>());
        services.AddSingleton<IMagicAiGatewayClient>(provider => new MagicAiGatewayClient(
            provider.GetRequiredService<GatewayConnection>(),
            provider.GetRequiredService<IRawGatewayClient>(),
            provider.GetRequiredService<IMagicChatClient>(),
            provider.GetRequiredService<IMagicProtocolClient>(),
            ownsConnection: false));

        return services;
    }
}
