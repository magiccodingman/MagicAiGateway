using MagicAiGateway.MCP.Package;
using MagicAiGateway.MCP.Package.Template.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MagicAiGateway.MCP.Package.Template;

/// <summary>
/// Defines this package's metadata, services, hosted workers, and MCP capabilities.
/// The framework generates the NativeAOT bootstrap and builds one independent host
/// from this recipe for every started package instance.
/// </summary>
public static class Program
{
    [MagicMcpPackage]
    public static void Configure(MagicMcpPackageBuilder builder)
    {
        builder.Package.ConfigureManifest(manifest =>
        {
            manifest.Name = "MagicAiGateway MCP Package Template";
            manifest.Version = "0.1.0";
            manifest.Description =
                "A controller-style C# template for compiled MagicAiGateway MCP packages.";
        });

        // Alternative: load package identity from a UTF-8 JSON file beside the
        // published library instead of configuring it inline.
        // builder.Package.AddManifestFile("magic-mcp-package.json");

        // Normal hosted services start and stop independently for every package instance.
        builder.Services.AddHostedService<ExampleBackgroundService>();

        // Generated at compile time: discovers every [McpServerToolType] class that
        // derives from MagicMcpToolController and registers it without AOT-unsafe
        // assembly scanning or a manually maintained type list.
        builder.AddMcpTools();
    }
}
