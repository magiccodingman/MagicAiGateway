using System.Collections.Concurrent;
using SharedMagic.Configuration;

namespace SharedMagic.Routing;

public interface IRouteTarget
{
    string Id { get; }
    bool IsHealthy { get; }
}

public sealed class RouteUnavailableException(string message) : Exception(message);
public sealed class RouteQueueFullException(string message) : Exception(message);

public sealed class DestinationLease<TTarget> : IAsyncDisposable where TTarget : IRouteTarget
{
    private readonly Action _release;
    private int _released;

    internal DestinationLease(TTarget target, Action release)
    {
        Target = target;
        _release = release;
    }

    public TTarget Target { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }

        return ValueTask.CompletedTask;
    }
}

public interface IRequestScheduler<TTarget> where TTarget : IRouteTarget
{
    void ReplaceTargets(string routeKey, IEnumerable<TTarget> targets);
    ValueTask<DestinationLease<TTarget>> AcquireAsync(string routeKey, CancellationToken cancellationToken);
    int GetActiveRequests(string targetId);
}

public sealed class LeastBusyRequestScheduler<TTarget> : IRequestScheduler<TTarget> where TTarget : IRouteTarget
{
    private sealed class RouteState(int maxConcurrency)
    {
        public SemaphoreSlim Concurrency { get; } = new(maxConcurrency, maxConcurrency);
        public int Queued;
        public long RoundRobin;
    }

    private readonly QueueOptions _options;
    private readonly ConcurrentDictionary<string, TTarget[]> _targets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RouteState> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _active = new(StringComparer.Ordinal);

    public LeastBusyRequestScheduler(QueueOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRequestsPerModel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxQueuedRequestsPerModel);
        _options = options;
    }

    public void ReplaceTargets(string routeKey, IEnumerable<TTarget> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        _targets[routeKey] = targets.DistinctBy(static x => x.Id).ToArray();
    }

    public int GetActiveRequests(string targetId) => _active.TryGetValue(targetId, out var count) ? count : 0;

    public async ValueTask<DestinationLease<TTarget>> AcquireAsync(string routeKey, CancellationToken cancellationToken)
    {
        if (!_targets.TryGetValue(routeKey, out var candidates) || candidates.All(static x => !x.IsHealthy))
        {
            throw new RouteUnavailableException($"No healthy destination is available for route '{routeKey}'.");
        }

        var route = _routes.GetOrAdd(routeKey, _ => new RouteState(_options.MaxConcurrentRequestsPerModel));
        var queued = Interlocked.Increment(ref route.Queued);
        if (queued > _options.MaxQueuedRequestsPerModel)
        {
            Interlocked.Decrement(ref route.Queued);
            throw new RouteQueueFullException($"The queue for route '{routeKey}' is full.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.MaximumQueueWaitSeconds));
            await route.Concurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RouteQueueFullException($"The queue wait for route '{routeKey}' timed out.");
        }
        finally
        {
            Interlocked.Decrement(ref route.Queued);
        }

        candidates = _targets.TryGetValue(routeKey, out var latest) ? latest : candidates;
        var healthy = candidates.Where(static x => x.IsHealthy).ToArray();
        if (healthy.Length == 0)
        {
            route.Concurrency.Release();
            throw new RouteUnavailableException($"No healthy destination is available for route '{routeKey}'.");
        }

        var minActive = healthy.Min(x => GetActiveRequests(x.Id));
        var leastBusy = healthy.Where(x => GetActiveRequests(x.Id) == minActive).ToArray();
        var index = (int)(Interlocked.Increment(ref route.RoundRobin) % leastBusy.Length);
        var selected = leastBusy[index];
        _active.AddOrUpdate(selected.Id, 1, static (_, value) => value + 1);

        return new DestinationLease<TTarget>(selected, () =>
        {
            _active.AddOrUpdate(selected.Id, 0, static (_, value) => Math.Max(0, value - 1));
            route.Concurrency.Release();
        });
    }
}
