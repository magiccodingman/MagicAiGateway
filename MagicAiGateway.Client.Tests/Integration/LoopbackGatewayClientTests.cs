using System.Net.Http.Json;
using System.Text.Json;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Connection;
using MagicAiGateway.Client.Tests.Infrastructure;

namespace MagicAiGateway.Client.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class LoopbackGatewayClientTests
{
    [Fact]
    public async Task ResolvesGatewayIdentityAndGetsModels()
    {
        await using var server = await LoopbackGatewayServer.StartAsync();
        using var directory = new TemporaryDirectory("loopback-models");
        await using var client = CreateClient(server.Endpoint, directory.Path);

        var resolved = await client.Connection.ConnectAsync();
        using var response = await client.Raw.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());

        Assert.Equal(GatewayConnectionState.Connected, client.Connection.State);
        Assert.Equal(server.GatewayId, resolved.Gateway.GatewayId);
        Assert.Equal(server.ClusterId, resolved.Gateway.ClusterId);
        Assert.Equal(server.Endpoint, resolved.BaseUri);
        Assert.Equal("Qwen36-27B", document.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Contains(
            server.Requests,
            request => request.Method == HttpMethod.Get.Method && request.Path == "/v1/models");
    }

    [Fact]
    public async Task SendsBufferedChatCompletionWithoutRewritingPayload()
    {
        await using var server = await LoopbackGatewayServer.StartAsync();
        using var directory = new TemporaryDirectory("buffered-completion");
        await using var client = CreateClient(server.Endpoint, directory.Path);
        var payload = new
        {
            model = "Qwen36-27B",
            messages = new[]
            {
                new { role = "user", content = "Say hello." }
            },
            stream = false,
            custom_provider_option = new { preserve_me = true }
        };

        using var response = await client.Raw.PostJsonAsync(
            "/v1/chat/completions",
            payload);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        var captured = Assert.Single(
            server.Requests,
            request => request.Path == "/v1/chat/completions");
        using var capturedDocument = JsonDocument.Parse(captured.Body!);

        Assert.Equal(
            "gateway client test response",
            document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString());
        Assert.True(
            capturedDocument.RootElement
                .GetProperty("custom_provider_option")
                .GetProperty("preserve_me")
                .GetBoolean());
    }

    [Fact]
    public async Task StreamsServerSentEventsThroughRawClient()
    {
        await using var server = await LoopbackGatewayServer.StartAsync();
        using var directory = new TemporaryDirectory("streaming-completion");
        await using var client = CreateClient(server.Endpoint, directory.Path);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "Qwen36-27B",
                messages = new[]
                {
                    new { role = "user", content = "Say hello." }
                },
                stream = true
            })
        };

        await using var response = await client.Raw.SendStreamingAsync(request);
        response.Response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(response.Stream);
        var dataLines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataLines.Add(line[6..]);
            }
        }

        Assert.True(dataLines.Count >= 4);
        Assert.Contains(dataLines, line => line.Contains("gateway ", StringComparison.Ordinal));
        Assert.Contains(dataLines, line => line.Contains("client test response", StringComparison.Ordinal));
        Assert.Equal("[DONE]", dataLines[^1]);
    }

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
