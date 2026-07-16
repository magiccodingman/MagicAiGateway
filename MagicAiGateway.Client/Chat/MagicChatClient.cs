using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Transport;
using MagicAiGateway.Protocol;

namespace MagicAiGateway.Client.Chat;

public interface IMagicChatClient
{
    Task<MagicChatCompletionResponse> CompleteAsync(
        MagicChatCompletionRequest request,
        CancellationToken cancellationToken = default);

    Task<MagicChatStreamingSession> StartStreamingAsync(
        MagicChatCompletionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class MagicChatClient(
    RawGatewayClient raw,
    MagicAiGatewayClientOptions options) : IMagicChatClient
{
    public async Task<MagicChatCompletionResponse> CompleteAsync(
        MagicChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request, stream: false);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(normalized, options: MagicProtocolJson.Options)
        };
        using var response = await raw.SendWithTimeoutAsync(
            httpRequest,
            HttpCompletionOption.ResponseContentRead,
            ResolveTimeout(normalized),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MagicChatCompletionResponse>(
                   MagicProtocolJson.Options,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new JsonException("The gateway returned an empty chat completion response.");
    }

    public async Task<MagicChatStreamingSession> StartStreamingAsync(
        MagicChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request, stream: true);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(normalized, options: MagicProtocolJson.Options)
        };
        try
        {
            var response = await raw.SendStreamingWithTimeoutAsync(
                httpRequest,
                ResolveTimeout(normalized),
                cancellationToken).ConfigureAwait(false);
            response.Response.EnsureSuccessStatusCode();
            return new MagicChatStreamingSession(
                response,
                httpRequest,
                normalized.MagicAiGateway is not null);
        }
        catch
        {
            httpRequest.Dispose();
            throw;
        }
    }

    private MagicChatCompletionRequest Normalize(MagicChatCompletionRequest request, bool stream)
    {
        var envelope = request.MagicAiGateway;
        if (envelope is not null && string.IsNullOrWhiteSpace(envelope.Application))
        {
            envelope = envelope with { Application = options.ApplicationId };
        }

        return request with
        {
            Stream = stream,
            MagicAiGateway = envelope
        };
    }

    private TimeSpan ResolveTimeout(MagicChatCompletionRequest request)
    {
        if (request.MagicAiGateway is null) return options.StandardRequestTimeout;
        if (request.MagicAiGateway.RequestedRunTimeoutSeconds is { } seconds)
        {
            if (seconds <= 0) throw new InvalidOperationException("Requested Magic run timeout must be greater than zero.");
            return TimeSpan.FromSeconds(seconds);
        }
        return options.ManagedRequestTimeout;
    }
}

public sealed class MagicChatStreamingSession : IAsyncDisposable
{
    private readonly GatewayResponseStream _response;
    private readonly HttpRequestMessage _request;
    private readonly bool _requiresMagicCompletion;
    private readonly Channel<MagicChatStreamUpdate> _updates = Channel.CreateUnbounded<MagicChatStreamUpdate>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly TaskCompletionSource<MagicChatCompletionSummary> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Task _readerTask;

    internal MagicChatStreamingSession(
        GatewayResponseStream response,
        HttpRequestMessage request,
        bool requiresMagicCompletion)
    {
        _response = response;
        _request = request;
        _requiresMagicCompletion = requiresMagicCompletion;
        _readerTask = ReadAsync(_disposeCancellation.Token);
    }

    public IAsyncEnumerable<MagicChatStreamUpdate> Updates =>
        _updates.Reader.ReadAllAsync(_disposeCancellation.Token);

    public Task<MagicChatCompletionSummary> Completion => _completion.Task;

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        string? id = null;
        string? model = null;
        string? finishReason = null;
        MagicTokenUsage? usage = null;
        MagicRunMetadata? magicRun = null;
        string? eventName = null;
        var data = new StringBuilder();

        try
        {
            using var reader = new StreamReader(_response.Stream, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        var payload = data.ToString();
                        data.Clear();
                        if (payload == "[DONE]")
                        {
                            if (_requiresMagicCompletion && magicRun?.Status != MagicRunStatuses.Completed)
                            {
                                throw new MagicProtocolException(
                                    "The stream ended before the gateway reported Magic run completion.");
                            }

                            var summary = new MagicChatCompletionSummary
                            {
                                Id = id,
                                Model = model,
                                FinishReason = finishReason,
                                Content = content.ToString(),
                                Reasoning = reasoning.ToString(),
                                Usage = usage,
                                MagicRun = magicRun
                            };
                            await _updates.Writer.WriteAsync(new MagicRunCompletedUpdate(summary), cancellationToken)
                                .ConfigureAwait(false);
                            _completion.TrySetResult(summary);
                            _updates.Writer.TryComplete();
                            return;
                        }

                        if (eventName is null)
                        {
                            var chunk = JsonSerializer.Deserialize<MagicChatCompletionChunk>(payload, MagicProtocolJson.Options)
                                        ?? throw new JsonException("An SSE chat chunk was empty.");
                            id ??= chunk.Id;
                            model ??= chunk.Model;
                            usage = chunk.Usage ?? usage;
                            if (chunk.MagicAiGateway is not null) magicRun = chunk.MagicAiGateway;
                            foreach (var choice in chunk.Choices)
                            {
                                finishReason = choice.FinishReason ?? finishReason;
                                if (!string.IsNullOrEmpty(choice.Delta.Content))
                                {
                                    content.Append(choice.Delta.Content);
                                    await _updates.Writer.WriteAsync(
                                        new MagicContentDelta(choice.Delta.Content),
                                        cancellationToken).ConfigureAwait(false);
                                }
                                if (!string.IsNullOrEmpty(choice.Delta.ReasoningContent))
                                {
                                    reasoning.Append(choice.Delta.ReasoningContent);
                                    await _updates.Writer.WriteAsync(
                                        new MagicReasoningDelta(choice.Delta.ReasoningContent),
                                        cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                        else
                        {
                            await ProcessNamedEventAsync(
                                eventName,
                                payload,
                                value => magicRun = value,
                                value => reasoning.Append(value),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    eventName = null;
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    eventName = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line[5..].Trim());
                }
            }

            throw new MagicProtocolException("The SSE stream ended without a [DONE] marker.");
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
            _updates.Writer.TryComplete(exception);
        }
    }

    private async ValueTask ProcessNamedEventAsync(
        string eventName,
        string payload,
        Action<MagicRunMetadata> setRun,
        Action<string> appendReasoning,
        CancellationToken cancellationToken)
    {
        if (eventName == MagicStreamEventTypes.RunCompleted)
        {
            var metadata = JsonSerializer.Deserialize<MagicRunMetadata>(payload, MagicProtocolJson.Options)
                           ?? throw new JsonException("The Magic completion event was empty.");
            setRun(metadata);
            return;
        }

        if (eventName == MagicStreamEventTypes.RunFailed)
        {
            throw new MagicRunFailedException(payload);
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (eventName == MagicStreamEventTypes.ReasoningDelta &&
            root.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String)
        {
            var value = text.GetString() ?? string.Empty;
            appendReasoning(value);
            await _updates.Writer.WriteAsync(
                new MagicReasoningDelta(value),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var callId = root.TryGetProperty("tool_call_id", out var callIdElement) ? callIdElement.GetString() : null;
        var toolName = root.TryGetProperty("tool_name", out var toolNameElement) ? toolNameElement.GetString() : null;
        await _updates.Writer.WriteAsync(
            new MagicToolProgress(eventName, callId, toolName, root.Clone()),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCancellation.Cancel();
        try { await _readerTask.ConfigureAwait(false); } catch { }
        await _response.DisposeAsync().ConfigureAwait(false);
        _request.Dispose();
        _disposeCancellation.Dispose();
    }
}

public sealed class MagicProtocolException(string message) : InvalidOperationException(message);
public sealed class MagicRunFailedException(string payload)
    : InvalidOperationException($"The Magic run failed: {payload}");
