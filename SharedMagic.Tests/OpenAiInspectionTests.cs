using System.Text;
using SharedMagic.Proxy;

namespace SharedMagic.Tests;

public sealed class OpenAiInspectionTests
{
    [Fact]
    public async Task RecognizesAndRewritesNullGatewayEnvelope()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"model":"demo","messages":[],"magic_ai_gateway":null,"unknown":42}"""));

        var inspection = await OpenAiRequestInspector.InspectAsync(body);
        Assert.True(inspection.HasNullMagicGateway);
        Assert.False(inspection.HasMagicGatewayObject);

        var rewritten = await OpenAiRequestInspector.RemoveNullMagicGatewayAsync(body);
        var json = Encoding.UTF8.GetString(Assert.IsType<byte[]>(rewritten));
        Assert.DoesNotContain("magic_ai_gateway", json, StringComparison.Ordinal);
        Assert.Contains("\"unknown\":42", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsScalarGatewayEnvelopeDuringInspection()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"model":"demo","messages":[],"magic_ai_gateway":true}"""));

        var inspection = await OpenAiRequestInspector.InspectAsync(body);
        Assert.True(inspection.HasInvalidMagicGateway);
    }
}
