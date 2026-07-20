using MagicAiGateway.DB.Client.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MagicAiGateway.DB.Client.Connection;

public sealed class DatabaseEndpointRefreshService(
    IDatabaseApiEndpointResolver resolver,
    DatabaseApiClientOptions options,
    ILogger<DatabaseEndpointRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RefreshInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await resolver.ResolveAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Database API endpoint refresh failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
        }
    }
}
