# Client SDK Testing

This guide covers the `MagicAiGateway.Client.Tests` xUnit project, including live tests against a running Magic AI Gateway and the deterministic unit/integration tests used during normal development and CI.

## Live tests

Live tests call a real running Magic AI Gateway instance.

### Prerequisites

For the complete live test suite, have the following running:

1. `MagicAiApi`
2. `MagicAiNode`
3. A configured backend such as llama.cpp or vLLM with the selected model available

The API must be reachable from the machine running the tests. The node and backend are required for model discovery and inference tests.

### Run the live tests

Change into the client test project directory:

```bash
cd MagicAiGateway.Client.Tests
```

Set the gateway endpoint and model:

```bash
export MAGIC_AI_GATEWAY_TEST_ENDPOINT="https://localhost:7443"
export MAGIC_AI_GATEWAY_TEST_MODEL="Qwen36-27B"
```

Enable tests that perform actual inference:

```bash
export MAGIC_AI_GATEWAY_TEST_RUN_INFERENCE="true"
```

Run only the live test category:

```bash
dotnet test MagicAiGateway.Client.Tests.csproj \
  --filter "Category=Live"
```

### What the live suite validates

The live suite can validate:

- Secure client connection and gateway identity negotiation
- Gateway model discovery through `/v1/models`
- Normal buffered chat-completion responses
- Streaming chat-completion responses
- SSE data chunks and the final `[DONE]` marker

Inference tests run only when `MAGIC_AI_GATEWAY_TEST_RUN_INFERENCE` is set to `true`. This prevents an ordinary test run from unexpectedly loading a model or consuming inference resources.

### Environment variables

| Variable | Required | Purpose |
| --- | --- | --- |
| `MAGIC_AI_GATEWAY_TEST_ENDPOINT` | Yes | Base URI of the running API, such as `https://localhost:7443` |
| `MAGIC_AI_GATEWAY_TEST_MODEL` | For model-specific inference tests | Model ID exposed by `/v1/models` |
| `MAGIC_AI_GATEWAY_TEST_RUN_INFERENCE` | For completion tests | Set to `true` to allow buffered and streaming inference tests |
| `MAGIC_AI_GATEWAY_TEST_API_KEY` | No | Bearer API key when the gateway requires one |
| `MAGIC_AI_GATEWAY_TEST_GATEWAY_NAME` | No | Expected gateway name; defaults to `MagicAiGateway` |
| `MAGIC_AI_GATEWAY_TEST_TIMEOUT_SECONDS` | No | Request timeout used by live tests |
| `MAGIC_AI_GATEWAY_TEST_TRUST_MODE` | No | Overrides the client certificate trust mode |

For the normal private certificate used by a local development gateway, the default live-test configuration supports application-owned local trust without requiring the cluster CA to be installed in the operating system trust store.

## Unit and deterministic integration tests

The client test project is divided into categories:

- `Unit` tests validate isolated protocol, configuration, discovery, trust-store, and request-validation behavior.
- `Integration` tests start a temporary local gateway and exercise real HTTP and streaming behavior without requiring the API, node, or model backend to be running.
- `Live` tests call an actual deployed gateway and are opt-in.

### Run all deterministic tests

From the repository root:

```bash
dotnet test MagicAiGateway.Client.Tests/MagicAiGateway.Client.Tests.csproj \
  --filter "Category!=Live"
```

Or from inside the test project directory:

```bash
dotnet test MagicAiGateway.Client.Tests.csproj \
  --filter "Category!=Live"
```

These tests are safe to run repeatedly and are the tests used by normal pull-request CI.

### Run only unit tests

```bash
dotnet test MagicAiGateway.Client.Tests.csproj \
  --filter "Category=Unit"
```

### Run only deterministic integration tests

```bash
dotnet test MagicAiGateway.Client.Tests.csproj \
  --filter "Category=Integration"
```

## Adding tests

Place new tests according to what they depend on:

```text
MagicAiGateway.Client.Tests/
├── Unit/           # No sockets, real gateway, or external services
├── Integration/    # Local test server or multiple SDK components together
├── Infrastructure/ # Shared fixtures, fake servers, and temporary resources
└── Live/           # Calls a real MagicAiApi deployment
```

Every test class should have the corresponding xUnit trait:

```csharp
[Trait("Category", "Unit")]
```

```csharp
[Trait("Category", "Integration")]
```

```csharp
[Trait("Category", "Live")]
```

Live tests should dynamically skip when their required environment variables are missing. Any test that performs model inference must also require the explicit inference opt-in so routine development and CI runs remain fast and predictable.
