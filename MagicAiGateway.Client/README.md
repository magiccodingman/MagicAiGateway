# MagicAiGateway.Client

`MagicAiGateway.Client` locates a Magic AI Gateway, establishes a trusted connection, sends ordinary OpenAI-compatible requests, and exposes typed chat and Magic protocol APIs.

## Dependency injection

```csharp
using MagicAiGateway.Client;
using MagicAiGateway.Client.DependencyInjection;

builder.Services.AddMagicAiGatewayClient(options =>
{
    options.ApplicationId = "MyApplication";
    options.ExpectedGatewayName = "MagicAiGateway";
});
```

Inject `IMagicAiGatewayClient`. The facade exposes focused APIs:

```csharp
public sealed class ModelService(IMagicAiGatewayClient gateway)
{
    public Task<HttpResponseMessage> GetModelsAsync(CancellationToken cancellationToken) =>
        gateway.Raw.GetAsync("/v1/models", cancellationToken);
}
```

- `Raw`: arbitrary relative gateway paths and provider-native payloads;
- `Chat`: typed buffered and streaming chat-completions;
- `Protocol`: Magic service discovery and typed envelope creation;
- `Connection`: endpoint/trust state.

## Typed chat

`MagicChatCompletionRequest` is the whole `/v1/chat/completions` request. `MagicChatMessage` is one message inside `Messages`.

```csharp
using MagicAiGateway.Protocol;

var result = await gateway.Chat.CompleteAsync(new MagicChatCompletionRequest
{
    Model = "Qwen36-27B",
    Messages =
    [
        MagicChatMessage.System("Be concise."),
        MagicChatMessage.User("Say hello.")
    ]
});

Console.WriteLine(result.Choices[0].Message.Content?.GetString());
Console.WriteLine(result.Usage?.TotalTokens);
```

Protocol models preserve unknown provider properties through JSON extension data so typed requests do not require the SDK to model every vLLM, llama.cpp, or future OpenAI-compatible option first.

## Discover Magic services

```csharp
var services = await gateway.Protocol.GetServicesAsync(cancellationToken);
var managedTools = await gateway.Protocol.GetServiceAsync(
    MagicServiceNames.ManagedTools,
    cancellationToken: cancellationToken);

Console.WriteLine(managedTools.Description);
Console.WriteLine(managedTools.OptionsSchema);
```

Service descriptors include version, supported endpoints, availability, default/maximum total run duration, option schema, response schema, and enriched streaming event names.

## Enable a Magic service

A Magic request still uses the normal OpenAI-compatible chat endpoint. The root-level `magic_ai_gateway` envelope selects one public service; the gateway decides which internal ordered steps implement it.

```csharp
var envelope = gateway.Protocol.CreateEnvelope(
    MagicServiceNames.ManagedTools,
    new ManagedToolsOptions
    {
        McpProfile = "primary",
        MaximumRounds = 16,
        MaximumToolCalls = 64
    },
    agent: "ResearchAgent",
    requestedRunTimeout: TimeSpan.FromMinutes(30));

var result = await gateway.Chat.CompleteAsync(new MagicChatCompletionRequest
{
    Model = "Qwen36-27B",
    Messages = [MagicChatMessage.User("Use the available tools and answer fully.")],
    MagicAiGateway = envelope
});

Console.WriteLine(result.Choices[0].Message.Content?.GetString());
Console.WriteLine(result.MagicAiGateway?.Status);
Console.WriteLine(result.Usage?.TotalTokens); // Aggregate usage for the entire logical run.
```

When the envelope omits `Application`, the SDK inserts `MagicAiGatewayClientOptions.ApplicationId`. Application and agent values are selectors; the gateway still resolves and authorizes them against the authenticated caller.

`managed_tools` is currently scaffolded. Its schema, lifecycle, timeout, authorization, and MCP/model extension points exist, but the default server implementation reports `service_not_ready` until the real continuation loop is installed.

## Magic-aware streaming

```csharp
var request = new MagicChatCompletionRequest
{
    Model = "Qwen36-27B",
    Messages = [MagicChatMessage.User("Research this and stream the final answer.")],
    MagicAiGateway = gateway.Protocol.CreateEnvelope(
        MagicServiceNames.ManagedTools,
        new ManagedToolsOptions { IncludeReasoning = true },
        responseMode: MagicResponseModes.Enriched)
};

await using var session = await gateway.Chat.StartStreamingAsync(request, cancellationToken);

await foreach (var update in session.Updates)
{
    switch (update)
    {
        case MagicContentDelta content:
            Console.Write(content.Text);
            break;
        case MagicReasoningDelta reasoning:
            Console.Error.Write(reasoning.Text);
            break;
        case MagicToolProgress tool:
            Console.Error.WriteLine($"{tool.EventType}: {tool.ToolName}");
            break;
    }
}

var completed = await session.Completion;
Console.WriteLine(completed.Usage?.TotalTokens);
Console.WriteLine(completed.MagicRun?.Status);
```

For ordinary streams, the standard OpenAI finish chunk and `[DONE]` define completion. For Magic streams, successful Magic run metadata defines logical completion, and the server still emits a normal final OpenAI-shaped chunk plus `[DONE]` to close the transport correctly.

Compatibility mode emits only OpenAI-shaped data chunks and final Magic metadata. Enriched mode additionally allows named reasoning, tool-progress, and run events.

## Timeout behavior

The SDK uses explicit policies rather than a magic sentinel value:

```csharp
builder.Services.AddMagicAiGatewayClient(options =>
{
    options.StandardRequestTimeout = TimeSpan.FromMinutes(3);
    options.ManagedRequestTimeout = Timeout.InfiniteTimeSpan;
});
```

- ordinary typed and raw requests default to three minutes;
- Magic-managed transport is infinite by default because the server owns the logical service deadline;
- caller cancellation tokens remain active in both modes;
- an explicit `requestedRunTimeout` is serialized into the Magic envelope and also bounds the SDK request;
- the server validates and clamps requested durations to the selected service maximum.

A Magic timeout covers the total logical run, including every internal model and tool continuation, not each call separately.

## Client credentials

Anonymous requests remain the default. `ApiKey` is retained as a convenience option and is internally converted into a static credential provider:

```csharp
builder.Services.AddMagicAiGatewayClient(options =>
{
    options.ApplicationId = "MyApplication";
    options.ApiKey = configuration["MagicAiGateway:ApiKey"];
});
```

Applications with an existing authentication system can replace `IGatewayCredentialProvider`. The provider runs for every request, allowing expiring or user-specific tokens to be acquired without rebuilding the gateway connection:

```csharp
using MagicAiGateway.Client.Authentication;

builder.Services.AddSingleton<IGatewayCredentialProvider>(
    new DelegateGatewayCredentialProvider(async (context, cancellationToken) =>
    {
        var token = await existingAuthentication.GetAccessTokenAsync(cancellationToken);
        return GatewayCredential.Bearer(token);
    }));
```

The credential provider affects client/data-plane requests only. Node pairing and fabric control traffic continue to use node certificates and fabric authorization.

## Explicit endpoint

```csharp
builder.Services.AddMagicAiGatewayClient(options =>
{
    options.ApplicationId = "MyApplication";
    options.EndpointOverride = new Uri("https://ai.example.com");
});
```

An explicit endpoint disables discovery. Public certificates use normal platform certificate validation. Local private gateways can be trusted through the application-owned cluster CA without installing that CA into the operating-system trust store.

## Local-first discovery

The default resolution order is:

1. loopback gateway (`https://localhost:7443`);
2. last trusted endpoint;
3. mDNS/DNS-SD discovery;
4. configured remote fallback endpoints.

```csharp
builder.Services.AddMagicAiGatewayClient(options =>
{
    options.ApplicationId = "MyApplication";
    options.Discovery.FallbackEndpoints.Add(new Uri("https://ai.example.com"));
});
```

## Raw streaming

The raw API remains available when an application wants to own all provider-specific framing:

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
{
    Content = JsonContent.Create(payload)
};

await using var response = await gateway.Raw.SendStreamingAsync(request, cancellationToken);
response.Response.EnsureSuccessStatusCode();

using var reader = new StreamReader(response.Stream);
while (await reader.ReadLineAsync(cancellationToken) is { } line)
{
    // Process provider-native SSE.
}
```

The SDK never retries after response bytes begin streaming.

## Non-DI use

```csharp
await using var gateway = MagicAiGatewayClient.Create(new()
{
    ApplicationId = "InteropHost"
});

await gateway.Connection.ConnectAsync();
```
