# Application authorization protocol

Use `[RequireMagicApplication(...)]` and `[RequireMagicRole(...)]` on controllers or actions. Multiple applications or roles in one attribute are OR sets; application and role requirements are combined with AND.

When the activation latch is off, attributed endpoints allow anonymous compatibility. When it is on:

- missing or invalid credentials return 401;
- a valid key assigned to the wrong application returns 403;
- a valid application lacking an allowed role returns 403.

DB.API evaluates its own requests locally through EF Core. Other services use `IApplicationAuthorizationEvaluator` from DB.Client.

## Candidate-key evaluation

The primary API must authenticate to DB.API with its own PrimaryApi service key. When it evaluates an incoming Web or MCP request, the candidate key travels in the JSON body, not in a second authorization header:

```http
POST /v1/security/application-authorizations/evaluate
Authorization: Bearer <primary-api-key>
X-Magic-Application: PrimaryApi
```

```json
{
  "candidateApiKey": "<incoming-key>",
  "claimedApplication": "Mcp",
  "allowedApplications": ["Mcp", "Web"],
  "requiredRoles": []
}
```

Never place candidate keys in query strings, URLs, logs, telemetry dimensions, or exception messages.

## Fabric is separate

DB.API registration and heartbeat use the cluster certificate and the `database-api` fabric role. Application API keys must never satisfy fabric policies, and a paired database peer must never satisfy node-only policies. TLS establishes which service is being contacted; application authorization establishes which caller may use it.
