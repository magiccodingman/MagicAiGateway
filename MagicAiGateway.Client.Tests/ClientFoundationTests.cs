using System.Text.Json.Nodes;
using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Protocol;
using MagicAiGateway.Client.Security;

namespace MagicAiGateway.Client.Tests;

public sealed class ClientFoundationTests
{
    [Fact]
    public void AttachAddsCanonicalGatewayEnvelopeWithoutMutatingSource()
    {
        var source = new JsonObject
        {
            ["model"] = "Qwen36-27B",
            ["stream"] = true
        };

        var result = MagicAiGatewayJson.Attach(source, new MagicAiGatewayEnvelope
        {
            Operation = "tool-loop",
            Options = new JsonObject { ["maximum_rounds"] = 8 }
        });

        Assert.False(source.ContainsKey(MagicAiGatewayProtocol.PropertyName));
        Assert.Equal("tool-loop", result[MagicAiGatewayProtocol.PropertyName]?["operation"]?.GetValue<string>());
        Assert.Equal(8, result[MagicAiGatewayProtocol.PropertyName]?["options"]?["maximum_rounds"]?.GetValue<int>());
    }

    [Fact]
    public async Task FileTrustStoreRoundTripsGatewayIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "magic-ai-client-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new MagicAiGatewayClientOptions { ApplicationId = "tests" };
            options.Security.StateDirectory = directory;
            var store = new FileGatewayTrustStore(options);
            var expected = new GatewayTrustRecord(
                "MagicAiGateway",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Convert.ToBase64String([1, 2, 3]),
                "https://localhost:7443",
                DateTimeOffset.UtcNow);

            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RawClientRejectsAbsoluteExternalUris()
    {
        var directory = Path.Combine(Path.GetTempPath(), "magic-ai-client-tests", Guid.NewGuid().ToString("N"));
        await using var client = MagicAiGatewayClient.Create(new MagicAiGatewayClientOptions
        {
            ApplicationId = "absolute-uri-test",
            Security = { StateDirectory = directory }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Raw.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.com/v1/models")));

        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
