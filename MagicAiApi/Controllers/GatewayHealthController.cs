using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MagicAiApi.Controllers;

[ApiController]
public sealed class GatewayHealthController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/health/live")]
    public IActionResult Live() => Ok(new { status = "alive" });

    [AllowAnonymous]
    [HttpGet("/health/ready")]
    public IActionResult Ready() => Ok(new { status = "ready" });
}
