using MagicAiGateway.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using SharedMagic.Contracts;
using SharedMagic.Proxy;
using SharedMagic.Routing;
using SharedMagic.Security;
using Yarp.ReverseProxy.Forwarder;

namespace MagicAiApi;

public static class GatewayProxyEndpoint
{
    private static readonly string[] Methods =
    [
        HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch,
        HttpMethods.Delete, HttpMethods.Options, HttpMethods.Head
    ];

    public static void Map(WebApplication app)
    {
        app.MapMethods("/v1/{**path}", Methods, ForwardAsync)
            .RequireAuthorization(GatewayPolicies.ClientAccess);
        app.MapMethods("/tokenize", Methods, ForwardAsync)
            .RequireAuthorization(GatewayPolicies.ClientAccess);
        app.MapMethods("/detokenize", Methods, ForwardAsync)
            .RequireAuthorization(GatewayPolicies.ClientAccess);
    }

    private static async Task ForwardAsync(
        HttpContext context,
        IHttpForwarder forwarder,
        IRequestScheduler<GatewayNodeTarget> scheduler,
        GatewayProxyInvoker invoker,
        IMagicToolRegistry toolRegistry,
        MagicProtocolHost protocolHost,
        IGatewayOperationResolver operationResolver,
        IAuthorizationService authorizationService,
        ILoggerFactory loggerFactory)
    {
        context.Request.EnableBuffering();
        var inspection = await OpenAiRequestInspector.InspectAsync(
            context.Request.Body,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);

        if (inspection.HasInvalidMagicGateway)
        {
            await WriteError(context, StatusCodes.Status400BadRequest,
                new OpenAiErrorBody(new(
                    $"{MagicAiGatewayProtocol.PropertyName} must be either an object or null.",
                    "invalid_request_error",
                    MagicAiGatewayProtocol.PropertyName,
                    "invalid_gateway_envelope"))).ConfigureAwait(false);
            return;
        }

        var operation = operationResolver.Resolve(
            context.Request,
            inspection.HasMagicGatewayObject);
        var model = inspection.Model
                    ?? context.Request.Query["model"].FirstOrDefault()
                    ?? context.Request.Headers["X-Magic-Model"].FirstOrDefault();
        var authorizationResource = new GatewayAuthorizationResource(
            operation,
            model,
            context.Request.Path);
        var authorizationResult = await authorizationService.AuthorizeAsync(
            context.User,
            authorizationResource,
            GatewayPolicies.ForOperation(operation)).ConfigureAwait(false);

        if (!authorizationResult.Succeeded)
        {
            await RejectUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        if (inspection.HasMagicGatewayObject && inspection.MagicGatewayEnvelope is { } envelope)
        {
            await protocolHost.ExecuteAsync(context, envelope, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var isTokenizePath = string.Equals(
                                 context.Request.Path.Value,
                                 "/tokenize",
                                 StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(
                                 context.Request.Path.Value,
                                 "/v1/tokenize",
                                 StringComparison.OrdinalIgnoreCase);
        if (isTokenizePath && inspection.LooksLikeOpenAiEnvelope)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented,
                OpenAiErrors.NotImplemented(
                    "Gateway-owned OpenAI tokenization is recognized but not implemented yet. Native provider /tokenize requests continue to pass through.")).ConfigureAwait(false);
            return;
        }

        if (inspection.HasNullMagicGateway)
        {
            var rewritten = await OpenAiRequestInspector.RemoveNullMagicGatewayAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (rewritten is not null)
            {
                context.Request.Body = new MemoryStream(rewritten, writable: false);
                context.Request.ContentLength = rewritten.Length;
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            await WriteError(context, StatusCodes.Status400BadRequest,
                new OpenAiErrorBody(new(
                    "A model is required in the request body, model query parameter, or X-Magic-Model header.",
                    "invalid_request_error",
                    "model",
                    "model_required"))).ConfigureAwait(false);
            return;
        }

        DestinationLease<GatewayNodeTarget> lease;
        try
        {
            lease = await scheduler.AcquireAsync(model, context.RequestAborted).ConfigureAwait(false);
        }
        catch (RouteUnavailableException exception)
        {
            await WriteError(
                context,
                StatusCodes.Status404NotFound,
                OpenAiErrors.NotFound(exception.Message)).ConfigureAwait(false);
            return;
        }
        catch (RouteQueueFullException exception)
        {
            await WriteError(
                context,
                StatusCodes.Status503ServiceUnavailable,
                OpenAiErrors.Overloaded(exception.Message)).ConfigureAwait(false);
            return;
        }

        await using (lease)
        {
            var originalBody = context.Response.Body;
            var logger = loggerFactory.CreateLogger("MagicAiApi.ToolCalls");
            var observing = new ToolCallObservingStream(originalBody, toolRegistry, observation =>
            {
                if (!observation.ContainsToolCalls) return;
                logger.LogInformation(
                    "Observed {Count} model tool call(s); {GatewayOwned} matched the gateway registry. Responses remain transparent in this implementation.",
                    observation.ToolCalls.Count,
                    observation.ToolCalls.Count(x => toolRegistry.Contains(x.Name)));
            });
            context.Response.Body = observing;
            try
            {
                var error = await forwarder.SendAsync(
                    context,
                    lease.Target.BaseUri,
                    invoker.Get(lease.Target),
                    new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(30) },
                    HttpTransformer.Default).ConfigureAwait(false);
                if (error != ForwarderError.None)
                {
                    var feature = context.GetForwarderErrorFeature();
                    logger.LogWarning(
                        feature?.Exception,
                        "Forwarding to node {NodeId} failed with {Error}.",
                        lease.Target.NodeId,
                        error);
                }
            }
            finally
            {
                await observing.CompleteAsync(context.RequestAborted).ConfigureAwait(false);
                context.Response.Body = originalBody;
            }
        }
    }

    private static Task RejectUnauthorizedAsync(HttpContext context) =>
        GatewayClientIdentity.IsClientAuthenticated(context.User)
            ? context.ForbidAsync(GatewayClientAuthenticationDefaults.Scheme)
            : context.ChallengeAsync(GatewayClientAuthenticationDefaults.Scheme);

    private static async Task WriteError(HttpContext context, int status, OpenAiErrorBody error)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(error, context.RequestAborted).ConfigureAwait(false);
    }
}
