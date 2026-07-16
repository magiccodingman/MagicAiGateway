using Microsoft.Extensions.Hosting;

namespace MagicAiGateway.MCP.Package.Template.Services;

/// <summary>
/// Demonstrates that normal hosted background services start and stop with each
/// package instance.
/// </summary>
public sealed class ExampleBackgroundService(
    ExampleState state,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.RecordHeartbeat(timeProvider.GetUtcNow());

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5), timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                state.RecordHeartbeat(timeProvider.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal instance shutdown.
        }
    }
}
