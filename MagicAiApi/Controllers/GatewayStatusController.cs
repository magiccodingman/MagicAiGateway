using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharedMagic.Security;

namespace MagicAiApi.Controllers;

[ApiController]
public sealed class GatewayStatusController(GatewayNodeRegistry registry) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/status")]
    public IActionResult Status()
    {
        var nodes = registry.GetNodes();
        var models = registry.GetModels();
        var onlineNodes = nodes.Count(static node => node.Online);
        var healthyBackends = nodes
            .Where(static node => node.Online)
            .SelectMany(static node => node.Backends)
            .Count(static backend => backend.Healthy);

        return Ok(new
        {
            status = onlineNodes == 0 ? "no-capacity" : "available",
            generatedAt = DateTimeOffset.UtcNow,
            summary = new
            {
                totalNodes = nodes.Count,
                onlineNodes,
                offlineNodes = nodes.Count - onlineNodes,
                healthyBackends,
                modelCount = models.Count
            },
            models = models.Select(static model => model.Id),
            nodes = nodes.Select(static node => new
            {
                node.NodeId,
                node.Name,
                node.Online,
                node.LastSeenAt,
                backendCount = node.Backends.Count,
                healthyBackendCount = node.Backends.Count(static backend => backend.Healthy),
                models = node.Backends
                    .Where(static backend => backend.Healthy)
                    .SelectMany(static backend => backend.Models)
                    .Select(static model => model.Id)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static model => model, StringComparer.Ordinal)
            })
        });
    }

    [MagicFabricAuthorize]
    [HttpGet("/internal/v1/status")]
    public IActionResult InternalStatus() => Ok(new
    {
        generatedAt = DateTimeOffset.UtcNow,
        nodes = registry.GetNodes(),
        models = registry.GetModels()
    });

    [MagicFabricAuthorize]
    [HttpGet("/internal/v1/nodes")]
    public IActionResult Nodes() => Ok(registry.GetNodes());
}
