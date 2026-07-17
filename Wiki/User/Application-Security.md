# Application security

Application API-key enforcement begins disabled so a new installation can be assembled and initialized. Protected endpoints behave anonymously while the security activation latch is off.

## Activation is global

Core credentials are initialized as one transaction for PrimaryApi, Web, and MCP. The returned secrets are displayed only in that response and must be saved securely, then placed in each application's protected configuration.

Once the first credential set activates application security:

- every endpoint with an application or role requirement requires a valid API key;
- every participating application must be configured with its own key;
- removing or revoking all keys does **not** turn security off;
- a system with security enabled and no valid key fails closed and requires administrator recovery.

This is the “one means all” rule: do not enable application credentials for only one major component and leave the others unconfigured.

## What is stored

The database stores a non-secret key prefix and a peppered HMAC-SHA256 hash. The complete API key is never stored. Key records may have a name, description, applications, roles, creation time, optional expiration, last-used time, and revocation time.

Use separate keys per application even though the schema supports multiple application assignments. Separate keys make rotation, revocation, and auditing safer.

## HTTP identity

DB.Client automatically sends:

```http
Authorization: Bearer <application-api-key>
X-Magic-Application: Web
```

The application header is a claim, not proof. DB.API verifies that the hashed key record is actually assigned to the claimed application and that the endpoint permits it.
