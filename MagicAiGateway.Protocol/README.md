# MagicAiGateway.Protocol

`MagicAiGateway.Protocol` owns the HTTP wire contracts shared by the gateway, SDK, and future non-.NET clients.

The package models:

- OpenAI-compatible chat-completions requests, messages, responses, chunks, tools, and usage;
- the root-level `magic_ai_gateway` service invocation envelope;
- service discovery descriptors and generated option schemas;
- Magic run lifecycle, aggregate usage, reasoning, and streaming events.

`MagicChatMessage` is one item inside `MagicChatCompletionRequest.Messages`. It is not the complete HTTP request. The Magic envelope remains a root request property because it controls the lifecycle of the whole request.

The contracts do not inherit from `Microsoft.Extensions.AI` or another provider SDK. Optional adapters may map those abstractions into these owned wire models without making an external package part of the protocol's public type hierarchy.
