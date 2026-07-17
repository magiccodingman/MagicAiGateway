namespace MagicAiGateway.MCP.Package;

/// <summary>Thrown when a package definition is incomplete or violates the framework contract.</summary>
public sealed class MagicMcpPackageConfigurationException : Exception
{
    public MagicMcpPackageConfigurationException(string message)
        : base(message)
    {
    }

    public MagicMcpPackageConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
