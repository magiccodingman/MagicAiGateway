using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MagicAiGateway.Protocol;
using SharedMagic.Execution;

namespace MagicAiApi.Protocol;

public sealed class HttpGatewayRunOutput(
    HttpContext httpContext,
    bool streaming,
    string responseMode) : IGatewayRunOutput
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _completed;

    public bool HasStarted => httpContext.Response.HasStarted;
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public async ValueTask PublishAsync(
        MagicChatStreamUpdate update,
        CancellationToken cancellationToken)
    {
        if (!streaming || IsCompleted) return;

        switch (update)
        {
            case MagicContentDelta content:
                await WriteDataAsync(new MagicChatCompletionChunk
                {
                    Choices =
                    [
                        new MagicChatChunkChoice
                        {
                            Index = 0,
                            Delta = new MagicChatDelta { Content = content.Text }
                        }
                    ]
                }, cancellationToken).ConfigureAwait(false);
                break;

            case MagicOpenAiChunkUpdate openAi:
                await WriteDataAsync(openAi.Chunk, cancellationToken).ConfigureAwait(false);
                break;

            case MagicReasoningDelta reasoning when responseMode == MagicResponseModes.Enriched:
                await WriteNamedEventAsync(
                    MagicStreamEventTypes.ReasoningDelta,
                    new { text = reasoning.Text },
                    cancellationToken).ConfigureAwait(false);
                break;

            case MagicToolProgress tool when responseMode == MagicResponseModes.Enriched:
                await WriteNamedEventAsync(
                    tool.EventType,
                    new { tool_call_id = tool.ToolCallId, tool_name = tool.ToolName, payload = tool.Payload },
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public async ValueTask CompleteAsync(
        MagicChatCompletionResponse response,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;

        if (!streaming)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            await httpContext.Response.WriteAsJsonAsync(
                response,
                MagicProtocolJson.Options,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var choice = response.Choices.FirstOrDefault();
        var content = choice?.Message.Content is { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : null;
        if (!string.IsNullOrEmpty(content))
        {
            await WriteDataAsync(new MagicChatCompletionChunk
            {
                Id = response.Id,
                Model = response.Model,
                Choices =
                [
                    new MagicChatChunkChoice
                    {
                        Index = choice?.Index ?? 0,
                        Delta = new MagicChatDelta { Content = content }
                    }
                ]
            }, cancellationToken).ConfigureAwait(false);
        }

        await WriteDataAsync(new MagicChatCompletionChunk
        {
            Id = response.Id,
            Model = response.Model,
            Choices =
            [
                new MagicChatChunkChoice
                {
                    Index = choice?.Index ?? 0,
                    Delta = new MagicChatDelta(),
                    FinishReason = choice?.FinishReason ?? response.MagicAiGateway?.FinishReason ?? "stop"
                }
            ],
            Usage = response.Usage,
            MagicAiGateway = response.MagicAiGateway
        }, cancellationToken).ConfigureAwait(false);

        if (responseMode == MagicResponseModes.Enriched && response.MagicAiGateway is not null)
        {
            await WriteNamedEventAsync(
                MagicStreamEventTypes.RunCompleted,
                response.MagicAiGateway,
                cancellationToken).ConfigureAwait(false);
        }

        await WriteDoneAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FailAsync(
        MagicRunError error,
        MagicRunMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;

        if (!streaming)
        {
            httpContext.Response.StatusCode = error.Code == "service_not_ready"
                ? StatusCodes.Status501NotImplemented
                : StatusCodes.Status500InternalServerError;
            var body = new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = error.Message,
                    ["type"] = "magic_gateway_error",
                    ["code"] = error.Code,
                    ["retryable"] = error.Retryable
                },
                [MagicAiGatewayProtocol.PropertyName] = JsonSerializer.SerializeToNode(metadata, MagicProtocolJson.Options)
            };
            await httpContext.Response.WriteAsJsonAsync(body, cancellationToken).ConfigureAwait(false);
            return;
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        await WriteNamedEventAsync(
            MagicStreamEventTypes.RunFailed,
            new { error, magic_ai_gateway = metadata },
            cancellationToken).ConfigureAwait(false);
        await WriteDataAsync(new MagicChatCompletionChunk
        {
            Choices =
            [
                new MagicChatChunkChoice
                {
                    Index = 0,
                    Delta = new MagicChatDelta(),
                    FinishReason = "error"
                }
            ],
            Usage = metadata.Usage,
            MagicAiGateway = metadata
        }, cancellationToken).ConfigureAwait(false);
        await WriteDoneAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteDataAsync<T>(T payload, CancellationToken cancellationToken)
    {
        await EnsureStreamingStartedAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(payload, MagicProtocolJson.Options);
        await WriteRawAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteNamedEventAsync<T>(
        string eventName,
        T payload,
        CancellationToken cancellationToken)
    {
        await EnsureStreamingStartedAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(payload, MagicProtocolJson.Options);
        await WriteRawAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteDoneAsync(CancellationToken cancellationToken) =>
        await WriteRawAsync("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);

    private ValueTask EnsureStreamingStartedAsync(CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteRawAsync(string value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await httpContext.Response.WriteAsync(value, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
