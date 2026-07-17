using MagicAiGateway.DB.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharedMagic.Security;

namespace MagicAiApi.Controllers;

[ApiController]
public sealed class GatewayFabricServicesController(
    GatewayFabricServiceRegistry services,
    GatewayFabricPeerRegistry peers) : ControllerBase
{
    [Authorize(Policy = GatewayFabricPolicies.DatabaseApi)]
    [HttpPost("/fabric/v1/services/heartbeat")]
    public IActionResult Heartbeat([FromBody] FabricServiceHeartbeat heartbeat)
    {
        var value = User.FindFirst(FabricAuthenticationDefaults.PeerIdClaim)?.Value;
        if (!Guid.TryParse(value, out var peerId) ||
            peerId != heartbeat.PeerId ||
            peerId != heartbeat.InstanceId ||
            peers.Find(peerId)?.Role != FabricPeerRoles.DatabaseApi ||
            !string.Equals(
                heartbeat.ServiceName,
                MagicFabricServices.Database,
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        try
        {
            services.Update(heartbeat);
            return Ok(new { accepted = true, heartbeat.InstanceId, receivedAt = DateTimeOffset.UtcNow });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpGet("/v1/fabric/services/database")]
    [RequireMagicApplication(MagicApplication.Web, MagicApplication.Mcp)]
    public ActionResult<FabricServiceDescriptor> Database()
    {
        var descriptor = services.Find(MagicFabricServices.Database);
        return descriptor is null
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "The database API is not registered, healthy, or statically configured."
            })
            : Ok(descriptor);
    }
}
