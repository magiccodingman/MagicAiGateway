# MagicAiGateway MCP Package Protocol

This document explains how to implement a MagicAiGateway MCP package in **any language**.

The package protocol is not a C# API and does not require .NET. A package may be written in C, C++, Rust, Zig, Go, NativeAOT-enabled .NET, or any other language capable of producing a native shared library and exposing the required C-compatible ABI.

The canonical ABI declarations live in [`MCP/magic_mcp_package.h`](../MCP/magic_mcp_package.h), and the normative protocol definition lives in [`MCP/README.md`](../MCP/README.md). This wiki page explains how those requirements fit together and why the protocol is designed this way.

## What a package is

A MagicAiGateway MCP package is a compiled native shared library:

- Windows: `.dll`
- Linux: `.so`
- macOS: `.dylib`

MagicAiGateway loads the library into its own process, resolves a fixed set of exported functions, and communicates with the package through those functions.

The package is responsible for everything behind that boundary. It may initialize services, dependency-injection containers, background workers, threads, databases, files, network clients, queues, schedulers, or any other internal runtime it needs.

MagicAiGateway does not inspect or control those internals. It only requires that the package:

1. exposes the required ABI,
2. manages its own lifecycle correctly,
3. accepts and emits valid MCP JSON-RPC messages,
4. keeps each started instance isolated,
5. never allow language-specific objects, exceptions, panics, or allocators to cross the native boundary.

## Why the protocol uses a native ABI

The goal is to let packages behave like independently designed applications without requiring every package to run an executable, open a port, host HTTP, or communicate through stdout.

A small C ABI provides the most portable compiled boundary available across languages and operating systems:

```text
MagicAiGateway
    |
    |-- LoadLibrary / dlopen
    |-- resolve exported functions
    |
    `-- exchange MCP messages through byte buffers
```

This keeps the top-level host language-agnostic. The host does not need to understand how Rust starts Tokio tasks, how C++ manages threads, how Go organizes goroutines, or how .NET builds a Generic Host. Each language uses its own preferred architecture internally.

## Required exports

ABI version 1 requires the following exported functions:

| Export | Responsibility |
| --- | --- |
| `magic_mcp_get_abi_version` | Return the package ABI version. ABI v1 returns `1`. |
| `magic_mcp_get_manifest` | Return package identity and protocol metadata as UTF-8 JSON. |
| `magic_mcp_start_instance` | Create and fully start one independent package instance. |
| `magic_mcp_send` | Deliver one complete MCP JSON-RPC message to an instance. |
| `magic_mcp_receive` | Read one complete MCP JSON-RPC message emitted by an instance. |
| `magic_mcp_stop_instance` | Gracefully stop and destroy one specific instance. |
| `magic_mcp_list_instances` | Return the IDs of all currently live instances. |
| `magic_mcp_shutdown` | Stop every remaining instance before host shutdown. |
| `magic_mcp_get_last_error` | Return thread-local diagnostic text for the previous ABI call. |

Exports must use C linkage and the `cdecl` calling convention described by the canonical header.

No exception, panic, stack unwind, or language-owned object may escape an exported function. Every exported function must catch or contain failures and return a defined status code instead.

## Package manifest

The host must be able to inspect package identity before starting an instance.

At minimum, the manifest contains:

```json
{
  "protocol": "magic-ai-gateway-mcp-package",
  "abiVersion": 1,
  "name": "Example Package",
  "version": "1.0.0"
}
```

`abiVersion` identifies the native package contract. It is separate from the MCP protocol version negotiated through MCP itself.

A package may add descriptive metadata or package-specific capabilities. Hosts must ignore unknown fields unless a later ABI version defines them.

## Instance lifecycle

A loaded library is a package runtime. A started instance is one independent MCP server session hosted by that runtime.

```text
Loaded package library
    |
    |-- instance A
    |     |-- package-defined state
    |     |-- workers and services
    |     `-- MCP session A
    |
    `-- instance B
          |-- separate state
          |-- separate workers and services
          `-- MCP session B
```

### Starting an instance

`magic_mcp_start_instance` accepts optional package-defined configuration as UTF-8 JSON.

The package must not return success until the instance is ready to receive MCP messages. Starting may include:

- creating an internal service container,
- starting worker threads or asynchronous runtimes,
- opening package-owned resources,
- initializing queues or state,
- creating the MCP server session.

On success, the package returns an opaque 16-byte instance ID.

The host treats this ID only as bytes. It must not parse it, change its byte order, infer structure from it, or assume it is a UUID even if a particular implementation uses one internally.

### Multiple instances

A package must support separate live instances when multiple successful start calls occur.

Each instance may have its own internal state and resources. Stopping one instance must never stop or corrupt another.

The package chooses how to implement this internally. Common designs include:

- a map from instance IDs to runtime objects,
- one asynchronous runtime with isolated session state,
- dedicated worker threads per instance,
- shared infrastructure with explicitly isolated instance contexts.

### Stopping an instance

`magic_mcp_stop_instance` targets one opaque ID.

Before returning success, the package must:

1. prevent new work from being accepted for that instance,
2. signal cancellation to active operations,
3. stop package-owned background work for that instance,
4. release instance-owned resources,
5. terminate its MCP session,
6. remove the ID from the live-instance set.

A stop operation may race with send or receive calls. The package must resolve that race without memory corruption, deadlock, or use-after-free. An in-flight operation may complete successfully or return a defined stopped/not-found status.

### Package shutdown

`magic_mcp_shutdown` stops every remaining live instance.

The host calls shutdown before process termination. ABI v1 requires the shared library to remain loaded for the lifetime of the host process; the host must not call `dlclose`, `FreeLibrary`, or an equivalent unload operation after loading a package.

This rule avoids runtime-specific unloading hazards and gives all package languages one predictable lifetime model.

## MCP communication

The native ABI is a transport for MCP. It does not replace MCP or define another tool protocol.

After an instance starts, the host sends ordinary MCP JSON-RPC messages such as initialization, tool discovery, and tool calls. The package processes those messages using any MCP implementation it chooses and emits ordinary MCP JSON-RPC responses or notifications.

### Send

`magic_mcp_send` accepts exactly one complete UTF-8 MCP JSON-RPC object.

The byte length is provided by the ABI, so the payload does not include:

- a null terminator,
- an HTTP envelope,
- a content-length header,
- a custom length prefix,
- stdout or newline framing required by the public ABI.

The package may adapt the message into any private internal framing required by its MCP library.

### Receive

`magic_mcp_receive` returns exactly one complete UTF-8 MCP JSON-RPC object.

Send and receive are separate because MCP is duplex. A package may emit more than a simple direct response, including:

- progress notifications,
- logging notifications,
- cancellation-related traffic,
- resource notifications,
- server-initiated requests supported by MCP,
- future MCP message types.

The host should maintain one receive pump per live instance. The package must preserve message order for each instance.

A package must not assume that every send immediately produces one response or that a response must be returned from the same native call.

## Buffer and memory ownership

Memory ownership never crosses the ABI boundary.

- The host allocates input and output buffers.
- The package copies output into host-provided memory.
- The package must not return memory that the host is expected to free.
- The host must not pass ownership of its allocator or buffers to the package.
- The package must not retain input pointers after the native function returns.
- Payloads are UTF-8 and length-delimited, not null-terminated.

Variable-length outputs use a query-size pattern:

1. Call with a null output pointer and zero capacity.
2. Read the required length from `output_length`.
3. Allocate a host-owned buffer.
4. Call again with that buffer.

For `magic_mcp_receive`, a buffer-too-small result must not discard the pending MCP message. The same message remains available for the retry.

This design avoids allocator mismatches between languages and runtimes, which are a common source of crashes in native plugin systems.

## Concurrency requirements

A package must support concurrent ABI calls involving different instance IDs.

For each individual instance, it must preserve:

- the order of accepted send messages,
- the order of emitted receive messages,
- safe coordination between send, receive, and stop,
- valid state transitions when internal services fail.

The implementation may serialize operations internally where necessary. The protocol does not require lock-free behavior or one thread per instance; it requires correctness and isolation.

If an instance fails internally and can no longer communicate, it must no longer be reported as live. Receive calls must be unblocked and return an appropriate failure rather than waiting forever on a dead runtime.

## Error handling

Normal protocol outcomes are returned as numeric status codes defined in the canonical header.

Human-readable diagnostics may be obtained through `magic_mcp_get_last_error`. These diagnostics are intended for logs and troubleshooting only. Hosts must not parse error strings for control flow.

Package authors should translate internal failures into the closest defined status while keeping language-specific exceptions or panic payloads inside the package.

## Security and trust boundary

A package is native code executing inside the MagicAiGateway process. ABI v1 does not sandbox packages or authenticate native calls.

Only trusted binaries should be loaded.

The package is responsible for any security required by its own behavior, including:

- authorization,
- authentication to external systems,
- secret storage,
- input validation,
- tenant isolation,
- filesystem restrictions,
- network policy,
- container policy,
- permission checks before destructive tools run.

MagicAiGateway transports MCP messages to the package. It does not inspect or guarantee the security of the package's internal implementation.

## Implementation checklist for any language

A language implementation is compatible when it can answer yes to all of the following:

- It compiles to a native shared library for the target platform.
- It exports every ABI v1 function with C linkage and `cdecl`.
- It returns a valid manifest before instances are started.
- It creates a complete, ready runtime for every successful start call.
- It returns a unique opaque 16-byte ID for every live instance.
- It keeps instance state and lifecycle isolated.
- It accepts complete MCP JSON-RPC messages through `magic_mcp_send`.
- It emits complete MCP JSON-RPC messages through `magic_mcp_receive`.
- It supports duplex MCP traffic rather than assuming strict request/response calls.
- It gracefully stops one targeted instance without affecting others.
- It can enumerate the currently live instance IDs.
- It can shut down every instance before process termination.
- It uses caller-owned buffers and never crosses allocators.
- It contains every exception, panic, or language-specific failure inside the ABI boundary.
- It handles concurrent calls and stop/send/receive races safely.

## Language-specific frameworks are optional

The protocol deliberately does not mandate an internal framework.

A Rust package may use Tokio. A C++ package may use Boost.Asio or custom threads. A Go package may use goroutines. A .NET package may use NativeAOT, dependency injection, `IHostedService`, and the official MCP SDK.

Those are implementation conveniences, not protocol requirements.

The only externally observable contract is the compiled shared library ABI and the MCP messages carried through it.