using System.Text.Json;
using MagicAiGateway.Client.Chat;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Tests.Infrastructure;
using MagicAiGateway.Protocol;

namespace MagicAiGateway.Client.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class MagicProtocolClientTests
{
    [Fact]
    public async Task DiscoversInstalledServiceDefinitions()
    {
        await using var server = await LoopbackGatewayServer.StartAsync();
        using var directory = new TemporaryDirectory("service-catalog");
        await using var client = CreateClient(server.Endpoint, directory.Path);

        var catalog = await client.Protocol.GetServicesAsync();
        var managedTools = Assert.Single(catalog.Data);
        var descriptor = await client.Protocol.GetServiceAsync(MagicServiceNames.ManagedTools);

        Assert.Equal(MagicServiceNames.ManagedTools, managedTools.Name);
        Assert.Equal("scaffolded", descriptor.Availability);
        Assert.Equal(1800, descriptor.DefaultRunTimeoutSeconds);
        Assert.Equal("object", descriptor.OptionsSchema["type"]?.GetValue<string>());
    }

    [Fact]
    public async Task TypedChatRequestPlacesMagicEnvelopeAtRequestRoot()
    {
        await using var server = await LoopbackGatewayServer.StartAsync();
        using var directory = new TemporaryDirectory("typed-magic-request");
        await using var client = CreateClient(server.Endpoint, directory.Path);
        var envelope = client.Protocol.CreateEnvelope(
            MagicServiceNames.ManagedTools,
            new ManagedToolsOptions { McpProfile = "primary", MaximumRounds = 8 });
        var request = new MagicChatCompletionRequest
        {
            Model = "Qwen36-27B",
            Messages = [MagicChatMessage.User("Use a tool.")],
            MagicAiGateway = envelope
        };

        var response = await client.Chat.CompleteAsync(request);
        var captured = Assert.Single(server.Requests, item => item.Path == "/v1/chat/completions");
        using var document = JsonDocument.Parse(captured.Body!);
        var root = document.RootElement;

        Assert.Equal("gateway client test response", response.Choices[0].Message.Content?.GetString());
        Assert.True(root.TryGetProperty(MagicAiGatewayProtocol.PropertyName, out var magic));
        Assert.Equal("loopback-integration-tests", magic.GetProperty("application").GetString());
        Assert.Equal(MagicServiceNames.ManagedTools, magic.GetProperty("service").GetProperty("name").GetString());
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.False(root.GetProperty("messages")[0].TryGetProperty(MagicAiGatewayProtocol.PropertyName, out _));
    }

    [Fact]
    public async Task BufferedMagicCompletionReturnsAggregateUsageAndRunMetadata()
    {
        await using var server = await LoopbackGatewayServer.StartAsync();
        using var directory = new TemporaryDirectory("buffered-magic");
        await using var client = CreateClient(server.Endpoint, directory.Path);
        var request = CreateMagicRequest(client, stream: false);

        var response = await client.Chat.CompleteAsync(request);

        Assert.Equal(5150, response.Usage?.TotalTokens);
        Assert.Equal(700, response.Usage?.CompletionTokenDetails?.ReasoningTokens);
        Assert.Equal(MagicRunStatuses.Completed, response.MagicAiGateway?.Status);
        Assert.Equal(3, response.MagicAiGateway?.ModelCalls);
        Assert.Equal(2, response.MagicAiGateway?.ToolCalls);
        Assert.Equal(3, response.MagicAiGateway?.ModelCallUsage.Count);
    }

    [Fact]
    public async Task StreamingMagicCompletionUsesMagicLogicalCompletionAndStandardDone()
    {
        await using var server = await LoopbackGatewayServer.StartAsync();
        using var directory = new TemporaryDirectory("streaming-magic");
        await using var client = CreateClient(server.Endpoint, directory.Path);
        var request = CreateMagicRequest(client, stream: true, enriched: true);

        await using var session = await client.Chat.StartStreamingAsync(request);
        var updates = new List<MagicChatStreamUpdate>();
        await foreach (var update in session.Updates)
        {
            updates.Add(update);
        }
        var completed = await session.Completion;

        Assert.Equal("gateway client test response", completed.Content);
        Assert.Equal("checking tools... ", completed.Reasoning);
        Assert.Equal(5150, completed.Usage?.TotalTokens);
        Assert.Equal(MagicRunStatuses.Completed, completed.MagicRun?.Status);
        Assert.Contains(updates, static update => update is MagicContentDelta);
        Assert.Contains(updates, static update => update is MagicReasoningDelta);
        Assert.IsType<MagicRunCompletedUpdate>(updates[^1]);
    }

    [Fact]
    public async Task OrdinaryStreamingStillCompletesOnOpenAiDoneMarker()
    {
        await using var server = await LoopbackGatewayServer.StartAsync();
        using var directory = new TemporaryDirectory("ordinary-streaming");
        await using var client = CreateClient(server.Endpoint, directory.Path);
        var request = new MagicChatCompletionRequest
        {
            Model = "Qwen36-27B",
            Messages = [MagicChatMessage.User("Say hello.")],
            Stream = true
        };

        await using var session = await client.Chat.StartStreamingAsync(request);
        await foreach (var _ in session.Updates) { }
        var completed = await session.Completion;

        Assert.Null(completed.MagicRun);
        Assert.Equal(120, completed.Usage?.TotalTokens);
        Assert.Equal("gateway client test response", completed.Content);
    }

    private static MagicChatCompletionRequest CreateMagicRequest(
        MagicAiGatewayClient client,
        bool stream,
        bool enriched = false) => new()
    {
        Model = "Qwen36-27B",
        Messages = [MagicChatMessage.User("Use managed tools.")],
        Stream = stream,
        MagicAiGateway = client.Protocol.CreateEnvelope(
            MagicServiceNames.ManagedTools,
            new ManagedToolsOptions { McpProfile = "primary" },
            requestedRunTimeout: TimeSpan.FromMinutes(30),
            responseMode: enriched ? MagicResponseModes.Enriched : MagicResponseModes.Compatible)
    };

    private static MagicAiGatewayClient CreateClient(Uri endpoint, string stateDirectory) =>
        MagicAiGatewayClient.Create(new MagicAiGatewayClientOptions
        {
            ApplicationId = "loopback-integration-tests",
            EndpointOverride = endpoint,
            Security =
            {
                StateDirectory = stateDirectory,
                TrustMode = GatewayTrustMode.SystemOnly
            }
        });
}
