using System.Text.Json.Nodes;
using MagicAiGateway.Protocol;

namespace MagicAiGateway.Client.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class ProtocolEnvelopeTests
{
    [Fact]
    public void AttachAddsCanonicalGatewayEnvelopeWithoutMutatingSource()
    {
        var source = new JsonObject
        {
            ["model"] = "Qwen36-27B",
            ["stream"] = true
        };

        var result = MagicProtocolJson.Attach(source, new MagicAiGatewayEnvelope
        {
            Application = "GameShow",
            Service = MagicServiceInvocation.Create(
                MagicServiceNames.ManagedTools,
                new ManagedToolsOptions { MaximumRounds = 8 })
        });

        Assert.False(source.ContainsKey(MagicAiGatewayProtocol.PropertyName));
        Assert.Equal(
            MagicServiceNames.ManagedTools,
            result[MagicAiGatewayProtocol.PropertyName]?["service"]?["name"]?.GetValue<string>());
        Assert.Equal(
            8,
            result[MagicAiGatewayProtocol.PropertyName]?["service"]?["options"]?["maximum_rounds"]?.GetValue<int>());
    }

    [Fact]
    public void RemoveDeletesOnlyGatewayEnvelopeWithoutMutatingSource()
    {
        var source = new JsonObject
        {
            ["model"] = "Qwen36-27B",
            [MagicAiGatewayProtocol.PropertyName] = new JsonObject
            {
                ["version"] = MagicAiGatewayProtocol.CurrentVersion
            }
        };

        var result = MagicProtocolJson.Remove(source);

        Assert.True(source.ContainsKey(MagicAiGatewayProtocol.PropertyName));
        Assert.False(result.ContainsKey(MagicAiGatewayProtocol.PropertyName));
        Assert.Equal("Qwen36-27B", result["model"]?.GetValue<string>());
    }
}
