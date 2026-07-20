# Database client integration

Register `MagicAiGateway.DB.Client` with a stable application identity and a protected API-key source. DB.Client adds both values to every direct DB.API call.

Ordinary applications use `GatewayDirectoryDatabaseApiEndpointResolver`: they connect to the expected primary gateway and request `/v1/fabric/services/database`. The descriptor contains leased endpoints, DB.API peer identity, and the fabric root needed for pinned private TLS.

The primary API uses `LocalGatewayDatabaseApiEndpointResolver`. It reads the same descriptor from the in-process service registry instead of calling its own controller. All remaining DB.Client behavior—TLS, credentials, serialization, refresh, invalidation, raw transport, and typed security calls—stays identical.

An explicit endpoint override has highest precedence. Non-loopback HTTP endpoints are rejected. Private fabric endpoints validate both the cluster root and expected peer GUID; public certificates may use normal system trust.

Endpoint descriptors are cached only until their refresh boundary or lease expiration. Network failures invalidate the connection so the next request resolves again. Streaming responses are never retried after bytes begin.
