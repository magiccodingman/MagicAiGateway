using MagicAiGateway.DB.Client.Authentication;
using MagicAiGateway.DB.Client.Configuration;
using MagicAiGateway.DB.Client.Connection;
using MagicAiGateway.DB.Client.Security;
using MagicAiGateway.DB.Client.Transport;
using MagicAiGateway.DB.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MagicAiGateway.DB.Client.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMagicAiGatewayDatabaseClient(
        this IServiceCollection services,
        Action<DatabaseApiClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new DatabaseApiClientOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton<IDatabaseApiEndpointResolver,
            GatewayDirectoryDatabaseApiEndpointResolver>();
        services.TryAddSingleton<IDatabaseApiCredentialProvider>(
            _ => new StaticDatabaseApiCredentialProvider(options.ApiKey));
        services.AddSingleton<DatabaseApiConnection>();
        services.AddSingleton<IDatabaseApiConnection>(
            provider => provider.GetRequiredService<DatabaseApiConnection>());
        services.AddSingleton<RawDatabaseApiClient>();
        services.AddSingleton<IRawDatabaseApiClient>(
            provider => provider.GetRequiredService<RawDatabaseApiClient>());
        services.AddSingleton<DatabaseSecurityClient>();
        services.AddSingleton<IDatabaseSecurityClient>(
            provider => provider.GetRequiredService<DatabaseSecurityClient>());
        services.TryAddSingleton<IApplicationAuthorizationEvaluator>(
            provider => provider.GetRequiredService<DatabaseSecurityClient>());
        services.AddHostedService<DatabaseEndpointRefreshService>();
        return services;
    }
}
