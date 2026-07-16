# MagicAiGateway.Client

`MagicAiGateway.Client` is the public .NET SDK foundation for locating a Magic AI Gateway, establishing a secure connection, and sending arbitrary buffered or streaming HTTP requests without hard-coding a gateway host in application code.

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

Inject `IMagicAiGatewayClient` and use a relative gateway path:

```csharp
public sealed class ModelService(IMagicAiGatewayClient gateway)
{
    public Task<HttpResponseMessage> GetModelsAsync(CancellationToken cancellationToken) =>
        gateway.Raw.GetAsync("/v1/models", cancellationToken);
}
```

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
        var token = await existingAuthentication
            .GetAccessTokenAsync(cancellationToken);

        return GatewayCredential.Bearer(token);
    }));
```

Register the custom provider before or after `AddMagicAiGatewayClient`; the explicit application registration is used in preference to the default anonymous/API-key provider.

For non-DI applications, pass a provider directly:

```csharp
await using var gateway = MagicAiGatewayClient.Create(
    new MagicAiGatewayClientOptions
    {
        ApplicationId = "InteropHost"
    },
    new DelegateGatewayCredentialProvider(GetCredentialAsync));
```

The credential provider affects ordinary client requests only. Node pairing and fabric control traffic continue to use the node certificate and fabric authorization system.

## Explicit endpoint

```csharp
builder.Services.AddMagicAiGatewayClient(options =>
{
    options.ApplicationId = "MyApplication";
    options.EndpointOverride = new Uri("https://ai.example.com");
});
```

An explicit endpoint disables discovery. Public certificates use normal platform certificate validation. Local private gateways can be trusted through the application-owned cluster CA without installing that CA into Windows, Linux, macOS, or container trust stores.

## Local-first discovery

The default resolution order is:

1. loopback gateway (`https://localhost:7443`);
2. the last trusted endpoint;
3. mDNS/DNS-SD gateway discovery;
4. configured remote fallback endpoints.

```csharp
builder.Services.AddMagicAiGatewayClient(options =>
{
    options.ApplicationId = "MyApplication";
    options.Discovery.FallbackEndpoints.Add(new Uri("https://ai.example.com"));
});
```

## Streaming

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
    // Process SSE or provider-native streaming data.
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

## Gateway protocol envelope

The canonical root property is available from `MagicAiGatewayProtocol.PropertyName`, and `MagicAiGatewayJson.Attach` can add a typed `MagicAiGatewayEnvelope` without forcing applications to adopt a rigid OpenAI request DTO.
