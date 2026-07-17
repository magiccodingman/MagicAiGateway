using System.ComponentModel;
using MagicAiGateway.MCP.Package;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;

namespace MagicAiGateway.MCP.Package.Template.MCP;

/// <summary>
/// MCP tool controllers behave like ASP.NET controllers: the framework creates a new
/// controller for each invocation, constructor dependencies come from DI, and the base
/// class supplies the current package instance context automatically.
/// </summary>
[McpServerToolType]
public sealed class ExampleTools(IConfiguration configuration) : MagicMcpToolController
{
    [McpServerTool(Name = "example_echo")]
    [Description("Echoes a message and identifies the package instance that handled it.")]
    public string Echo(
        [Description("The message to echo.")] string message)
    {
        return $"[{PackageInstance.InstanceId}] {message}";
    }

    [McpServerTool(Name = "example_instance_status")]
    [Description("Returns basic information about the current package instance.")]
    public object GetInstanceStatus()
    {
        return new
        {
            PackageInstance.InstanceId,
            PackageInstance.StartedAtUtc,
            ConfigurationExample = configuration["example:message"] ?? "not configured"
        };
    }
}
