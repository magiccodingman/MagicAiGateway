using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SharedMagic.Configuration;
using SharedMagic.Contracts;
using SharedMagic.Security;

namespace MagicAiApi;

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

    [MagicFabricAuthorize]
    [HttpGet("/internal/v1/nodes")]
    public IActionResult Nodes() => Ok(registry.GetNodes());
}

[ApiController]
public sealed class GatewayTokenizerController(GatewayNodeRegistry registry, GatewayNodeClient client) : ControllerBase
{
    [MagicFabricAuthorize]
    [HttpGet("/internal/v1/tokenizers/{model}")]
    public async Task<IActionResult> Get(string model, CancellationToken cancellationToken)
    {
        var target = registry.FindAnyForModel(model);
        if (target is null) return NotFound(OpenAiErrors.NotFound($"No node currently provides model '{model}'."));
        using var response = await client.GetAsync(target, $"/internal/v1/tokenizers/{Uri.EscapeDataString(model)}", cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
            Content = content
        };
    }
}

[ApiController]
public sealed class GatewayPairingController(
    GatewayCertificateAuthority authority,
    GatewayPairingRegistry registry,
    PairingChallengeStore challenges,
    IOptions<GatewayOptions> gatewayOptions,
    IOptions<FabricSecurityOptions> securityOptions) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("/fabric/v1/pair/challenge")]
    public IActionResult Challenge([FromBody] PairingChallengeRequest request)
    {
        if (!string.Equals(request.ExpectedGatewayName, gatewayOptions.Value.Name, StringComparison.Ordinal))
        {
            return Conflict(new { message = "Gateway name mismatch." });
        }
        return Ok(challenges.Create(request.NodeId));
    }

    [AllowAnonymous]
    [HttpPost("/fabric/v1/pair")]
    public IActionResult Pair([FromBody] PairingRequest request)
    {
        if (!string.Equals(request.ExpectedGatewayName, gatewayOptions.Value.Name, StringComparison.Ordinal))
        {
            return Conflict(new PairingResponse("rejected", authority.Identity.InstanceId, authority.Identity.ClusterId, gatewayOptions.Value.Name, Message: "Gateway name mismatch."));
        }

        if (!Uri.TryCreate(request.AdvertisedBaseUri, UriKind.Absolute, out var advertised) ||
            (advertised.Scheme != Uri.UriSchemeHttps && !advertised.IsLoopback))
        {
            return BadRequest(new PairingResponse("rejected", authority.Identity.InstanceId, authority.Identity.ClusterId, gatewayOptions.Value.Name, Message: "The advertised node URI must use HTTPS unless it is loopback."));
        }

        var enrollmentValidated = securityOptions.Value.PairingMode == PairingMode.EnrollmentToken &&
                                  challenges.Validate(
                                      request.NodeId,
                                      request.ChallengeId,
                                      request.EnrollmentProofBase64,
                                      securityOptions.Value.EnrollmentToken);
        var approved = securityOptions.Value.PairingMode == PairingMode.EnrollmentToken
            ? enrollmentValidated
            : registry.IsApproved(request.NodeId) || ShouldAutoApprove(request);
        if (!approved)
        {
            registry.AddOrUpdatePending(request);
            return Accepted(new PairingResponse("pending", authority.Identity.InstanceId, authority.Identity.ClusterId, gatewayOptions.Value.Name, Message: "Pairing awaits administrator approval or a valid enrollment proof."));
        }

        byte[] csr;
        try { csr = Convert.FromBase64String(request.CsrBase64); }
        catch (FormatException) { return BadRequest(new { message = "The CSR is not valid base64." }); }

        byte[] issued;
        try { issued = authority.IssueCertificate(request.NodeId, request.NodeName, csr); }
        catch (CryptographicException exception) { return BadRequest(new { message = exception.Message }); }

        registry.AddOrUpdatePending(request);
        registry.Approve(request.NodeId);
        var root = authority.RootCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
        string? gatewayProof = null;
        if (enrollmentValidated && request.ChallengeId is { } challengeId && !string.IsNullOrWhiteSpace(securityOptions.Value.EnrollmentToken))
        {
            gatewayProof = Convert.ToBase64String(PairingProof.ComputeGatewayResponse(
                securityOptions.Value.EnrollmentToken,
                challengeId,
                request.NodeId,
                authority.Identity.InstanceId,
                authority.Identity.ClusterId,
                gatewayOptions.Value.Name,
                issued,
                root));
        }

        return Ok(new PairingResponse(
            "paired",
            authority.Identity.InstanceId,
            authority.Identity.ClusterId,
            gatewayOptions.Value.Name,
            Convert.ToBase64String(issued),
            Convert.ToBase64String(root),
            gatewayProof));
    }

    [AllowAnonymous]
    [HttpGet("/fabric/v1/pairing")]
    public IActionResult Pending()
    {
        if (!IsAdministrator()) return Unauthorized();
        return Ok(registry.GetAll());
    }

    [AllowAnonymous]
    [HttpPost("/fabric/v1/pairing/{nodeId:guid}/approve")]
    public IActionResult Approve(Guid nodeId)
    {
        if (!IsAdministrator()) return Unauthorized();
        return registry.Approve(nodeId) ? Ok(new { nodeId, approved = true }) : NotFound();
    }

    private bool ShouldAutoApprove(PairingRequest request)
    {
        if (HttpContext.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)) return true;
        return securityOptions.Value.PairingMode switch
        {
            PairingMode.AutomaticTrustOnFirstUse => true,
            PairingMode.EnrollmentToken => false,
            _ => false
        };
    }

    private bool IsAdministrator()
    {
        if (HttpContext.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)) return true;
        if (string.IsNullOrWhiteSpace(securityOptions.Value.AdminToken)) return false;
        return Request.Headers.TryGetValue("X-Magic-Admin-Token", out var value) && SecureEquals(value.ToString(), securityOptions.Value.AdminToken);
    }

    private static bool SecureEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(left);
        var b = System.Text.Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
