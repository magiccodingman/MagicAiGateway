# MagicAiGateway.MCP.Package

`MagicAiGateway.MCP.Package` is the C# framework for building compiled packages that implement the [MagicAiGateway MCP Package Protocol](../MCP/README.md). A consuming project publishes as a NativeAOT `.dll`, `.so`, or `.dylib`; this package supplies the native ABI, multi-instance runtime, MCP transport, lifecycle management, dependency injection, manifest handling, compile-time tool discovery, and developer guardrails.

Package authors work with a small application surface:

- a normal `Program.cs`
- a typed package manifest
- standard .NET dependency injection
- `IHostedService` and `BackgroundService`
- controller-style MCP tool classes
- the official `ModelContextProtocol` server builder for advanced MCP features

They do not copy or edit native pointers, exported functions, instance registries, pipes, channels, or shutdown synchronization.

## Package application

A package contains exactly one accessible static method marked with `[MagicMcpPackage]`:

```csharp
using MagicAiGateway.MCP.Package;
using Microsoft.Extensions.DependencyInjection;

public static class Program
{
    [MagicMcpPackage]
    public static void Configure(MagicMcpPackageBuilder builder)
    {
        builder.Package.ConfigureManifest(manifest =>
        {
            manifest.Name = "Example Package";
            manifest.Version = "1.0.0";
            manifest.Description = "An example MagicAiGateway MCP package.";
        });

        // Alternative: load the author-controlled manifest fields from a UTF-8
        // JSON file placed beside the published native library.
        // builder.Package.AddManifestFile("magic-mcp-package.json");

        builder.Services.AddHostedService<ExampleBackgroundService>();
        builder.AddMcpTools();
    }
}
```

The source generator creates the module initializer that calls this method and freezes the resulting package definition. Configuration failures are captured and surfaced through the native ABI diagnostics rather than escaping from library initialization.

## Manifest

`builder.Package.ConfigureManifest(...)` receives a `MagicMcpPackageManifest`. The package author controls:

- `Name` — required
- `Version` — required semantic version
- `Description` — optional
- `Author` — optional
- `Homepage` — optional absolute URI
- `Metadata` — optional string key/value metadata

The framework controls and serializes the protocol name, ABI version, opaque instance-ID shape, transport framing, and guaranteed runtime capabilities. Those fields cannot be overridden by inline configuration or a manifest file.

Manifest sources are applied in registration order. This allows a file plus an inline override:

```csharp
builder.Package
    .AddManifestFile("magic-mcp-package.json")
    .ConfigureManifest(manifest =>
    {
        manifest.Version = "1.0.1";
    });
```

A file contains the same author-controlled fields:

```json
{
  "name": "Example Package",
  "version": "1.0.0",
  "description": "An example package",
  "author": "Example Developer"
}
```

Relative paths are resolved beside the loaded package library. The manifest must be available before the host starts an instance because `magic_mcp_get_manifest` is valid immediately after the library is loaded.

## MCP tool controllers

Tool classes follow one framework model:

```csharp
using System.ComponentModel;
using MagicAiGateway.MCP.Package;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class WeatherTools(IWeatherService weather) : MagicMcpToolController
{
    [McpServerTool(Name = "get_current_weather")]
    [Description("Gets current weather for a location.")]
    public Task<string> GetCurrentWeatherAsync(
        [Description("City or location.")] string location,
        CancellationToken cancellationToken)
    {
        string instanceId = PackageInstance.InstanceId;
        return weather.GetCurrentAsync(instanceId, location, cancellationToken);
    }
}
```

A tool controller is analogous to an ASP.NET controller:

- the framework creates a fresh controller for every MCP tool invocation
- constructor parameters are resolved from the request service scope
- `PackageInstance` is initialized automatically by `MagicMcpToolController`
- the controller is disposed after the invocation when it implements `IDisposable` or `IAsyncDisposable`
- long-lived state belongs in injected services, not in the controller

Tool-controller lifetime is intentionally not configurable. Do not register controllers as singleton, scoped, or transient services. Register their dependencies instead:

```csharp
builder.Services.AddSingleton<WorkQueue>();
builder.Services.AddScoped<IWeatherService, WeatherService>();
builder.Services.AddHostedService<QueueProcessor>();
```

The resulting lifetime model is:

| Component | Lifetime |
| --- | --- |
| `MagicMcpToolController` | New object per tool invocation |
| Scoped dependency | One per MCP request scope |
| Transient dependency | Normal DI transient behavior |
| Singleton dependency | One per started package instance |
| Hosted service | One per started package instance |

Each package instance owns an independent Generic Host and service provider. A type/factory `AddSingleton<T>()` registration therefore creates one singleton in each instance, not one shared singleton across every instance in the loaded library. Registering an already-created singleton object intentionally shares that object because the same object reference is part of the package definition recipe.

## `AddMcpTools()` and NativeAOT

`builder.AddMcpTools()` is generated at compile time. The generator discovers classes that both:

1. carry `[McpServerToolType]`; and
2. derive from `MagicMcpToolController`.

It emits explicit generic registrations for every valid controller. This gives controller-style discovery without `WithToolsFromAssembly()`, runtime assembly scanning, or trimmed-away methods under NativeAOT.

The analyzer reports build errors when:

- `[McpServerToolType]` is used without `MagicMcpToolController`
- a controller derives from the base but omits `[McpServerToolType]`
- a controller is abstract or static
- `[McpServerTool]` appears outside a valid controller
- a controller tool method is static
- a controller is registered with `AddSingleton`
- the package has no valid `[MagicMcpPackage]` configuration method, or has more than one

The runtime repeats essential validation as defense in depth.

## Advanced MCP configuration

`builder.Mcp` is the official `IMcpServerBuilder`. Use it for capabilities beyond generated tool controllers, including prompts, resources, filters, and custom handlers:

```csharp
builder.Mcp
    .WithPrompts<MyPrompts>()
    .WithResources<MyResources>();
```

Use generic registrations for NativeAOT preservation. Avoid assembly-wide runtime discovery APIs.

The framework always sets MCP `ServerInfo.Name` and `ServerInfo.Version` from the validated package manifest so MCP identity cannot disagree with native package discovery.

## Services, hosted workers, and startup configuration

`builder.Services` is a normal `IServiceCollection`. Its descriptors are frozen as a recipe and copied into every package instance's independent Generic Host.

The host may supply optional UTF-8 JSON when it calls `magic_mcp_start_instance`. The framework:

- validates that the payload is one JSON object
- exposes the original bytes through `MagicMcpPackageInstanceContext.ConfigurationJson`
- adds the JSON to the instance's normal `IConfiguration`

Normal options binding therefore works:

```csharp
builder.Services.AddOptions<MyPackageOptions>()
    .BindConfiguration("myPackage")
    .ValidateOnStart();
```

The startup configuration schema belongs to the package and should be documented by its author.

Default console logging providers are removed. The package is embedded native code; stdout and stderr are not protocol channels. Add an explicit file, telemetry, or structured logging provider when the package needs logs.

## Publishing

The consuming project, not this framework library, selects NativeAOT shared-library output:

```xml
<PropertyGroup>
  <OutputType>Library</OutputType>
  <PublishAot>true</PublishAot>
  <NativeLib>Shared</NativeLib>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

Publish for each supported runtime identifier:

```bash
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r linux-arm64
dotnet publish -c Release -r win-x64
```

The result is a native shared library exporting the ABI documented in [`MCP/magic_mcp_package.h`](../MCP/magic_mcp_package.h). The host loads it for the lifetime of the process, queries its manifest, starts independent instances, pumps MCP messages through `send` and `receive`, stops instances by ID, and calls global shutdown before process termination.

NativeAOT is the C# mechanism for producing that native library. Rust, C++, Go, Zig, and other languages implement the same language-neutral ABI using their own native toolchains.

## Security boundary

A package is trusted native code loaded into the host process. This framework does not sandbox packages or invent authorization around ABI calls. Authentication, authorization, secret handling, tenant isolation, filesystem policy, and business-input validation remain package responsibilities.

The language-neutral protocol and architectural reasoning are documented in:

- [`MCP/README.md`](../MCP/README.md)
- [`MCP/magic_mcp_package.h`](../MCP/magic_mcp_package.h)
- [`Wiki/MCP-Package-Protocol.md`](../Wiki/MCP-Package-Protocol.md)
