using MagicAiGateway.Client.Authentication;

namespace MagicAiGateway.Client.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class GatewayCredentialProviderTests
{
    [Fact]
    public async Task StaticApiKeyProducesBearerCredential()
    {
        var provider = new StaticApiKeyCredentialProvider("test-key");
        var context = new GatewayCredentialContext(
            new Uri("https://localhost:7443"),
            HttpMethod.Get,
            new Uri("https://localhost:7443/v1/models"));

        var credential = await provider.GetCredentialAsync(context);

        Assert.NotNull(credential);
        Assert.Equal("Bearer", credential.Scheme);
        Assert.Equal("test-key", credential.Parameter);
    }

    [Fact]
    public async Task DelegateProviderReceivesPerRequestContext()
    {
        GatewayCredentialContext? observed = null;
        var provider = new DelegateGatewayCredentialProvider((context, _) =>
        {
            observed = context;
            return ValueTask.FromResult<GatewayCredential?>(
                GatewayCredential.Bearer("dynamic-token"));
        });
        var expectedUri = new Uri("https://gateway.example/v1/chat/completions");

        var credential = await provider.GetCredentialAsync(new GatewayCredentialContext(
            new Uri("https://gateway.example"),
            HttpMethod.Post,
            expectedUri));

        Assert.NotNull(credential);
        Assert.Equal(expectedUri, observed?.RequestUri);
        Assert.Equal(HttpMethod.Post, observed?.Method);
    }
}
