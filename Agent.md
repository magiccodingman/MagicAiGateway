# Agent architecture and security notes

This file records invariants future coding agents must preserve while extending Magic AI Gateway.

## System shape

`MagicAiApi` is the client-facing OpenAI-compatible gateway. `MagicAiNode` runs next to one or more vLLM/llama.cpp instances. `MagicAiGateway.Client` is the public, NuGet-ready SDK for gateway protocol contracts, endpoint discovery, trust, connection resolution, and client transport. `SharedMagic` contains server-side/provider-neutral contracts, request inspection, scheduling, fabric advertisement, and fabric security primitives.

There are two routing layers:

1. `MagicAiApi`: model name -> healthy nodes.
2. `MagicAiNode`: model name -> healthy local backend instances.

Both layers use `IRequestScheduler<TTarget>` and destination leases. Leases must be disposed on success, cancellation, client disconnect, and failure so active-request accounting and queue permits cannot leak.

## Client SDK boundary

`MagicAiGateway.Client` must remain usable without referencing `MagicAiApi`, `MagicAiNode`, `SharedMagic`, ASP.NET Core, YARP, SignalR, or server hosting infrastructure. Server projects may reference the client package for public protocol and discovery primitives; the client package must never reference a server project.

The client facade stays small. Connection resolution, raw transport, and future high-level APIs belong behind focused interfaces rather than accumulating methods on one god object. Preserve both dependency-injection registration and a non-DI factory so console, desktop, service, and future interop hosts use the same implementation.

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
- object: halt normal forwarding and dispatch to the gateway protocol pipeline.

Until protocol handlers exist, the object case returns an OpenAI-shaped `501`. The reserved object must never leak to vLLM or llama.cpp.

Gateway-owned OpenAI tokenization on `/tokenize` is also recognized and currently returns `501`. Provider-native tokenization remains pass-through.

## Response interception and tool calls

`ToolCallObservingStream` is intentionally transparent. It observes copied bytes while immediately forwarding the original bytes. It reconstructs fragmented Chat Completions tool calls and Responses API function-call argument deltas without JSON reserialization.

Current invariant:

- detect tool calls;
- query `IMagicToolRegistry` ownership;
- execute nothing;
- suppress nothing;
- return the provider response unchanged.

Unknown tools are client-owned. Future gateway tool execution must never hijack an unknown/client tool.

When internal execution is added, use an explicit gateway-protocol opt-in for interceptable streaming. A gateway-owned call cannot be forwarded to the client and then silently consumed. Mixed gateway/client tool-call turns should default to returning the whole turn to the client unless a protocol version defines safe mixed ownership.

Tool arguments can contain credentials or private data. Normal logs may include only tool name, call ID, argument byte count, and ownership result—not raw arguments.

## Proxy and retry rules

YARP `IHttpForwarder` is used after the custom scheduler selects a destination. Reuse `HttpMessageInvoker` instances; do not create one per request.

Do not transparently retry once response bytes have reached the client. Never retry a streamed generation after partial output. A retry could duplicate compute and produce a different continuation.

## Backends

Provider-specific behavior belongs behind `IAiBackendAdapter`. Do not scatter vLLM/llama.cpp conditionals across controllers or routing code.

Model IDs are opaque and case-sensitive. Do not lowercase or normalize them. If aliases are added, make them explicit configuration.

A backend marked offline is polled more frequently than a healthy backend. Inventory refresh occurs with every health probe so model changes propagate without restarting the node.

## Tokenizers

Tokenizer information is capability-based. Do not assume every provider returns a full portable tokenizer artifact. Preserve normalized metadata, raw provider data, chat template, model path when available, and whether remote tokenization is supported.

Tokenizer and model filesystem paths are fabric-internal information and must not appear in anonymous health endpoints.

## State

Gateway state includes identity, CA, gateway certificate, pending/approved peers, and secrets. Node state includes identity, private key, issued certificate, cluster root, and pinned gateway identity. State files must stay ignored by Git and should be user-only on Unix where possible.

Removing state changes identity. Code that rotates certificates must preserve identity and trust records unless an explicit reset was requested.
