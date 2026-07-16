using System.Text.Json.Nodes;
using MagicAiGateway.Client.Protocol;

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

        var result = MagicAiGatewayJson.Attach(source, new MagicAiGatewayEnvelope
        {
            Operation = "tool-loop",
            Options = new JsonObject { ["maximum_rounds"] = 8 }
        });

        Assert.False(source.ContainsKey(MagicAiGatewayProtocol.PropertyName));
        Assert.Equal(
            "tool-loop",
            result[MagicAiGatewayProtocol.PropertyName]?["operation"]?.GetValue<string>());
        Assert.Equal(
            8,
            result[MagicAiGatewayProtocol.PropertyName]?["options"]?["maximum_rounds"]?.GetValue<int>());
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

        var result = MagicAiGatewayJson.Remove(source);

        Assert.True(source.ContainsKey(MagicAiGatewayProtocol.PropertyName));
        Assert.False(result.ContainsKey(MagicAiGatewayProtocol.PropertyName));
        Assert.Equal("Qwen36-27B", result["model"]?.GetValue<string>());
    }
}
