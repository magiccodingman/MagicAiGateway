# MagicAiGateway MCP Package Protocol

The MagicAiGateway MCP Package Protocol lets MagicAiGateway load a compiled native library as a long-lived, multi-instance MCP package. A package may be written in any language capable of producing a native shared library with the required C ABI, including C, C++, Rust, Zig, Go, and NativeAOT-enabled .NET.

The gateway does **not** prescribe the package's internal architecture. A package may use dependency injection, background workers, threads, containers, databases, files, its own authorization model, or no framework at all. The gateway only requires the ABI below and valid MCP communication through it.

This protocol does not use HTTP, ports, controllers, stdin, or stdout. The shared library is loaded into the host process and messages cross the boundary through caller-owned byte buffers.

## Core model

Loading the shared library activates the package runtime. Calling `magic_mcp_start_instance` creates one independent live instance and returns an opaque 16-byte ID. Every subsequent send, receive, or stop operation targets that ID.

Each instance is one MCP server session and may own arbitrary internal state. Multiple instances from the same library may run concurrently.

```text
MagicAiGateway host
    |
    |-- load library (.dll / .so / .dylib)
    |-- resolve ABI v1 exports
    |
    |-- start_instance(config) -> instance A
    |       |-- package-defined services
    |       |-- package-defined background work
    |       `-- MCP server session A
    |
    `-- start_instance(config) -> instance B
            |-- separate services and state
            `-- MCP server session B
```

## Required exports

The canonical C declarations are in [`magic_mcp_package.h`](magic_mcp_package.h).

| Export | Purpose |
| --- | --- |
| `magic_mcp_get_abi_version` | Returns the package ABI version. ABI v1 returns `1`. |
| `magic_mcp_get_manifest` | Returns required package metadata as UTF-8 JSON. |
| `magic_mcp_start_instance` | Creates and fully starts one independent instance. |
| `magic_mcp_send` | Delivers one MCP JSON-RPC message to an instance. |
| `magic_mcp_receive` | Reads one MCP JSON-RPC message emitted by an instance. |
| `magic_mcp_stop_instance` | Gracefully stops and destroys one instance. |
| `magic_mcp_list_instances` | Returns the opaque IDs of all currently live instances. |
| `magic_mcp_shutdown` | Stops every remaining instance before host shutdown. |
| `magic_mcp_get_last_error` | Returns thread-local diagnostic text for the last ABI call. |

Exports use the C calling convention (`cdecl`) and must never allow an exception, panic, or language-specific object to cross the ABI boundary.

## Manifest

Every package must provide a UTF-8 JSON manifest before any instance is started. At minimum it must include:

```json
{
  "protocol": "magic-ai-gateway-mcp-package",
  "abiVersion": 1,
  "name": "Example Package",
  "version": "1.0.0"
}
```

`name` and `version` identify the package. `abiVersion` identifies this native interoperability contract; it is separate from the MCP protocol version negotiated inside MCP messages.

Packages may add their own metadata and capability fields. Hosts must ignore unknown manifest properties unless a later ABI version says otherwise.

## Instance lifecycle

### Start

`magic_mcp_start_instance` accepts optional UTF-8 configuration JSON and must not report success until the instance is ready to receive MCP messages.

An empty configuration is represented by a null pointer with a zero length. A non-empty value must contain exactly one JSON object. Its schema belongs to the package and should be documented by the package author.

On success, the package writes exactly 16 bytes to `instance_id_output`. The ID is opaque. Hosts must not parse it, change byte order, convert it to text and back, or infer meaning from it.

### Send and receive

`magic_mcp_send` accepts exactly one complete UTF-8 MCP JSON-RPC object. No null terminator, newline delimiter, HTTP envelope, or length prefix is included in the message bytes because the ABI already supplies the length.

`magic_mcp_receive` returns exactly one complete UTF-8 MCP JSON-RPC object. Send and receive are deliberately separate: MCP is duplex, so an instance can emit responses, notifications, progress updates, cancellation-related traffic, or future server-initiated messages independently of a particular `send` call.

A host should run one receive pump per live instance. Calls are safe to issue from different host threads, but a host should avoid multiple simultaneous receive loops for the same ID because message ordering belongs to that instance.

### Stop

`magic_mcp_stop_instance` targets one ID. It must request graceful cancellation, stop package-defined background work, release instance-owned resources, and remove the ID from the live-instance set before returning success.

Stopping one instance must not stop any other instance from the same library.

### Shutdown

`magic_mcp_shutdown` stops all remaining instances. The host should call it before process termination. No ABI call may race with shutdown.

ABI v1 requires the package library to remain loaded for the lifetime of the host process. Hosts must not call `dlclose`, `FreeLibrary`, or an equivalent unload operation after loading a package. This uniform rule also matches NativeAOT, whose runtime does not support unloading a loaded NativeAOT shared library.

## Buffer ownership

Memory never changes owners across the ABI boundary.

- The host allocates every input and output buffer.
- The package copies data into host-provided output memory.
- The package never returns a pointer that the host must free.
- The host never asks the package to free host memory.
- UTF-8 payloads are length-delimited and are not null-terminated.

Variable-length outputs use a query-size pattern:

1. Call with `output = NULL` and `output_capacity = 0`.
2. Read the required byte count from `output_length`.
3. Allocate that many bytes.
4. Call again with the allocated buffer.

For `magic_mcp_receive`, `MAGIC_MCP_BUFFER_TOO_SMALL` does not discard the message. The same message remains pending for the next receive call.

## Status codes

| Value | Name | Meaning |
| ---: | --- | --- |
| `0` | `MAGIC_MCP_SUCCESS` | The operation completed. |
| `1` | `MAGIC_MCP_NO_MESSAGE` | The receive timeout elapsed or a poll found nothing. |
| `2` | `MAGIC_MCP_INVALID_ARGUMENT` | A pointer, length, timeout, configuration, or message was invalid. |
| `3` | `MAGIC_MCP_INSTANCE_NOT_FOUND` | The opaque ID is not currently live. |
| `4` | `MAGIC_MCP_BUFFER_TOO_SMALL` | `output_length` contains the required size. |
| `5` | `MAGIC_MCP_INSTANCE_STOPPED` | The target stopped while the operation was in progress. |
| `100` | `MAGIC_MCP_INTERNAL_ERROR` | The package failed internally. |

`magic_mcp_get_abi_version` is the exception: it returns the ABI version directly rather than a status code.

After a failure, `magic_mcp_get_last_error` may provide human-readable UTF-8 diagnostic text for the most recent ABI call on the current calling thread. Diagnostics are not stable API and must not be parsed for control flow.

## Concurrency and ordering

A package must support concurrent calls involving different instance IDs. It must preserve send order for each individual instance and preserve the order of messages returned by that instance's receive stream.

Stopping and sending to the same instance may race. The package must resolve that race without memory corruption; either the send completes or it returns an appropriate stopped/not-found status.

The host must keep all input buffers valid for the duration of the native call. The package must not retain those pointers after the call returns.

## MCP behavior

After starting an instance, the host communicates using ordinary MCP JSON-RPC messages. Tool discovery, tool execution, prompts, resources, initialization, notifications, and errors are MCP concerns rather than new package-protocol methods.

For example, the host initializes the MCP session, then can send `tools/list` and `tools/call` requests. The package is responsible for producing standards-compliant MCP responses through `magic_mcp_receive`.

The MagicAiGateway package ABI transports MCP; it does not reinterpret tool schemas or invent an alternate tool protocol.

## Trust and security boundary

A loaded package is native code executing inside the host process. ABI v1 does not sandbox packages, authenticate ABI calls, inspect package internals, or impose an authorization system.

Only trusted package binaries should be loaded. Any authentication, authorization, secret handling, input validation, tenant isolation, container policy, or other security needed by a package is the package author's responsibility.

## Language implementations

Other languages do not need to copy the C# architecture. They only need to:

1. Produce a native shared library for the target platform.
2. Export the ABI v1 symbols with C linkage and `cdecl` calling convention.
3. Maintain independent state for each opaque instance ID.
4. Start and stop their own runtime, workers, and resources correctly.
5. Accept and emit complete MCP JSON-RPC messages through `send` and `receive`.
6. Never cross the ABI boundary with language-owned objects, allocators, exceptions, or panics.

The C# project at `MagicAiGateway.MCP.Package.Template` is an opinionated implementation for .NET developers. It uses NativeAOT, the .NET Generic Host, dependency injection, hosted background services, and the official `ModelContextProtocol` package while exporting this same language-neutral ABI.
