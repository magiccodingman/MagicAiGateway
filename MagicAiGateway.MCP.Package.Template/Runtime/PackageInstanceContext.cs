namespace MagicAiGateway.MCP.Package.Template.Runtime;

/// <summary>
/// Describes one independently started package instance. Registering this object
/// in DI makes the instance identity and host-provided configuration available to
/// tools, hosted services, and other application services.
/// </summary>
public sealed class PackageInstanceContext
{
    internal PackageInstanceContext(Guid instanceId, ReadOnlyMemory<byte> configurationJson)
    {
        InstanceId = instanceId.ToString("D");
        StartedAtUtc = DateTimeOffset.UtcNow;
        ConfigurationJson = configurationJson;
    }

    public string InstanceId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public ReadOnlyMemory<byte> ConfigurationJson { get; }
}
