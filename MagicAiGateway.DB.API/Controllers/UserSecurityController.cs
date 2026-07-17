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
public sealed class UserSecurityController(
    UserSecurityService users,
    IOptions<AdminRecoveryOptions> recoveryOptions,
    AdminRecoveryGate recoveryGate) : ControllerBase
{
    [HttpGet("/v1/security/users/bootstrap-status")]
    public async Task<ActionResult<AdministratorBootstrapStatusResponse>> BootstrapStatus(CancellationToken cancellationToken) =>
        Ok(await users.GetBootstrapStatusAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("/v1/security/users/login")]
    public async Task<ActionResult<UserLoginResponse>> Login(
        [FromBody] UserLoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await users.LoginAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Authenticated ? Ok(result) : Unauthorized(result);
    }

    [HttpPost("/v1/security/users/change-password")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await users.ChangePasswordAsync(request, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (UnauthorizedAccessException exception)
        {
            return Unauthorized(new { message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("/recovery/v1/admin/password")]
    public async Task<IActionResult> RecoverAdministrator(
        [FromBody] AdminPasswordRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!recoveryOptions.Value.Enabled ||
            HttpContext.Connection.RemoteIpAddress is not { } address ||
            !IPAddress.IsLoopback(address) ||
            !IsRecoveryListener() ||
            !HasRecoveryToken())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.NewTemporaryPassword) ||
            request.NewTemporaryPassword.Length < 12)
        {
            return BadRequest(new { message = "Passwords must contain at least 12 characters." });
        }

        if (!recoveryGate.TryUse()) return NotFound();

        try
        {
            await users.ResetAdministratorPasswordAsync(request.NewTemporaryPassword, cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                reset = true,
                mustChangePassword = true,
                message = "The administrator password was reset once for this process. Disable recovery mode and change the password immediately."
            });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private bool IsRecoveryListener()
    {
        if (!Uri.TryCreate(recoveryOptions.Value.ListenUrl, UriKind.Absolute, out var recoveryUri))
        {
            return false;
        }

        return HttpContext.Connection.LocalPort == recoveryUri.Port;
    }

    private bool HasRecoveryToken()
    {
        if (string.IsNullOrWhiteSpace(recoveryOptions.Value.OneTimeToken) ||
            !Request.Headers.TryGetValue(MagicAuthorizationHeaders.RecoveryToken, out var supplied))
        {
            return false;
        }
        var left = Encoding.UTF8.GetBytes(supplied.ToString());
        var right = Encoding.UTF8.GetBytes(recoveryOptions.Value.OneTimeToken);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
