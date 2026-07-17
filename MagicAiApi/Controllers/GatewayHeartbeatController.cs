using MagicAiApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharedMagic.Contracts;
using SharedMagic.Security;

namespace MagicAiApi.Controllers;

[ApiController]
[Authorize(Policy = GatewayFabricPolicies.Node)]
public sealed class GatewayHeartbeatController(GatewayNodeRegistry registry) : ControllerBase
{
    [HttpPost("/fabric/v1/heartbeat")]
    public IActionResult Heartbeat([FromBody] NodeHeartbeat heartbeat)
    {
        var certificateId = User.FindFirst(FabricAuthenticationDefaults.PeerIdClaim)?.Value;
        if (!Guid.TryParse(certificateId, out var peerId) || peerId != heartbeat.NodeId)
        {
            return Forbid();
        }

        registry.Update(heartbeat);
        return Ok(new
        {
            accepted = true,
            heartbeat.NodeId,
            receivedAt = DateTimeOffset.UtcNow
        });
    }
}
