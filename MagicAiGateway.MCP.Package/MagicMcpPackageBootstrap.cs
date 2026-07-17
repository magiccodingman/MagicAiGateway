using System.ComponentModel;
using System.Reflection;
using MagicAiGateway.MCP.Package.Runtime;

namespace MagicAiGateway.MCP.Package;

/// <summary>
/// Infrastructure used by the generated package bootstrap. Package applications should
/// configure the supplied <see cref="MagicMcpPackageBuilder"/> rather than calling this type.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MagicMcpPackageBootstrap
{
    public static MagicMcpPackageBuilder CreateBuilder(Assembly packageAssembly)
    {
        ArgumentNullException.ThrowIfNull(packageAssembly);
        return new MagicMcpPackageBuilder(packageAssembly);
    }

    public static void RecordConfigurationFailure(Exception exception) =>
        MagicMcpPackageRegistry.RecordFailure(exception);
}
