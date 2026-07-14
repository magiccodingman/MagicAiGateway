using System.Text;
using SharedMagic.Proxy;

namespace SharedMagic.Tests;

public sealed class OpenAiToolCallParserTests
{
    [Fact]
    public void ParsesNonStreamingToolCalls()
    {
        var json = """
        {"choices":[{"index":0,"message":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"weather","arguments":"{\"city\":\"Norfolk\"}"}}]}}]}
        """;

        var call = Assert.Single(OpenAiToolCallParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("call_1", call.Id);
        Assert.Equal("weather", call.Name);
        Assert.Equal("{\"city\":\"Norfolk\"}", call.Arguments);
    }

    [Fact]
    public void ReassemblesStreamingToolCallFragments()
    {
        var accumulator = new StreamingToolCallAccumulator();
        accumulator.ObserveJson(Encoding.UTF8.GetBytes("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_2","function":{"name":"gate","arguments":"{\"x\":"}}]}}]}"""));
        accumulator.ObserveJson(Encoding.UTF8.GetBytes("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"way_tool","arguments":"42}"}}]}}]}"""));

        var call = Assert.Single(accumulator.GetToolCalls());
        Assert.Equal("gateway_tool", call.Name);
        Assert.Equal("{\"x\":42}", call.Arguments);
    }
    [Fact]
    public void ParsesResponsesApiFunctionCalls()
    {
        var json = """
        {"output":[{"type":"function_call","call_id":"call_response","name":"gateway.lookup","arguments":"{\"id\":7}"}]}
        """;

        var call = Assert.Single(OpenAiToolCallParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("call_response", call.Id);
        Assert.Equal("gateway.lookup", call.Name);
        Assert.Equal("{\"id\":7}", call.Arguments);
    }

}
