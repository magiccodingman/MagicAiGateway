using MagicAiGateway.MCP.Package;
using Microsoft.Extensions.Hosting;

namespace MagicAiGateway.MCP.Package.Template.Services;

/// <summary>
/// Demonstrates that ordinary hosted services start once for each package instance and
/// receive graceful cancellation when that specific instance stops.
/// </summary>
public sealed class ExampleBackgroundService(
    MagicMcpPackageInstanceContext packageInstance) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initialize package-owned workers, connections, queues, or subscriptions here.
        _ = packageInstance.InstanceId;

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal instance shutdown.
        }
    }
}
