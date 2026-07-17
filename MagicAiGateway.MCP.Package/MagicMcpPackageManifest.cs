using System.Text.Json.Serialization;

namespace MagicAiGateway.MCP.Package;

/// <summary>
/// Describes a compiled MagicAiGateway MCP package. Package authors configure the
/// identity fields; ABI, transport, and instance-capability fields are controlled by
/// the framework so they cannot drift away from the binary contract.
/// </summary>
public sealed class MagicMcpPackageManifest
{
    internal const string ProtocolName = "magic-ai-gateway-mcp-package";
    internal const int CurrentAbiVersion = 1;

    /// <summary>Gets the package protocol identifier.</summary>
    public string Protocol { get; internal set; } = ProtocolName;

    /// <summary>Gets the native package ABI implemented by this framework.</summary>
    public int AbiVersion { get; internal set; } = CurrentAbiVersion;

    /// <summary>Gets or sets the required human-readable package name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the required semantic package version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional package description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets an optional package author or organization.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets an optional project or documentation URL.</summary>
    public string? Homepage { get; set; }

    /// <summary>
    /// Gets or sets optional package-defined string metadata. Hosts must ignore keys
    /// they do not understand.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>Gets the opaque instance-ID contract.</summary>
    public MagicMcpInstanceIdManifest InstanceId { get; internal set; } = new();

    /// <summary>Gets the MCP message transport contract.</summary>
    public MagicMcpTransportManifest Transport { get; internal set; } = new();

    /// <summary>Gets package-runtime capabilities guaranteed by this framework.</summary>
    public MagicMcpPackageCapabilitiesManifest Capabilities { get; internal set; } = new();

    internal void NormalizeRuntimeFields()
    {
        Protocol = ProtocolName;
        AbiVersion = CurrentAbiVersion;
        InstanceId = new();
        Transport = new();
        Capabilities = new();
    }

    internal void CopyAuthorFieldsFrom(MagicMcpPackageManifest source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Name = source.Name;
        Version = source.Version;
        Description = source.Description;
        Author = source.Author;
        Homepage = source.Homepage;
        Metadata = source.Metadata is null
            ? null
            : new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal);
    }
}

/// <summary>Describes the opaque package instance identifier.</summary>
public sealed class MagicMcpInstanceIdManifest
{
    public int SizeBytes { get; internal set; } = 16;
    public string Encoding { get; internal set; } = "opaque";
}

/// <summary>Describes how MCP JSON-RPC messages cross the native ABI.</summary>
public sealed class MagicMcpTransportManifest
{
    public string Encoding { get; internal set; } = "utf-8";
    public string Framing { get; internal set; } = "one MCP JSON-RPC message per send or receive";
    public bool Duplex { get; internal set; } = true;
}

/// <summary>Describes capabilities guaranteed by the package runtime.</summary>
public sealed class MagicMcpPackageCapabilitiesManifest
{
    public bool MultipleInstances { get; internal set; } = true;
    public bool ServerToHostMessages { get; internal set; } = true;
}
