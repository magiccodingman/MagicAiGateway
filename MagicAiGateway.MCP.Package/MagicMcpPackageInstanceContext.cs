namespace MagicAiGateway.MCP.Package;

/// <summary>
/// Describes one independently started package instance. The framework registers this
/// context in the instance's dependency-injection container and initializes every
/// <see cref="MagicMcpToolController"/> with it before invoking a tool.
/// </summary>
public sealed class MagicMcpPackageInstanceContext
{
    internal MagicMcpPackageInstanceContext(Guid instanceId, ReadOnlyMemory<byte> configurationJson)
    {
        InstanceId = instanceId.ToString("D");
        StartedAtUtc = DateTimeOffset.UtcNow;
        ConfigurationJson = configurationJson;
    }

    /// <summary>Gets the opaque package instance ID in display form.</summary>
    public string InstanceId { get; }

    /// <summary>Gets when this package instance began starting.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Gets the original UTF-8 configuration JSON supplied by the host.</summary>
    public ReadOnlyMemory<byte> ConfigurationJson { get; }
}
