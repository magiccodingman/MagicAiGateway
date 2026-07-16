namespace MagicAiGateway.MCP.Package.Template.Services;

/// <summary>
/// Example singleton state shared by a background service and MCP tools within
/// one package instance. Every instance receives a separate singleton.
/// </summary>
public sealed class ExampleState
{
    private long _heartbeatCount;
    private long _lastHeartbeatUnixMilliseconds;

    public long HeartbeatCount => Interlocked.Read(ref _heartbeatCount);

    public DateTimeOffset? LastHeartbeatUtc
    {
        get
        {
            long value = Interlocked.Read(ref _lastHeartbeatUnixMilliseconds);
            return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    internal void RecordHeartbeat(DateTimeOffset timestamp)
    {
        Interlocked.Increment(ref _heartbeatCount);
        Interlocked.Exchange(ref _lastHeartbeatUnixMilliseconds, timestamp.ToUnixTimeMilliseconds());
    }
}
