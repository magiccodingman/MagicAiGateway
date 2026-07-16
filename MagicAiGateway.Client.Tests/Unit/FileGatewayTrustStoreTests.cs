using MagicAiGateway.Client.Configuration;
using MagicAiGateway.Client.Security;
using MagicAiGateway.Client.Tests.Infrastructure;

namespace MagicAiGateway.Client.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class FileGatewayTrustStoreTests
{
    [Fact]
    public async Task RoundTripsGatewayIdentity()
    {
        using var directory = new TemporaryDirectory("trust-round-trip");
        var options = new MagicAiGatewayClientOptions { ApplicationId = "tests" };
        options.Security.StateDirectory = directory.Path;
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

    [Fact]
    public async Task ClearRemovesPersistedTrust()
    {
        using var directory = new TemporaryDirectory("trust-clear");
        var options = new MagicAiGatewayClientOptions { ApplicationId = "tests" };
        options.Security.StateDirectory = directory.Path;
        var store = new FileGatewayTrustStore(options);
        await store.SaveAsync(new GatewayTrustRecord(
            "MagicAiGateway",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Convert.ToBase64String([1, 2, 3]),
            "https://localhost:7443",
            DateTimeOffset.UtcNow));

        await store.ClearAsync();

        Assert.Null(await store.LoadAsync());
    }
}
