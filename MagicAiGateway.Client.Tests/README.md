# MagicAiGateway.Client.Tests

This xUnit v3 project is the expandable validation harness for `MagicAiGateway.Client`.

## Layout

- `Unit/` contains deterministic validation of protocol helpers, discovery classification, trust persistence, and request safety.
- `Integration/` sends real HTTP requests through the SDK to an in-process Kestrel gateway, including buffered JSON and SSE streaming.
- `Infrastructure/` contains reusable test servers and temporary-state helpers.
- `Live/` contains opt-in calls against a running Magic AI Gateway.

Normal CI runs Unit and Integration tests. Live tests are excluded from CI because a GitHub runner cannot reach a developer's LAN gateway and should never consume inference accidentally.

## Run deterministic tests

```bash
dotnet test MagicAiGateway.Client.Tests/MagicAiGateway.Client.Tests.csproj \
  --configuration Release \
  --filter "Category!=Live"
```

Run one category:

```bash
dotnet test MagicAiGateway.Client.Tests/MagicAiGateway.Client.Tests.csproj \
  --filter "Category=Unit"

dotnet test MagicAiGateway.Client.Tests/MagicAiGateway.Client.Tests.csproj \
  --filter "Category=Integration"
```

## Run live gateway tests

The only required setting for connection and model-list tests is the gateway endpoint:

```bash
export MAGIC_AI_GATEWAY_TEST_ENDPOINT="https://localhost:7443"

dotnet test MagicAiGateway.Client.Tests/MagicAiGateway.Client.Tests.csproj \
  --filter "Category=Live"
```

The default trust mode is `local-tofu`: public certificates use normal operating-system trust, while localhost, `.local`, and private LAN IPs may bootstrap the gateway's private cluster CA and then validate its GUID identity.

### Enable inference tests

Buffered and streaming chat-completion tests are skipped unless inference is explicitly enabled:

```bash
export MAGIC_AI_GATEWAY_TEST_ENDPOINT="https://localhost:7443"
export MAGIC_AI_GATEWAY_TEST_MODEL="Qwen36-27B"
export MAGIC_AI_GATEWAY_TEST_RUN_INFERENCE="true"

dotnet test MagicAiGateway.Client.Tests/MagicAiGateway.Client.Tests.csproj \
  --filter "Category=Live"
```

### Optional settings

| Environment variable | Purpose | Default |
|---|---|---|
| `MAGIC_AI_GATEWAY_TEST_EXPECTED_NAME` | Expected gateway friendly name | `MagicAiGateway` |
| `MAGIC_AI_GATEWAY_TEST_API_KEY` | Bearer token for the public API | unset |
| `MAGIC_AI_GATEWAY_TEST_MODEL` | Model used and optionally validated | unset |
| `MAGIC_AI_GATEWAY_TEST_RUN_INFERENCE` | Allows chat completion tests | `false` |
| `MAGIC_AI_GATEWAY_TEST_TIMEOUT_SECONDS` | Per-operation timeout | `120` |
| `MAGIC_AI_GATEWAY_TEST_TRUST_MODE` | `system`, `local-tofu`, `tofu`, or `insecure-development` | `local-tofu` |

Use `system` for a normal publicly trusted domain. Use `tofu` only when an explicitly configured non-local private endpoint should be trusted on first use. `insecure-development` remains limited by the SDK's development safeguards and should not be used for production validation.

## Adding tests

Add pure behavior checks under `Unit/`. Add tests that need real HTTP framing, headers, or streaming under `Integration/` and extend `LoopbackGatewayServer` with the smallest required endpoint behavior. Add tests that validate the deployed gateway under `Live/`, and gate anything that performs inference behind `MAGIC_AI_GATEWAY_TEST_RUN_INFERENCE`.
