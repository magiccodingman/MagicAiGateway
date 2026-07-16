# Agent architecture and security notes

This file records invariants future coding agents must preserve while extending Magic AI Gateway.

## System shape

`MagicAiApi` is the client-facing OpenAI-compatible gateway. `MagicAiNode` runs next to one or more vLLM/llama.cpp instances. `MagicAiGateway.Protocol` owns the shared HTTP wire contracts. `MagicAiGateway.Client` is the public, NuGet-ready SDK for endpoint discovery, trust, connection resolution, raw transport, typed chat, and Magic protocol helpers. `SharedMagic` contains server-side/provider-neutral inspection, scheduling, execution-runtime, fabric advertisement, and security primitives.

There are two routing layers:

1. `MagicAiApi`: model name -> healthy nodes.
2. `MagicAiNode`: model name -> healthy local backend instances.

Both layers use `IRequestScheduler<TTarget>` and destination leases. Leases must be disposed on success, cancellation, client disconnect, and failure so active-request accounting and queue permits cannot leak.

## Protocol contract boundary

`MagicAiGateway.Protocol` owns all canonical JSON property names, OpenAI-compatible request/response contracts, Magic service invocation contracts, service descriptors, run metadata, usage, and streaming event types. Do not duplicate protocol literals such as `magic_ai_gateway` in other projects.

`MagicChatCompletionRequest` is the complete `/v1/chat/completions` request. `MagicChatMessage` is one item inside its `messages` array. The root `magic_ai_gateway` envelope controls the lifecycle of the whole request and must never be placed on an individual message.

Protocol types must not inherit from `Microsoft.Extensions.AI`, an OpenAI SDK, or another provider package. Optional adapters may translate those abstractions into owned protocol types, but external packages must not become part of the public wire-contract hierarchy.

Typed protocol models preserve unknown provider fields through extension data. Ordinary passthrough must continue preserving original provider bytes. Fully deserialize/reconstruct a request or response only when a selected Magic service deliberately owns that lifecycle.

## Client SDK boundary

`MagicAiGateway.Client` must remain usable without referencing `MagicAiApi`, `MagicAiNode`, `SharedMagic`, ASP.NET Core, YARP, SignalR, or server hosting infrastructure. It may reference `MagicAiGateway.Protocol`. The client package must never reference a server project.

The client facade stays small. Connection resolution, raw transport, typed chat, and protocol discovery belong behind focused interfaces rather than accumulating methods on one god object. Preserve both dependency-injection registration and a non-DI factory so console, desktop, service, and future interop hosts use the same implementation.

Raw client requests use relative gateway paths. Do not turn the SDK into a generic arbitrary-URL HTTP client. Buffered and streaming calls must preserve provider extension fields and response bytes without reconstructing OpenAI payloads.

Client discovery order is configurable, with local-first as the default: loopback, last-known trusted endpoint, mDNS/DNS-SD, then configured remote fallback. An explicit endpoint override disables discovery. The SDK does not run node-style heartbeat or SignalR background services; it resolves lazily or through an explicit `ConnectAsync` call and re-resolves after connection failure.

Public gateway certificates use normal platform trust. Private local gateway trust is application-owned and portable: an allowed first-use bootstrap is followed by validation against the cluster root and gateway GUID identity, then persisted trust binds gateway name, gateway ID, cluster ID, and root CA. Never replace this with a permanent accept-any-certificate callback. First-use trust is weaker and must remain limited/configurable and clearly documented.

## Client security spine

Client/data-plane authentication and node/fabric authentication are separate security domains. Client API keys, OAuth tokens, or user identities must never satisfy `FabricAuthenticationDefaults.Policy`; node certificates must never automatically grant client inference or administrative access.

Client-facing routes use named `GatewayPolicies` rather than hard-coded `AllowAnonymous`. The default `GatewayAccess:Mode` is `Anonymous`, so this policy layer preserves zero-configuration homelab behavior. Future access-control work should change authentication schemes, authorization handlers, claims, and policy requirements centrally rather than revisiting every endpoint.

Every proxied request is classified into a stable `GatewayOperation` and authorized before scheduler acquisition. Model restrictions, scopes, quotas, tool permissions, and audit context belong in resource authorization around `GatewayAuthorizationResource`; they do not belong in schedulers, backend adapters, or provider-specific code.

All authenticated client schemes must produce a client security-domain claim using `GatewayClientAuthenticationDefaults.SecurityDomainClaim`. The client policies deliberately require this domain so a fabric principal cannot be mistaken for an application/user principal.

The SDK obtains credentials through `IGatewayCredentialProvider` for each ordinary client request. `MagicAiGatewayClientOptions.ApiKey` is only a convenience wrapper around `StaticApiKeyCredentialProvider`. External login systems, expiring OAuth tokens, and service credentials should plug into the provider abstraction instead of modifying raw transport or higher-level SDK APIs.

Do not attach client credentials to mDNS discovery or use them as gateway identity. TLS trust answers which gateway is being contacted; client credentials answer who is allowed to use it.

## Magic service runtime

A client selects exactly one public Magic service by name and version. The client does not define internal execution steps, priorities, or dependencies. The selected service and trusted server contributors compile a `MagicExecutionPlan`.

Plans execute by `MagicExecutionPhase`, then lower numeric priority. Steps at the same phase and priority may run concurrently only when their declared `MagicStepAccess` read/write resources do not conflict. Two transcript writers, a transcript reader beside a writer, or two client-response writers in the same concurrent group must fail plan validation before execution.

Every Magic request receives one `GatewayRunContext`. Transcript, bounded content/reasoning journal, aggregate usage, application/agent context, service state, cancellation, deadlines, and final output live there. Feature services must use these shared abstractions rather than implementing their own message storage, streaming writer, timeout loop, or active-request dictionary.

`IGatewayRunManager` owns active-run registration and cleanup. Request disconnect, server shutdown, run deadline, and future administrative cancellation must flow through the linked run cancellation token. Every run lease must be disposed in `finally`/`await using` so state cannot grow forever.

A public service may contribute multiple internal steps, and global/application contributors may add audit or metrics steps. Public service discovery exposes descriptors and option schemas, not implementation class names.

## Magic timeout rules

Ordinary typed SDK calls default to three minutes. Magic-managed SDK transport is infinite by default because the server owns the logical service deadline; caller cancellation remains active.

A service descriptor defines its default and maximum total run durations. A caller may request a duration in the Magic envelope, but the server validates and clamps it to the service maximum. A requested timeout is for the entire logical run, not separately for every model or tool call.

Services still require non-time limits such as maximum continuation rounds, total tool calls, transcript size, and captured journal size. Do not treat a long deadline as permission for unbounded memory or loops.

## Magic response and streaming rules

Magic mode changes logical completion, not the underlying OpenAI-compatible transport closure. A successful Magic stream reports completed Magic run metadata and still emits a final OpenAI-shaped finish/usage chunk followed by `[DONE]`.

The gateway may perform several internal model/tool/model turns, but the caller receives one logical completion and whole-run aggregate usage. Internal tool-call turns that the gateway intends to consume must not be forwarded as ordinary final assistant output.

Compatibility streaming emits only OpenAI-shaped data chunks plus root Magic metadata in the final chunk. Enriched streaming may additionally emit named Magic reasoning/tool/run events. Generic OpenAI clients must remain usable in compatibility mode.

Conversation transcript and run journal are distinct. Only transcript messages are re-fed to the model. Normalized reasoning, progress events, timings, usage, and provider metadata belong in the journal unless an explicit adapter/policy says otherwise.

## Managed tools service

`managed_tools` is the first public service and currently provides the lifecycle, schema, authorization, plan, timeout, and MCP/model extension seams. Until the real continuation loop is installed, it must fail with a structured `service_not_ready` result rather than pretending tools executed.

The real implementation must use `IGatewayModelInvoker`, `IToolCatalogProvider`, `IToolCallExecutor`, and `IManagedToolRunService`. The orchestration loop should understand canonical tool definitions/calls/results, not MCP JSON-RPC transport details.

Continuation requests append the assistant tool-call message and tool result messages to `GatewayConversationTranscript`, then create a new OpenAI-compatible request with the root Magic envelope removed before it reaches vLLM or llama.cpp.

## Trust model

Discovery, identity, and trust are separate concerns.

- mDNS/DNS-SD only discovers candidate addresses.
- Friendly names select candidates; they are not identities.
- Persistent GUIDs and cluster-issued certificates are identities.
- IP addresses and ports are mutable routing data and must never become peer identity.
- The gateway creates and retains the cluster CA private key. It must never be sent to a node.
- A node generates its private key locally and sends a PKCS#10 CSR. The gateway returns only the signed node certificate and public root certificate.
- A locally generated gateway certificate makes first gateway discovery TOFU-like. Gateway approval controls whether a node may join, but does not by itself authenticate the candidate gateway to the node. Use an enrollment secret, an explicitly trusted static endpoint, or out-of-band fingerprint verification when the LAN is not trusted. After pairing, the node pins the gateway ID and cluster CA.

Do not add secrets, tokens, private keys, model paths, or detailed inventory to mDNS TXT records or anonymous health responses.

## Pairing modes

`ApprovalRequired` is the LAN default. `EnrollmentToken` supports unattended deployment through a short-lived HMAC challenge and a response proof bound to the issued certificate and cluster root; never regress this to sending the raw enrollment token or accepting an unbound pairing response. `AutomaticTrustOnFirstUse` is explicitly weaker and must remain clearly named/documented. Loopback pairing may be automatic.

Pairing administration is local-only unless `AdminToken` is configured. Do not weaken this check or make pairing approval broadly anonymous.

## Fabric authorization

Controllers and endpoints intended only for gateway/node communication must use `MagicFabricAuthorizeAttribute` or require `FabricAuthenticationDefaults.Policy`.

The fabric authentication handler accepts:

- a certificate chaining to the configured cluster CA whose GUID identity is approved; or
- an explicitly enabled loopback token on a genuine loopback socket.

Never trust forwarded headers to decide that a connection is loopback. Use `HttpContext.Connection.RemoteIpAddress`.

Anonymous `/health/live` and `/health/ready` routes must remain minimal. Do not leak node lists, model paths, backend versions, tool arguments, or GPU details there.

## Endpoint validation and SSRF

A node advertises its current URI, but the gateway must treat that value as untrusted routing input. Non-loopback fabric URIs must use HTTPS. Future hardening should additionally enforce configured address ranges/domain allowlists. Every current gateway-to-node TLS connection must verify that the certificate identity matches the registered node ID; do not replace this with CA-only validation.

Never create a generic authenticated endpoint that fetches an arbitrary URL supplied by a caller.

## Control and data planes

SignalR is the control plane. Nodes initiate the connection and send heartbeats/model inventory. A heartbeat lease is authoritative for online/offline routing; SignalR connection state is an immediate signal but not the sole health check.

The OpenAI proxy is the data plane. It must preserve streaming and provider extension fields. Do not deserialize requests into a rigid OpenAI DTO and reconstruct them unless a specific interceptor deliberately owns the request.

## Request interception

The root-level `magic_ai_gateway` property is reserved:

- absent: ordinary passthrough;
- null: remove the reserved property, then ordinary passthrough;
- object: halt normal forwarding and dispatch to the Magic protocol host.

The reserved object must never leak to vLLM or llama.cpp. Gateway-owned OpenAI tokenization on `/tokenize` is recognized but not implemented; provider-native tokenization remains pass-through.

## Response interception and passive tool observation

`ToolCallObservingStream` is intentionally transparent and belongs only to ordinary passthrough. It observes copied bytes while immediately forwarding the original bytes. It reconstructs fragmented Chat Completions tool calls and Responses API function-call argument deltas without JSON reserialization.

Passive observation must execute nothing, suppress nothing, and return provider bytes unchanged. Managed tool continuation uses the separate Magic service path because internal tool-call turns cannot be sent to the client before the gateway decides to consume them.

Tool arguments can contain credentials or private data. Normal logs may include only tool name, call ID, argument byte count, and ownership result—not raw arguments.

## Proxy and retry rules

YARP `IHttpForwarder` is used after the custom scheduler selects a destination. Reuse `HttpMessageInvoker` instances; do not create one per request.

Do not transparently retry once response bytes have reached the client. Never retry a streamed generation after partial output. A retry could duplicate compute and produce a different continuation.

## Backends

Provider-specific behavior belongs behind `IAiBackendAdapter` or a focused model protocol adapter. Do not scatter vLLM/llama.cpp/Qwen conditionals across controllers, service planning, or routing code.

Model IDs are opaque and case-sensitive. Do not lowercase or normalize them. If aliases are added, make them explicit configuration.

A backend marked offline is polled more frequently than a healthy backend. Inventory refresh occurs with every health probe so model changes propagate without restarting the node.

## Tokenizers

Tokenizer information is capability-based. Do not assume every provider returns a full portable tokenizer artifact. Preserve normalized metadata, raw provider data, chat template, model path when available, and whether remote tokenization is supported.

Tokenizer and model filesystem paths are fabric-internal information and must not appear in anonymous health endpoints.

## State

Gateway state includes identity, CA, gateway certificate, pending/approved peers, and secrets. Node state includes identity, private key, issued certificate, cluster root, and pinned gateway identity. State files must stay ignored by Git and should be user-only on Unix where possible.

Removing state changes identity. Code that rotates certificates must preserve identity and trust records unless an explicit reset was requested.
