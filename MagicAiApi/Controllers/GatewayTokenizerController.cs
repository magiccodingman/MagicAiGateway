using Microsoft.AspNetCore.Mvc;
using SharedMagic.Contracts;
using SharedMagic.Security;

namespace MagicAiApi.Controllers;

[ApiController]
public sealed class GatewayTokenizerController(
    GatewayNodeRegistry registry,
    GatewayNodeClient client) : ControllerBase
{
    [MagicFabricAuthorize]
    [HttpGet("/internal/v1/tokenizers/{model}")]
    public async Task<IActionResult> Get(string model, CancellationToken cancellationToken)
    {
        var target = registry.FindAnyForModel(model);
        if (target is null)
        {
            return NotFound(OpenAiErrors.NotFound($"No node currently provides model '{model}'."));
        }

        using var response = await client
            .GetAsync(target, $"/internal/v1/tokenizers/{Uri.EscapeDataString(model)}", cancellationToken)
            .ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
            Content = content
        };
    }
}
