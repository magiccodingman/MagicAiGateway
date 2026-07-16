using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Connection;
using MagicAiGateway.Client.Tests.Infrastructure;

namespace MagicAiGateway.Client.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class RawGatewayClientValidationTests
{
    [Fact]
    public async Task RejectsAbsoluteExternalUrisBeforeResolvingGateway()
    {
        using var directory = new TemporaryDirectory("absolute-uri");
        await using var client = MagicAiGatewayClient.Create(new MagicAiGatewayClientOptions
        {
            ApplicationId = "absolute-uri-test",
            Security = { StateDirectory = directory.Path }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Raw.SendAsync(new HttpRequestMessage(
                HttpMethod.Get,
                "https://example.com/v1/models")));

        Assert.Equal(GatewayConnectionState.Disconnected, client.Connection.State);
    }

    [Fact]
    public async Task RejectsRequestWithoutUri()
    {
        using var directory = new TemporaryDirectory("missing-uri");
        await using var client = MagicAiGatewayClient.Create(new MagicAiGatewayClientOptions
        {
            ApplicationId = "missing-uri-test",
            Security = { StateDirectory = directory.Path }
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, (Uri?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Raw.SendAsync(request));
    }
}
