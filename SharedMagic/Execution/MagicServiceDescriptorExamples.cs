using System.Text.Json;
using System.Text.Json.Nodes;
using MagicAiGateway.Protocol;

namespace SharedMagic.Execution;

public static class MagicServiceDescriptorExamples
{
    public static JsonObject CreateInvocation<TOptions>(
        string serviceName,
        int serviceVersion,
        TOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        return new JsonObject
        {
            ["model"] = "<model>",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "<message>"
                }
            },
            ["stream"] = true,
            [MagicAiGatewayProtocol.PropertyName] = new JsonObject
            {
                ["version"] = MagicAiGatewayProtocol.CurrentVersion,
                ["application"] = "<application>",
                ["agent"] = "<optional-agent>",
                ["service"] = new JsonObject
                {
                    ["name"] = serviceName,
                    ["version"] = serviceVersion,
                    ["options"] = JsonSerializer.SerializeToNode(options, MagicProtocolJson.Options)
                }
            }
        };
    }

    public static JsonObject CreateResponse(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        return new JsonObject
        {
            ["id"] = "chatcmpl-<id>",
            ["object"] = "chat.completion",
            ["model"] = "<model>",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "<final response>"
                    },
                    ["finish_reason"] = "stop"
                }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["total_tokens"] = 0
            },
            [MagicAiGatewayProtocol.PropertyName] = new JsonObject
            {
                ["version"] = MagicAiGatewayProtocol.CurrentVersion,
                ["run_id"] = "run_<id>",
                ["service"] = serviceName,
                ["status"] = MagicRunStatuses.Completed,
                ["model_calls"] = 1,
                ["tool_calls"] = 0,
                ["usage_accuracy"] = MagicUsageAccuracy.ProviderReported
            }
        };
    }
}
