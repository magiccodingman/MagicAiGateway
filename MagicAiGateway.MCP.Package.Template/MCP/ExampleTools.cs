using System.ComponentModel;
using MagicAiGateway.MCP.Package.Template.Runtime;
using MagicAiGateway.MCP.Package.Template.Services;
using ModelContextProtocol.Server;

namespace MagicAiGateway.MCP.Package.Template.MCP;

/// <summary>
/// Example MCP tool class. It behaves much like a controller: the class receives
/// services through constructor injection and attributed methods become MCP tools.
/// </summary>
[McpServerToolType]
public sealed class ExampleTools(
    PackageInstanceContext instanceContext,
    ExampleState state,
    TimeProvider timeProvider)
{
    [McpServerTool(Name = "example_echo")]
    [Description("Echoes a message and identifies the package instance that handled it.")]
    public string Echo(
        [Description("The message to echo.")] string message)
    {
        return $"[{instanceContext.InstanceId}] {message}";
    }

    [McpServerTool(Name = "example_instance_status")]
    [Description("Returns status from this instance and its hosted background service.")]
    public string GetInstanceStatus()
    {
        string lastHeartbeat = state.LastHeartbeatUtc?.ToString("O") ?? "not recorded";

        return $"Instance {instanceContext.InstanceId} started at {instanceContext.StartedAtUtc:O}; " +
               $"current UTC time is {timeProvider.GetUtcNow():O}; " +
               $"background heartbeats: {state.HeartbeatCount}; last heartbeat: {lastHeartbeat}.";
    }
}
