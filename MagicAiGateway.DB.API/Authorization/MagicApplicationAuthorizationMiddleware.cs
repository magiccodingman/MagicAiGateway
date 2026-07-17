using System.Net.Http.Headers;
using MagicAiGateway.DB.Contracts;

namespace MagicAiGateway.DB.API.Authorization;

public sealed class MagicApplicationAuthorizationMiddleware(RequestDelegate next)
{
    public const string DecisionItemKey = "MagicAiGateway.ApplicationAuthorizationDecision";

    public async Task InvokeAsync(HttpContext context, IApplicationAuthorizationEvaluator evaluator)
    {
        var endpoint = context.GetEndpoint();
        var applicationRequirement = endpoint?.Metadata.GetMetadata<RequireMagicApplicationAttribute>();
        var roleRequirement = endpoint?.Metadata.GetMetadata<RequireMagicRoleAttribute>();
        if (applicationRequirement is null && roleRequirement is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var claimedApplication = ParseApplication(context.Request.Headers[MagicAuthorizationHeaders.Application]);
        var candidate = ParseBearer(context.Request.Headers.Authorization);
        var decision = await evaluator.EvaluateAsync(
            new ApplicationAuthorizationRequest(
                candidate,
                claimedApplication,
                applicationRequirement?.Applications ?? Enum.GetValues<MagicApplication>().Where(static value => value != MagicApplication.Unknown).ToArray(),
                roleRequirement?.Roles),
            context.RequestAborted).ConfigureAwait(false);
        context.Items[DecisionItemKey] = decision;

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
                code = decision.Authenticated ? "application_forbidden" : "application_unauthorized",
                message = decision.Reason
            },
            decision.SecurityEnabled,
            decision.SecurityRevision
        }, context.RequestAborted).ConfigureAwait(false);
    }

    private static MagicApplication ParseApplication(string? value) =>
        Enum.TryParse<MagicApplication>(value, ignoreCase: true, out var application)
            ? application
            : MagicApplication.Unknown;

    private static string? ParseBearer(string? value)
    {
        if (!AuthenticationHeaderValue.TryParse(value, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return header.Parameter;
    }
}
