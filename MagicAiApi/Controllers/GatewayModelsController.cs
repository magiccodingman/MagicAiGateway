using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MagicAiApi.Controllers;

[ApiController]
public sealed class GatewayModelsController(GatewayNodeRegistry registry) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/v1/models")]
    public IActionResult Models() => Ok(new
    {
        @object = "list",
        data = registry.GetModels().Select(static model => new
        {
            id = model.Id,
            @object = "model",
            created = 0,
            owned_by = model.OwnedBy ?? "magic-ai-gateway"
        })
    });
}
