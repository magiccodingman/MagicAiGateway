using MagicAiGateway.Client.Discovery;

namespace MagicAiGateway.Client.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class GatewayCandidateTests
{
    [Theory]
    [InlineData("https://localhost:7443")]
    [InlineData("https://gateway.local:7443")]
    [InlineData("https://10.0.0.5:7443")]
    [InlineData("https://172.20.1.5:7443")]
    [InlineData("https://192.168.1.5:7443")]
    [InlineData("https://169.254.1.5:7443")]
    public void PrivateAndLocalConfiguredEndpointsAreLocalCandidates(string endpoint)
    {
        var candidate = new GatewayCandidate(
            new Uri(endpoint),
            GatewayCandidateKind.Configured,
            "test");

        Assert.True(candidate.IsLocal);
    }

    [Fact]
    public void PublicConfiguredEndpointIsNotLocalCandidate()
    {
        var candidate = new GatewayCandidate(
            new Uri("https://ai.example.com"),
            GatewayCandidateKind.Configured,
            "test");

        Assert.False(candidate.IsLocal);
    }
}
