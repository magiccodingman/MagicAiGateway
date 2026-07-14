# Magic AI Gateway

Magic AI Gateway presents one OpenAI-compatible endpoint while routing requests through one or more `MagicAiNode` services to local vLLM and llama.cpp instances.

```text
OpenAI client -> MagicAiApi -> MagicAiNode -> vLLM / llama.cpp
```

The gateway aggregates model availability across nodes. Each node independently health-checks and load-balances its configured local inference servers.

## Requirements

- .NET 10 SDK/runtime
- UDP 5353 allowed on the local network when mDNS discovery is enabled
- The configured HTTPS port allowed between the gateway and nodes
- A running vLLM or llama.cpp OpenAI-compatible server for each enabled node backend

No operating-system certificate installation is required for local fabric communication. The gateway creates a private cluster certificate authority in its state directory, and paired nodes receive cluster-issued certificates automatically.

## Initial gateway setup

Edit `MagicAiApi/appsettings.json` or override values with environment variables. The default listeners are:

- HTTP: `7080`
- HTTPS: `7443`
- mDNS service: `_magicaigw._tcp.local`
- Gateway name: `MagicAiGateway`

Start the gateway:

```bash
dotnet run --project MagicAiApi
```

Runtime identity, certificates, approved peers, and pairing requests are written beneath `Fabric:Security:StateDirectory`. Back up this directory and do not commit it.

A gateway can also poll a manually configured, already-paired node when multicast or SignalR is unavailable. Static entries include the immutable node ID so TLS can be pinned correctly:

```json
"StaticNodes": [
  {
    "NodeId": "00000000-0000-0000-0000-000000000000",
    "Name": "remote-gpu-node",
    "BaseUri": "https://node.example.com:8443",
    "Enabled": true,
    "PollSeconds": 10
  }
]
```

Replace the example ID with the node identity shown by its state or gateway pairing record.

### Pairing modes

`Fabric:Security:PairingMode` supports:

- `ApprovalRequired` — recommended default for LAN deployments. A discovered node remains pending until approved. This controls node admission; locally generated gateway certificates still make first gateway discovery a trust-on-first-use decision.
- `EnrollmentToken` — unattended deployments provide the same `EnrollmentToken` to the gateway and node. Pairing uses a short-lived challenge plus a response proof bound to the issued certificate and cluster root; the token itself is not transmitted.
- `AutomaticTrustOnFirstUse` — fully automatic first contact. Convenient for trusted home-lab networks, but any attacker present during first pairing could impersonate the gateway.

`PairingServerCertificateMode` defaults to `TrustOnFirstUse` for automatically generated LAN certificates. Set it to `SystemTrust` when a static internet gateway endpoint presents a publicly or privately trusted TLS certificate.

Loopback pairing is automatically accepted because both processes are on the same machine.

View and approve pending nodes from the gateway machine:

```bash
curl -k https://localhost:7443/fabric/v1/pairing
curl -k -X POST https://localhost:7443/fabric/v1/pairing/<NODE-ID>/approve
```

For remote administration, configure `Fabric:Security:AdminToken` and send it in `X-Magic-Admin-Token`. Do not expose pairing administration publicly without additional edge authentication.

## Initial node setup

Configure `MagicAiNode/appsettings.json`:

```json
{
  "Node": {
    "Name": "gpu-node-1",
    "GatewayName": "MagicAiGateway",
    "AdvertisedBaseUri": "https://10.26.26.20:7553",
    "StaticGateways": [],
    "Backends": [
      {
        "Id": "vllm-qwen",
        "Name": "Qwen vLLM",
        "Kind": "Vllm",
        "BaseUri": "http://localhost:8000",
        "Enabled": true
      },
      {
        "Id": "llama-router",
        "Name": "llama.cpp router",
        "Kind": "LlamaCpp",
        "BaseUri": "http://localhost:8080",
        "Enabled": false
      }
    ]
  }
}
```

`AdvertisedBaseUri` must be reachable by `MagicAiApi`. Use HTTPS outside loopback. Multiple nodes on one machine must use different listener ports and advertised URIs.

Start the node:

```bash
dotnet run --project MagicAiNode
```

With discovery enabled, the node searches for `_magicaigw._tcp.local` instances whose name starts with `GatewayName`. `StaticGateways` can contain complete gateway URLs for different subnets, VLANs, public addresses, custom ports, or environments where multicast is disabled:

```json
"StaticGateways": [
  "https://gateway.example.com",
  "https://10.26.26.10:7443"
]
```

When several gateways use the same friendly name, retain the node state directory after the first approved pairing. The persistent gateway ID and cluster CA, rather than the friendly name or IP address, become the trusted identity.

## Internet deployment

mDNS is local-link discovery and is not an internet discovery mechanism. For internet-connected nodes:

1. Configure a static HTTPS gateway URL on the node.
2. Configure a publicly reachable HTTPS node URL, or place it behind a secure reverse proxy/VPN.
3. Use a publicly trusted certificate at the edge when clients or proxies require it.
4. Restrict fabric routes by firewall and retain Magic AI Gateway mutual authentication.

Do not expose backend vLLM or llama.cpp ports directly to the internet.

## vLLM and llama.cpp backends

The node polls healthy backends every 10 seconds and offline backends every 3 seconds by default. Every poll refreshes `/v1/models`. When a backend returns, its model inventory is immediately restored to the node heartbeat.

For vLLM tokenizer metadata, launch vLLM with its tokenizer-info endpoint enabled. The exact vLLM switch can change between releases; verify it against the vLLM version being deployed. The endpoint can expose chat templates and model paths, so leave it bound behind `MagicAiNode` rather than publishing it anonymously.

llama.cpp metadata is obtained from `/props`, and remote tokenization uses `/tokenize`. A llama.cpp server may provide provider-side tokenization without exposing a complete portable Hugging Face tokenizer bundle; callers should inspect the returned capability fields.

## Client use

Point an OpenAI-compatible client at `MagicAiApi`:

```bash
curl -k https://localhost:7443/v1/models

curl -k https://localhost:7443/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "your-served-model-name",
    "messages": [{"role":"user","content":"Hello"}],
    "stream": true
  }'
```

The request and response bodies are streamed without OpenAI DTO reserialization. Unknown provider fields remain intact.

### Gateway protocol placeholder

A non-null root-level `magic_ai_gateway` object is recognized and dispatched through the protocol-handler pipeline. With no handlers installed it returns an OpenAI-shaped `501 not_implemented` response. A null value means ordinary passthrough and is removed before the provider sees the request.

An OpenAI-shaped request sent to `/tokenize` is also recognized and currently returns `501`. Native provider `/tokenize` traffic remains pass-through when a model is supplied in the body, `model` query parameter, or `X-Magic-Model` header.

### Tool calls

Chat Completions and Responses API tool-call output is observed while bytes continue streaming to the client. Fragmented streaming tool-call names and arguments are reconstructed internally. The initial tool registry is empty, no tool executes, and all tool calls remain client-owned behavior.

## Useful endpoints

Gateway:

- `GET /health/live`
- `GET /health/ready`
- `GET /v1/models`
Fabric-internal routes require an authenticated cluster connection:

- Gateway: `GET /internal/v1/nodes`
- Gateway: `GET /internal/v1/tokenizers/{model}`
- Node: `GET /internal/v1/status`
- Node: `GET /internal/v1/models`
- Node: `GET /internal/v1/tokenizers/{model}`
- Node: `POST /internal/v1/tokenize`

## Resetting pairing

Stopping the process and removing its configured state directory resets its identity. Removing gateway state creates a new cluster CA and requires every node to pair again. Removing only a node state directory creates a new node identity requiring new approval.
