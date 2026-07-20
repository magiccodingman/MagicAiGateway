using MagicAiGateway.DB.API.Database;
using Microsoft.AspNetCore.Mvc;

namespace MagicAiGateway.DB.API.Controllers;

[ApiController]
public sealed class HealthController(DatabaseReadinessState readiness) : ControllerBase
{
    [HttpGet("/health/live")]
    public IActionResult Live() => Ok(new { status = "live", checkedAt = DateTimeOffset.UtcNow });

    [HttpGet("/health/ready")]
    public IActionResult Ready() => readiness.IsReady
        ? Ok(new { status = "ready", checkedAt = DateTimeOffset.UtcNow })
        : StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            status = "not_ready",
            error = readiness.Error,
            checkedAt = DateTimeOffset.UtcNow
        });
}
