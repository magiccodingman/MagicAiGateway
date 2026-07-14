using SharedMagic.Configuration;
using SharedMagic.Routing;

namespace SharedMagic.Tests;

public sealed class RequestSchedulerTests
{
    private sealed record Target(string Id, bool IsHealthy = true) : IRouteTarget;

    [Fact]
    public async Task ChoosesTheLeastBusyHealthyTarget()
    {
        var scheduler = new LeastBusyRequestScheduler<Target>(new QueueOptions
        {
            MaxConcurrentRequestsPerModel = 4,
            MaxQueuedRequestsPerModel = 4,
            MaximumQueueWaitSeconds = 2
        });
        scheduler.ReplaceTargets("model", [new("a"), new("b")]);

        await using var first = await scheduler.AcquireAsync("model", CancellationToken.None);
        await using var second = await scheduler.AcquireAsync("model", CancellationToken.None);

        Assert.NotEqual(first.Target.Id, second.Target.Id);
    }

    [Fact]
    public async Task RejectsRoutesWithoutHealthyTargets()
    {
        var scheduler = new LeastBusyRequestScheduler<Target>(new QueueOptions());
        scheduler.ReplaceTargets("model", [new("offline", false)]);
        await Assert.ThrowsAsync<RouteUnavailableException>(async () =>
        {
            await scheduler.AcquireAsync("model", CancellationToken.None);
        });
    }
}
