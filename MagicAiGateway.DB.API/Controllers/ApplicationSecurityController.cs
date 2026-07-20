using System.Net;
using System.Security.Cryptography;
using System.Text;
using MagicAiGateway.DB.API.Configuration;
using MagicAiGateway.DB.API.Security;
using MagicAiGateway.DB.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MagicAiGateway.DB.API.Controllers;

[ApiController]
public sealed class ApplicationSecurityController(
    ApplicationSecurityService security,
    IOptions<ApplicationSecurityOptions> options) : ControllerBase
{
    [HttpGet("/v1/security/applications/status")]
    [RequireMagicApplication(MagicApplication.PrimaryApi, MagicApplication.Web, MagicApplication.Mcp)]
    public async Task<ActionResult<SecurityStatusResponse>> Status(CancellationToken cancellationToken) =>
        Ok(await security.GetStatusAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("/v1/security/application-authorizations/evaluate")]
    [RequireMagicApplication(MagicApplication.PrimaryApi)]
    public async Task<ActionResult<ApplicationAuthorizationDecision>> Evaluate(
        [FromBody] ApplicationAuthorizationRequest request,
        CancellationToken cancellationToken) =>
        Ok(await security.EvaluateAsync(request, cancellationToken).ConfigureAwait(false));

    [HttpPost("/v1/security/application-credentials/initialize")]
    public async Task<ActionResult<InitializeApplicationCredentialsResponse>> Initialize(
        [FromBody] InitializeApplicationCredentialsRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest() && !HasBootstrapToken())
        {
            return Unauthorized(new { message = "Initial application credentials require loopback access or X-Magic-Bootstrap-Token." });
        }

        try
        {
            return Ok(await security.InitializeApplicationsAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { message = exception.Message });
        }
    }

    [HttpGet("/v1/security/api-keys")]
    [RequireMagicApplication(MagicApplication.Web)]
    [RequireMagicRole(BuiltInRole.Administrator)]
    public async Task<ActionResult<IReadOnlyList<ApiKeySummary>>> GetApiKeys(CancellationToken cancellationToken) =>
        Ok(await security.GetApiKeysAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("/v1/security/api-keys")]
    [RequireMagicApplication(MagicApplication.Web)]
    [RequireMagicRole(BuiltInRole.Administrator)]
    public async Task<ActionResult<CreateApiKeyResponse>> CreateApiKey(
        [FromBody] CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await security.CreateApiKeyAsync(request, cancellationToken).ConfigureAwait(false);
            return Created($"/v1/security/api-keys/{created.ApiKey.Id}", created);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { message = exception.Message });
        }
    }

    [HttpDelete("/v1/security/api-keys/{apiKeyId:guid}")]
    [RequireMagicApplication(MagicApplication.Web)]
    [RequireMagicRole(BuiltInRole.Administrator)]
    public async Task<IActionResult> RevokeApiKey(Guid apiKeyId, CancellationToken cancellationToken) =>
        await security.RevokeApiKeyAsync(apiKeyId, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : NotFound();

    private bool IsLocalRequest() =>
        HttpContext.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    private bool HasBootstrapToken()
    {
        if (string.IsNullOrWhiteSpace(options.Value.BootstrapToken) ||
            !Request.Headers.TryGetValue(MagicAuthorizationHeaders.BootstrapToken, out var supplied))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(supplied.ToString());
        var right = Encoding.UTF8.GetBytes(options.Value.BootstrapToken);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
