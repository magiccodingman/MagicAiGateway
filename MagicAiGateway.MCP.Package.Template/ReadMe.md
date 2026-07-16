# MagicAiGateway C# MCP Package Template

This project is the C# implementation template for the [MagicAiGateway MCP Package Protocol](../MCP/README.md). It publishes as a NativeAOT shared library and gives package authors a familiar .NET application model behind the protocol boundary:

- `Program.cs`
- dependency injection
- configuration through `IConfiguration`
- singleton, scoped, and transient services
- `IHostedService` and `BackgroundService`
- attributed MCP tools with constructor injection
- independent state and service lifetimes for every started package instance

There is no HTTP server, Kestrel listener, controller routing, stdin protocol, stdout protocol, HTTPS redirection, or authorization middleware in the template. MagicAiGateway loads the compiled `.dll`, `.so`, or `.dylib` and communicates through the native ABI in `Runtime/MagicMcpExports.cs`.

## The developer experience

Most package development happens in `Program.cs`, `MCP/`, and your own service folders. The native ABI and transport plumbing under `Runtime/` are infrastructure and normally should not need modification.

```text
MagicAiGateway.MCP.Package.Template/
├── Program.cs                  Application composition and DI
├── MCP/
│   └── ExampleTools.cs         Controller-like MCP example
├── Services/
│   ├── ExampleState.cs         Per-instance singleton state
│   └── ExampleBackgroundService.cs
└── Runtime/
    ├── MagicMcpExports.cs      Native ABI exports
    ├── PackageRuntime.cs       Multi-instance registry
    ├── PackageInstance.cs      Host + MCP lifetime for one instance
    └── PackageManifest.cs      Required package name/version metadata
```

## Program.cs

`Program.BuildHost` runs once for every call to `magic_mcp_start_instance`. Each invocation builds a separate Generic Host and DI container.

Register services exactly as you would in a normal hosted .NET application:

```csharp
builder.Services.AddSingleton<MyState>();
builder.Services.AddScoped<MyRepository>();
builder.Services.AddTransient<MyFormatter>();
builder.Services.AddHostedService<MyBackgroundWorker>();
```

Every package instance receives its own singleton services and hosted workers. Stopping one instance gracefully stops only that host and its services.

The template intentionally starts from `Host.CreateEmptyApplicationBuilder`. That prevents an embedded package from silently consuming the parent process's configuration files, environment-variable conventions, command-line arguments, or console logging providers. Configuration explicitly supplied when the instance is started is added to the normal `IConfiguration` pipeline.

## MCP tools: controller-like classes with DI

`MCP/ExampleTools.cs` demonstrates the intended organization. The class receives services through constructor injection, and methods marked with `[McpServerTool]` become MCP tools:

```csharp
[McpServerToolType]
public sealed class FileTools(IFileService files)
{
    [McpServerTool(Name = "read_file")]
    [Description("Reads a UTF-8 text file owned by this package instance.")]
    public Task<string> ReadFileAsync(
        [Description("Package-relative file path.")] string path,
        CancellationToken cancellationToken)
    {
        return files.ReadAsync(path, cancellationToken);
    }
}
```

Register each tool class explicitly in `Program.cs`:

```csharp
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = PackageManifest.Name,
            Version = PackageManifest.Version
        };
    })
    .WithTools<ExampleTools>()
    .WithTools<FileTools>();
```

Explicit generic registration is important for NativeAOT. Avoid `WithToolsFromAssembly()` in this template because assembly-wide runtime discovery depends on reflection and may be trimmed.

The MCP SDK handles tool discovery, generated input schemas, argument binding, DI resolution, cancellation tokens, invocation, and MCP response formatting. Tool methods should still validate their own business inputs and define clear `Description` attributes.

## Background services and state

`ExampleBackgroundService` starts automatically as part of `magic_mcp_start_instance` because it is registered with `AddHostedService`. It receives graceful cancellation when that instance is stopped.

`ExampleState` is a singleton shared by the background service and MCP tools inside one instance. Starting a second instance creates another `ExampleState`; the two do not share state unless you intentionally introduce static or external shared storage.

## Instance context and configuration

`PackageInstanceContext` is registered in DI and provides:

- the opaque instance ID in display form
- the instance start time
- the original host-provided configuration bytes

Non-empty startup configuration must be one UTF-8 JSON object. It is also added to `IConfiguration`, so normal options binding works:

```csharp
builder.Services.Configure<MyPackageOptions>(
    builder.Configuration.GetSection("myPackage"));
```

The configuration schema is package-defined. Document required fields in your package's own README.

## Manifest

Edit `Runtime/PackageManifest.cs` before publishing a real package:

```csharp
public const string Name = "My MCP Package";
public const string Version = "1.0.0";
```

`name`, `version`, and `abiVersion` are required package metadata. Keep `AbiVersion` at `1` while implementing this ABI. Package versioning is independent and belongs to your package.

You may extend the JSON manifest with package-specific metadata, but do not remove or rename required fields.

## Logging

The template clears default logging providers because stdout and stderr belong to the parent process and are not protocol channels. A package that needs logs should configure an explicit destination it owns, such as a file, structured logging sink, telemetry service, or an MCP logging/diagnostic feature.

Do not restore console logging and then rely on the host to parse it. All host/package protocol communication must remain on the native ABI.

## Publishing the native library

Publish once for each target operating system and architecture. NativeAOT produces the platform-native shared library and includes the exported C ABI symbols.

Examples:

```bash
# Linux x64 -> .so
dotnet publish -c Release -r linux-x64

# Linux ARM64 -> .so
dotnet publish -c Release -r linux-arm64

# Windows x64 -> .dll
dotnet publish -c Release -r win-x64
```

Build on the target operating system unless your NativeAOT toolchain explicitly supports the desired cross-compilation path. The native artifact is placed under the RID-specific `bin/Release/net10.0/<rid>/publish/` directory.

This project is a library, so `dotnet run` is not its normal execution model. A MagicAiGateway host or ABI test harness loads the published library and calls its exports.

## Runtime behavior

For each instance, the infrastructure:

1. validates optional configuration JSON
2. creates a new DI container and Generic Host
3. starts all hosted services
4. creates an MCP server using the official `ModelContextProtocol` package
5. opens an in-memory duplex transport
6. accepts complete MCP JSON-RPC messages through `magic_mcp_send`
7. exposes responses and notifications through `magic_mcp_receive`
8. gracefully cancels and disposes the MCP server, background services, and host on `magic_mcp_stop_instance`

The host owns all native buffers. The package never returns memory that another runtime must free.

## Security

The package runs as native code inside the process that loads it. The package protocol does not provide a sandbox, authentication layer, authorization policy, or automatic validation of package behavior.

Treat package binaries as trusted code. Implement any package-specific authentication, authorization, secret management, tenant isolation, filesystem restrictions, container policy, and input validation inside the package itself.

## Creating a real package

1. Rename the project and namespaces.
2. Set the package name and version in `PackageManifest`.
3. Replace `ExampleTools`, `ExampleState`, and `ExampleBackgroundService` with your implementation.
4. Register your services and every MCP tool type in `Program.cs`.
5. Document your startup configuration schema and internal security expectations.
6. Publish for each supported RID.
7. Test the exported ABI and MCP initialization/tool calls before distributing the binary.

The language-neutral ABI contract, status codes, memory rules, and lifecycle requirements are documented in [`MCP/README.md`](../MCP/README.md) and [`MCP/magic_mcp_package.h`](../MCP/magic_mcp_package.h).
