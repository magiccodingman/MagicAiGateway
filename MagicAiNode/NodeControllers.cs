using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharedMagic.Contracts;
using SharedMagic.Security;

namespace MagicAiNode;

[ApiController]
public sealed class NodeHealthController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/health/live")]
    public IActionResult Live() => Ok(new { status = "alive" });

    [AllowAnonymous]
    [HttpGet("/health/ready")]
    public IActionResult Ready() => Ok(new { status = "ready" });
}

[ApiController]
[MagicFabricAuthorize]
public sealed class NodeInventoryController(BackendCatalog catalog) : ControllerBase
{
    [HttpGet("/internal/v1/status")]
    public IActionResult Status() => Ok(catalog.GetSnapshots());

    [HttpGet("/internal/v1/models")]
    public IActionResult Models() => Ok(catalog.GetSnapshots()
        .Where(static x => x.Healthy)
        .SelectMany(static x => x.Models)
        .GroupBy(static x => x.Id, StringComparer.Ordinal)
        .Select(static x => x.First()));
}

[ApiController]
[MagicFabricAuthorize]
public sealed class NodeTokenizerController(
    BackendCatalog catalog,
    IEnumerable<IAiBackendAdapter> adapters) : ControllerBase
{
    [HttpGet("/internal/v1/tokenizers/{model}")]
    public async Task<IActionResult> Get(string model, CancellationToken cancellationToken)
    {
        var target = catalog.FindForModel(model);
        if (target is null) return NotFound(OpenAiErrors.NotFound($"No local backend provides model '{model}'."));
        var adapter = adapters.Single(x => x.Kind == target.Options.Kind);
        var descriptor = await adapter.GetTokenizerAsync(target.Options, model, cancellationToken).ConfigureAwait(false);
        return descriptor is null ? NotFound() : Ok(descriptor);
    }

    [HttpPost("/internal/v1/tokenize")]
    public async Task<IActionResult> Tokenize([FromBody] TokenizeRequest request, CancellationToken cancellationToken)
    {
        var target = catalog.FindForModel(request.Model);
        if (target is null) return NotFound(OpenAiErrors.NotFound($"No local backend provides model '{request.Model}'."));
        var adapter = adapters.Single(x => x.Kind == target.Options.Kind);
        var result = await adapter.TokenizeAsync(target.Options, request, cancellationToken).ConfigureAwait(false);
        return result is null ? StatusCode(StatusCodes.Status502BadGateway) : Ok(result);
    }
}
