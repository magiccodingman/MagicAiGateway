using System.Net.Http.Json;
using System.Text.Json;

namespace MagicAiGateway.Client.Tests.Live;

[Trait("Category", "Live")]
public sealed class LiveGatewayClientTests
{
    [Fact]
    public async Task ConnectsAndValidatesGatewayIdentity()
    {
        await using var context = await LiveGatewayTestContext.CreateAsync();
        var connection = context.Client.Connection.Current;

        Assert.NotNull(connection);
        Assert.Equal(context.Settings.ExpectedGatewayName, connection.Gateway.Name);
        Assert.NotEqual(Guid.Empty, connection.Gateway.GatewayId);
        Assert.NotEqual(Guid.Empty, connection.Gateway.ClusterId);
        Assert.Contains("openai-proxy", connection.Gateway.Features);
    }

    [Fact]
    public async Task ModelsEndpointReturnsOpenAiModelList()
    {
        await using var context = await LiveGatewayTestContext.CreateAsync();
        using var timeout = context.CreateTimeoutSource();
        using var response = await context.Client.Raw.GetAsync(
            "/v1/models",
            timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token),
            cancellationToken: timeout.Token);

        Assert.Equal("list", document.RootElement.GetProperty("object").GetString());
        var models = document.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, models.ValueKind);
        Assert.NotEmpty(models.EnumerateArray());

        if (!string.IsNullOrWhiteSpace(context.Settings.Model))
        {
            Assert.Contains(
                models.EnumerateArray(),
                model => string.Equals(
                    model.GetProperty("id").GetString(),
                    context.Settings.Model,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task BufferedChatCompletionReturnsAssistantOutput()
    {
        await using var context = await LiveGatewayTestContext.CreateAsync(requireInference: true);
        using var timeout = context.CreateTimeoutSource();
        using var response = await context.Client.Raw.PostJsonAsync(
            "/v1/chat/completions",
            new
            {
                model = context.Settings.Model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = "Reply with one short sentence confirming this client test worked."
                    }
                },
                stream = false,
                temperature = 0.1,
                max_tokens = 512
            },
            timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token),
            cancellationToken: timeout.Token);

        var choice = document.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = ReadOptionalString(message, "content");
        var reasoning = ReadOptionalString(message, "reasoning_content");

        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.True(
            !string.IsNullOrWhiteSpace(content) || !string.IsNullOrWhiteSpace(reasoning),
            "The completion returned neither content nor reasoning_content.");
    }

    [Fact]
    public async Task StreamingChatCompletionEmitsChunksAndDoneMarker()
    {
        await using var context = await LiveGatewayTestContext.CreateAsync(requireInference: true);
        using var timeout = context.CreateTimeoutSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = context.Settings.Model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = "Reply with one short sentence confirming streaming works."
                    }
                },
                stream = true,
                temperature = 0.1,
                max_tokens = 512
            })
        };

        await using var response = await context.Client.Raw.SendStreamingAsync(
            request,
            timeout.Token);
        response.Response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(response.Stream);
        var dataLines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataLines.Add(line[6..]);
            }
        }

        Assert.NotEmpty(dataLines);
        Assert.Equal("[DONE]", dataLines[^1]);
        Assert.Contains(dataLines, IsCompletionChunk);
    }

    private static bool IsCompletionChunk(string line)
    {
        if (line == "[DONE]") return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("choices", out var choices) &&
                   choices.ValueKind == JsonValueKind.Array &&
                   choices.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadOptionalString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
