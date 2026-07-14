using Microsoft.AspNetCore.Mvc;
using SharedMagic.Security;

namespace MagicAiNode.Controllers;

[ApiController]
[MagicFabricAuthorize]
public sealed class NodeInventoryController(BackendCatalog catalog) : ControllerBase
{
    [HttpGet("/internal/v1/status")]
    public IActionResult Status() => Ok(catalog.GetSnapshots());

    [HttpGet("/internal/v1/models")]
    public IActionResult Models() => Ok(catalog.GetSnapshots()
        .Where(static snapshot => snapshot.Healthy)
        .SelectMany(static snapshot => snapshot.Models)
        .GroupBy(static model => model.Id, StringComparer.Ordinal)
        .Select(static group => group.First()));
}
