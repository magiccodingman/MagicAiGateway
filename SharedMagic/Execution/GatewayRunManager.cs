using System.Collections.Concurrent;

namespace SharedMagic.Execution;

public interface IGatewayRunManager
{
    GatewayRunLease Start(
        GatewayRunContext context,
        TimeSpan duration,
        CancellationToken requestCancellation,
        CancellationToken shutdownCancellation = default);

    bool TryGet(Guid runId, out GatewayRunSnapshot? snapshot);
    IReadOnlyCollection<GatewayRunSnapshot> GetActiveRuns();
    ValueTask<bool> CancelAsync(Guid runId, string reason);
}

public sealed class GatewayRunManager : IGatewayRunManager
{
    private readonly ConcurrentDictionary<Guid, GatewayRunLease> _runs = new();

    public GatewayRunLease Start(
        GatewayRunContext context,
        TimeSpan duration,
        CancellationToken requestCancellation,
        CancellationToken shutdownCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));

        var linked = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, shutdownCancellation);
        linked.CancelAfter(duration);
        var lease = new GatewayRunLease(this, context, linked, DateTimeOffset.UtcNow.Add(duration));
        if (!_runs.TryAdd(context.RunId, lease))
        {
            linked.Dispose();
            throw new InvalidOperationException($"Run '{context.RunId}' is already registered.");
        }

        context.Journal.Record("run.started", new { durationSeconds = duration.TotalSeconds });
        return lease;
    }

    public bool TryGet(Guid runId, out GatewayRunSnapshot? snapshot)
    {
        if (_runs.TryGetValue(runId, out var lease))
        {
            snapshot = lease.CreateSnapshot();
            return true;
        }

        snapshot = null;
        return false;
    }

    public IReadOnlyCollection<GatewayRunSnapshot> GetActiveRuns() =>
        _runs.Values.Select(static lease => lease.CreateSnapshot()).ToArray();

    public ValueTask<bool> CancelAsync(Guid runId, string reason)
    {
        if (!_runs.TryGetValue(runId, out var lease)) return ValueTask.FromResult(false);
        lease.Context.Journal.Record("run.cancel.requested", reason);
        lease.Cancel();
        return ValueTask.FromResult(true);
    }

    internal void Complete(GatewayRunLease lease)
    {
        _runs.TryRemove(new KeyValuePair<Guid, GatewayRunLease>(lease.Context.RunId, lease));
    }
}

public sealed class GatewayRunLease : IAsyncDisposable
{
    private readonly GatewayRunManager _owner;
    private readonly CancellationTokenSource _cancellation;
    private int _disposed;

    internal GatewayRunLease(
        GatewayRunManager owner,
        GatewayRunContext context,
        CancellationTokenSource cancellation,
        DateTimeOffset deadline)
    {
        _owner = owner;
        Context = context;
        _cancellation = cancellation;
        Deadline = deadline;
    }

    public GatewayRunContext Context { get; }
    public DateTimeOffset Deadline { get; }
    public CancellationToken CancellationToken => _cancellation.Token;

    public void Cancel() => _cancellation.Cancel();

    internal GatewayRunSnapshot CreateSnapshot() => new(
        Context.RunId,
        Context.Service.Name,
        Context.Application.Name,
        Context.Agent?.Name,
        Context.StartedAt,
        Context.Status,
        Context.Usage.Calls.Count,
        Context.ToolCalls);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _owner.Complete(this);
        _cancellation.Dispose();
        Context.Journal.Record("run.disposed");
        return ValueTask.CompletedTask;
    }
}
