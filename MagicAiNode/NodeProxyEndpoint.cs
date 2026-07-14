using SharedMagic.Contracts;
using SharedMagic.Proxy;
using SharedMagic.Routing;
using SharedMagic.Security;
using Yarp.ReverseProxy.Forwarder;

namespace MagicAiNode;

public static class NodeProxyEndpoint
{
    private static readonly string[] Methods =
    [
        HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch,
        HttpMethods.Delete, HttpMethods.Options, HttpMethods.Head
    ];

    public static void Map(WebApplication app)
    {
        app.MapMethods("/v1/{**path}", Methods, ForwardAsync).RequireAuthorization(FabricAuthenticationDefaults.Policy);
        app.MapMethods("/tokenize", Methods, ForwardAsync).RequireAuthorization(FabricAuthenticationDefaults.Policy);
        app.MapMethods("/detokenize", Methods, ForwardAsync).RequireAuthorization(FabricAuthenticationDefaults.Policy);
        app.MapMethods("/props", Methods, ForwardAsync).RequireAuthorization(FabricAuthenticationDefaults.Policy);
    }

    private static async Task ForwardAsync(
        HttpContext context,
        IHttpForwarder forwarder,
        IRequestScheduler<BackendRouteTarget> scheduler,
        BackendProxyInvokerPool invokers,
        ILoggerFactory loggerFactory)
    {
        context.Request.EnableBuffering();
        var inspection = await OpenAiRequestInspector.InspectAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        var model = inspection.Model
                    ?? context.Request.Query["model"].FirstOrDefault()
                    ?? context.Request.Headers["X-Magic-Model"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(model))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new OpenAiErrorBody(new("A model is required in the request body, model query parameter, or X-Magic-Model header.", "invalid_request_error", "model", "model_required")),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        DestinationLease<BackendRouteTarget> lease;
        try
        {
            lease = await scheduler.AcquireAsync(model, context.RequestAborted).ConfigureAwait(false);
        }
        catch (RouteUnavailableException exception)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(OpenAiErrors.NotFound(exception.Message), context.RequestAborted).ConfigureAwait(false);
            return;
        }
        catch (RouteQueueFullException exception)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(OpenAiErrors.Overloaded(exception.Message), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await using (lease)
        {
            var logger = loggerFactory.CreateLogger("MagicAiNode.Proxy");
            var error = await forwarder.SendAsync(
                context,
                lease.Target.BaseUri,
                invokers.Get(lease.Target.Options),
                new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(30) },
                new BackendTransformer(lease.Target.Options)).ConfigureAwait(false);
            if (error != ForwarderError.None)
            {
                var feature = context.GetForwarderErrorFeature();
                logger.LogWarning(feature?.Exception, "Forwarding to backend {BackendId} failed with {Error}.", lease.Target.Id, error);
            }
        }
    }
}
