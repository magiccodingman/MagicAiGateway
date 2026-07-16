using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MagicAiGateway.Client.Protocol;
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
                MinimumClientProtocolVersion: 1,
                RootCertificateBase64: string.Empty,
                Features: ["openai-proxy", "streaming", "gateway-protocol"]));
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
        if (!stream)
        {
            await context.Response.WriteAsJsonAsync(new
            {
                id = "chatcmpl-client-test",
                @object = "chat.completion",
                created = 1,
                model = "Qwen36-27B",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        finish_reason = "stop",
                        message = new
                        {
                            role = "assistant",
                            content = "gateway client test response"
                        }
                    }
                }
            }, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        var events = new[]
        {
            "data: {\"id\":\"chatcmpl-client-test\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"gateway \"},\"finish_reason\":null}]}\n\n",
            "data: {\"id\":\"chatcmpl-client-test\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"client test response\"},\"finish_reason\":null}]}\n\n",
            "data: {\"id\":\"chatcmpl-client-test\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
            "data: [DONE]\n\n"
        };

        foreach (var eventData in events)
        {
            await context.Response.WriteAsync(eventData, context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

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
