using System.Text.Json;
using SharedMagic.Contracts;

namespace MagicAiApi;

public interface IMagicProtocolHandler
{
    int Order { get; }
    bool CanHandle(JsonElement envelope);
    Task HandleAsync(HttpContext context, JsonElement envelope, CancellationToken cancellationToken);
}

public sealed class MagicProtocolDispatcher(IEnumerable<IMagicProtocolHandler> handlers)
{
    private readonly IMagicProtocolHandler[] _handlers = handlers.OrderBy(static x => x.Order).ToArray();

    public async Task DispatchAsync(HttpContext context, JsonElement envelope, CancellationToken cancellationToken)
    {
        var handler = _handlers.FirstOrDefault(x => x.CanHandle(envelope));
        if (handler is not null)
        {
            await handler.HandleAsync(context, envelope, cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        await context.Response.WriteAsJsonAsync(
            OpenAiErrors.NotImplemented("The magic_ai_gateway protocol envelope was recognized, but no installed protocol handler can execute it yet."),
            cancellationToken).ConfigureAwait(false);
    }
}
