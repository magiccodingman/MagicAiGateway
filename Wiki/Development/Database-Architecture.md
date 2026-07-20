# Database architecture

## Project boundaries

- `MagicAiGateway.DB.Contracts`: public DTOs, enums, fabric service contracts, and authorization metadata. No EF Core or server dependency.
- `MagicAiGateway.DB`: persistence entities, DbContext, entity configuration, and migrations. Only DB.API references it.
- `MagicAiGateway.DB.API`: PostgreSQL lifecycle, Docker provisioning, application services, local authorization, controllers, and fabric registration.
- `MagicAiGateway.DB.Client`: service-directory resolution, pinned TLS, application credentials, raw transport, typed clients, and the remote authorization evaluator.

No project other than DB.API may open PostgreSQL directly. Controllers must expose domain operations rather than generic table access. Database reads and writes use EF Core LINQ; do not introduce scattered ADO.NET or handwritten SQL.

## Security principals

Users and API keys share `SecurityPrincipals`. Roles attach through `PrincipalRoles`, which permits future principal types without creating one role table per subject. API keys attach to one or more stable `MagicApplication` values through `ApiKeyApplications`.

`BuiltInRole` currently contains only `Administrator`. Role implication is implemented by `BuiltInRoleHierarchy`, never by comparing enum numbers. This preserves non-linear role expansion later.

## Security activation

`SecurityConfiguration` is a singleton record containing the application-security latch and monotonically increasing revision. Never infer whether security is enabled from the current number of API keys. Once activated, loss of all keys must fail closed.

Increment the revision whenever a password, key, role assignment, application assignment, disabled state, or security mode changes. Consumers may use it to invalidate authorization caches later.

## PostgreSQL lifecycle

A configured database name means “already created.” An omitted name permits provider-driven physical database creation. Runtime schema application uses `MigrateAsync`; never combine migrations with `EnsureCreated`.

Docker provisioning is isolated behind `IPostgresProvisioner`. Only labelled, owned containers may be restarted or stopped. Volumes are persistent and never automatically removed.
