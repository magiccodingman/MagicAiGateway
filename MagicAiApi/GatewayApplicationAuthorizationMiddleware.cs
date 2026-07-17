using System.Net.Http.Headers;
using MagicAiGateway.DB.Contracts;

namespace MagicAiApi;

public sealed class GatewayApplicationAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IApplicationAuthorizationEvaluator evaluator)
    {
        var endpoint = context.GetEndpoint();
        var applicationRequirement = endpoint?.Metadata.GetMetadata<RequireMagicApplicationAttribute>();
        var roleRequirement = endpoint?.Metadata.GetMetadata<RequireMagicRoleAttribute>();
        if (applicationRequirement is null && roleRequirement is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        ApplicationAuthorizationDecision decision;
        try
        {
            decision = await evaluator.EvaluateAsync(
                new ApplicationAuthorizationRequest(
                    ParseBearer(context.Request.Headers.Authorization),
                    ParseApplication(context.Request.Headers[MagicAuthorizationHeaders.Application]),
                    applicationRequirement?.Applications ??
                    Enum.GetValues<MagicApplication>()
                        .Where(static value => value != MagicApplication.Unknown)
                        .ToArray(),
                    roleRequirement?.Roles),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = "database_security_unavailable",
                    message = "The gateway could not reach the database security authority."
                }
            }, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (decision.Authorized)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = decision.Authenticated
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = decision.Authenticated
                    ? "application_forbidden"
                    : "application_unauthorized",
                message = decision.Reason
            },
            decision.SecurityEnabled,
            decision.SecurityRevision
        }, context.RequestAborted).ConfigureAwait(false);
    }

    private static MagicApplication ParseApplication(string? value) =>
        Enum.TryParse<MagicApplication>(value, true, out var application)
            ? application
            : MagicApplication.Unknown;

    private static string? ParseBearer(string? value) =>
        AuthenticationHeaderValue.TryParse(value, out var header) &&
        string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            ? header.Parameter
            : null;
}
