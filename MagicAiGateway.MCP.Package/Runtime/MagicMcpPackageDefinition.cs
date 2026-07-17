using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MagicAiGateway.MCP.Package.Runtime;

internal sealed class MagicMcpPackageDefinition(
    Assembly packageAssembly,
    MagicMcpPackageManifest manifest,
    byte[] manifestJsonUtf8,
    ServiceDescriptor[] serviceDescriptors)
{
    public Assembly PackageAssembly { get; } = packageAssembly;
    public MagicMcpPackageManifest Manifest { get; } = manifest;
    public byte[] ManifestJsonUtf8 { get; } = manifestJsonUtf8;
    public ServiceDescriptor[] ServiceDescriptors { get; } = serviceDescriptors;
}
