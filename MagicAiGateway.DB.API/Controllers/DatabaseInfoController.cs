using MagicAiGateway.DB.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MagicAiGateway.DB.API.Controllers;

[ApiController]
public sealed class DatabaseInfoController : ControllerBase
{
    [HttpGet("/v1/database/info")]
    [RequireMagicApplication(MagicApplication.PrimaryApi, MagicApplication.Web, MagicApplication.Mcp)]
    public IActionResult GetInfo() => Ok(new
    {
        service = MagicFabricServices.Database,
        application = MagicApplication.DatabaseApi.ToString(),
        version = typeof(DatabaseInfoController).Assembly.GetName().Version?.ToString() ?? "1.0.0"
    });
}
