using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MagicAiGateway.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MagicAiGateway.Client.Tests.Infrastructure;

internal sealed record CapturedGatewayRequest(
    string Method,
    string Path,
    string? Body,
    string? Authorization);

internal sealed class LoopbackGatewayServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentQueue<CapturedGatewayRequest> _requests = new();

    private LoopbackGatewayServer(WebApplication application)
    {
        _application = application;
        GatewayId = Guid.NewGuid();
        ClusterId = Guid.NewGuid();
    }

    public Guid GatewayId { get; }
    public Guid ClusterId { get; }
    public Uri Endpoint { get; private set; } = null!;
    public IReadOnlyCollection<CapturedGatewayRequest> Requests => _requests.ToArray();

    public static async Task<LoopbackGatewayServer> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        var server = new LoopbackGatewayServer(application);
        server.MapRoutes();

        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = addresses?.SingleOrDefault()
                      ?? throw new InvalidOperationException("The loopback test server did not publish an address.");
        server.Endpoint = new Uri(address.EndsWith('/') ? address : address + "/");
        return server;
    }

    private void MapRoutes()
    {
        _application.MapGet(MagicAiGatewayProtocol.GatewayInfoPath, (HttpContext context) =>
        {
            Capture(context, null);
            return Results.Json(new GatewayInfo(
                "MagicAiGateway",
                GatewayId,
                ClusterId,
                MagicAiGatewayProtocol.CurrentVersion,
                MagicAiGatewayProtocol.MinimumSupportedVersion,
                RootCertificateBase64: string.Empty,
                Features: ["openai-proxy", "streaming", "gateway-protocol", "service-catalog"]));
        });

        _application.MapGet(MagicAiGatewayProtocol.ServicesPath, (HttpContext context) =>
        {
            Capture(context, null);
            return Results.Json(new MagicServiceCatalog
            {
                Data = [CreateManagedToolsDescriptor()]
            }, MagicProtocolJson.Options);
        });

        _application.MapGet(MagicAiGatewayProtocol.ServicesPath + "/{name}", (HttpContext context, string name) =>
        {
            Capture(context, null);
            return name == MagicServiceNames.ManagedTools
                ? Results.Json(CreateManagedToolsDescriptor(), MagicProtocolJson.Options)
                : Results.NotFound();
        });

        _application.MapGet("/v1/models", (HttpContext context) =>
        {
            Capture(context, null);
            return Results.Json(new
            {
                @object = "list",
                data = new[]
                {
                    new
                    {
                        id = "Qwen36-27B",
                        @object = "model",
                        created = 0,
                        owned_by = "loopback-test"
                    }
                }
            });
        });

        _application.MapPost("/v1/chat/completions", HandleChatCompletionAsync);
    }

    private async Task HandleChatCompletionAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(
            context.Request.Body,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
        var body = document.RootElement.GetRawText();
        Capture(context, body);

        var stream = document.RootElement.TryGetProperty("stream", out var streamElement) &&
                     streamElement.ValueKind == JsonValueKind.True;
        var magicEnabled = document.RootElement.TryGetProperty(
            MagicAiGatewayProtocol.PropertyName,
            out var magicElement) &&
            magicElement.ValueKind == JsonValueKind.Object;
        var enriched = magicEnabled &&
                       magicElement.TryGetProperty("response_mode", out var responseMode) &&
                       responseMode.GetString() == MagicResponseModes.Enriched;

        if (!stream)
        {
            var response = CreateCompletion(magicEnabled);
            await context.Response.WriteAsJsonAsync(
                response,
                MagicProtocolJson.Options,
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        var metadata = CreateRunMetadata();
        var events = new List<string>
        {
            "data: {\"id\":\"chatcmpl-client-test\",\"object\":\"chat.completion.chunk\",\"model\":\"Qwen36-27B\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"checking tools... \"},\"finish_reason\":null}]}\n\n",
            "data: {\"id\":\"chatcmpl-client-test\",\"object\":\"chat.completion.chunk\",\"model\":\"Qwen36-27B\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"gateway \"},\"finish_reason\":null}]}\n\n",
            "data: {\"id\":\"chatcmpl-client-test\",\"object\":\"chat.completion.chunk\",\"model\":\"Qwen36-27B\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"client test response\"},\"finish_reason\":null}]}\n\n"
        };

        var finalChunk = new MagicChatCompletionChunk
        {
            Id = "chatcmpl-client-test",
            Model = "Qwen36-27B",
            Choices =
            [
                new MagicChatChunkChoice
                {
                    Index = 0,
                    Delta = new MagicChatDelta(),
                    FinishReason = "stop"
                }
            ],
            Usage = new MagicTokenUsage
            {
                PromptTokens = magicEnabled ? 4200 : 100,
                CompletionTokens = magicEnabled ? 950 : 20,
                TotalTokens = magicEnabled ? 5150 : 120,
                CompletionTokenDetails = new MagicCompletionTokenDetails
                {
                    ReasoningTokens = magicEnabled ? 700 : 5
                }
            },
            MagicAiGateway = magicEnabled ? metadata : null
        };
        events.Add($"data: {JsonSerializer.Serialize(finalChunk, MagicProtocolJson.Options)}\n\n");
        if (enriched)
        {
            events.Add($"event: {MagicStreamEventTypes.RunCompleted}\ndata: {JsonSerializer.Serialize(metadata, MagicProtocolJson.Options)}\n\n");
        }
        events.Add("data: [DONE]\n\n");

        foreach (var eventData in events)
        {
            await context.Response.WriteAsync(eventData, context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static MagicChatCompletionResponse CreateCompletion(bool magicEnabled) => new()
    {
        Id = "chatcmpl-client-test",
        Created = 1,
        Model = "Qwen36-27B",
        Choices =
        [
            new MagicChatChoice
            {
                Index = 0,
                FinishReason = "stop",
                Message = MagicChatMessage.Assistant("gateway client test response")
            }
        ],
        Usage = new MagicTokenUsage
        {
            PromptTokens = magicEnabled ? 4200 : 100,
            CompletionTokens = magicEnabled ? 950 : 20,
            TotalTokens = magicEnabled ? 5150 : 120,
            CompletionTokenDetails = new MagicCompletionTokenDetails
            {
                ReasoningTokens = magicEnabled ? 700 : 5
            }
        },
        MagicAiGateway = magicEnabled ? CreateRunMetadata() : null
    };

    private static MagicRunMetadata CreateRunMetadata() => new()
    {
        RunId = "run_loopback",
        Service = MagicServiceNames.ManagedTools,
        Status = MagicRunStatuses.Completed,
        FinishReason = "stop",
        ModelCalls = 3,
        ToolCalls = 2,
        UsageAccuracy = MagicUsageAccuracy.ProviderReported,
        Usage = new MagicTokenUsage
        {
            PromptTokens = 4200,
            CompletionTokens = 950,
            TotalTokens = 5150,
            CompletionTokenDetails = new MagicCompletionTokenDetails { ReasoningTokens = 700 }
        },
        ModelCallUsage =
        [
            new MagicModelCallUsage
            {
                Sequence = 1,
                Model = "Qwen36-27B",
                Usage = new MagicTokenUsage { PromptTokens = 1000, CompletionTokens = 150, TotalTokens = 1150 }
            },
            new MagicModelCallUsage
            {
                Sequence = 2,
                Model = "Qwen36-27B",
                Usage = new MagicTokenUsage { PromptTokens = 1450, CompletionTokens = 200, TotalTokens = 1650 }
            },
            new MagicModelCallUsage
            {
                Sequence = 3,
                Model = "Qwen36-27B",
                Usage = new MagicTokenUsage { PromptTokens = 1750, CompletionTokens = 600, TotalTokens = 2350 }
            }
        ]
    };

    private static MagicServiceDescriptor CreateManagedToolsDescriptor() => new()
    {
        Name = MagicServiceNames.ManagedTools,
        Version = 1,
        Description = "Loopback managed tool service.",
        Availability = "scaffolded",
        SupportedEndpoints = ["/v1/chat/completions"],
        DefaultRunTimeoutSeconds = 1800,
        MaximumRunTimeoutSeconds = 3600,
        OptionsSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["mcp_profile"] = new JsonObject { ["type"] = "string" }
            }
        },
        StreamingEvents = [MagicStreamEventTypes.RunCompleted]
    };

    private void Capture(HttpContext context, string? body) =>
        _requests.Enqueue(new CapturedGatewayRequest(
            context.Request.Method,
            context.Request.Path,
            body,
            context.Request.Headers.Authorization.FirstOrDefault()));

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
