using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using MagicAiGateway.MCP.Package.Runtime;
using MagicAiGateway.MCP.Package.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace MagicAiGateway.MCP.Package;

/// <summary>
/// Defines the package manifest, dependency-injection recipe, hosted services, and
/// MCP capabilities used to create every independently started package instance.
/// </summary>
public sealed class MagicMcpPackageBuilder
{
    private readonly Assembly _packageAssembly;
    private int _built;

    internal MagicMcpPackageBuilder(Assembly packageAssembly)
    {
        _packageAssembly = packageAssembly;
        Services = new ServiceCollection();
        Package = new MagicMcpPackageOptionsBuilder();
        Mcp = Services.AddMcpServer();
    }

    /// <summary>Gets package-level manifest configuration.</summary>
    public MagicMcpPackageOptionsBuilder Package { get; }

    /// <summary>
    /// Gets normal .NET service registration. Each started package instance builds a
    /// separate service provider from this recipe.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Gets the official MCP server builder for advanced prompts, resources, filters,
    /// or handlers. Tool controllers are normally registered through generated
    /// <c>AddMcpTools()</c>.
    /// </summary>
    public IMcpServerBuilder Mcp { get; }

    /// <summary>Registers one generated controller type using per-invocation activation.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public MagicMcpPackageBuilder RegisterGeneratedToolController<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods |
            DynamicallyAccessedMemberTypes.PublicConstructors)] TController>()
        where TController : MagicMcpToolController
    {
        ThrowIfBuilt();
        Mcp.WithMagicMcpToolController<TController>();
        return this;
    }

    /// <summary>Freezes and registers this package definition.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Build()
    {
        if (Interlocked.Exchange(ref _built, 1) != 0)
        {
            throw new MagicMcpPackageConfigurationException(
                "The MagicAiGateway MCP package builder may only be built once.");
        }

        MagicMcpPackageManifest manifest = Package.BuildManifest();
        byte[] manifestJson = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            MagicMcpPackageJsonContext.Default.MagicMcpPackageManifest);

        MagicMcpPackageDefinition definition = new(
            _packageAssembly,
            manifest,
            manifestJson,
            Services.ToArray());

        MagicMcpPackageRegistry.Register(definition);
    }

    private void ThrowIfBuilt()
    {
        if (Volatile.Read(ref _built) != 0)
        {
            throw new InvalidOperationException("The package definition has already been built.");
        }
    }
}
