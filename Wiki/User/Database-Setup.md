# Database setup

MagicAiGateway.DB.API is the only component that talks directly to PostgreSQL. The primary API, Web, MCP, and other applications use MagicAiGateway.DB.Client and call DB.API over HTTP.

## Preferred: externally managed PostgreSQL

Set `Database:AutoDeploy:Enabled` to `false` and provide a PostgreSQL connection through configuration, environment variables, user secrets, or another protected provider. Do not commit passwords.

When `Database:Connection:Database` is supplied, DB.API assumes the database already exists and that the configured account may create and alter objects inside it. Startup fails if those assumptions are false.

When the database name is omitted, DB.API uses `magic_ai_gateway`. It connects through the configured administrative database and creates the target database when necessary. Schema changes are then applied through EF Core migrations.

## Optional Docker deployment

Set `Database:AutoDeploy:Enabled` to `true`. Docker must be available to the DB.API process or startup fails. The managed container:

- uses the configured PostgreSQL image;
- is labelled as MagicAiGateway-owned;
- stores data in a named persistent volume;
- is reused after restarts;
- may be stopped on graceful shutdown;
- never deletes the volume automatically.

DB.API refuses to take over an existing container with the configured name unless its ownership labels match.

Docker socket access is highly privileged. Only grant it when automatic deployment is actually required. An externally managed PostgreSQL installation with backups remains the recommended production setup.

## Readiness

`/health/live` reports that the process is running. `/health/ready` reports ready only after PostgreSQL is reachable, the physical database exists, migrations have completed, and security seed data has been initialized.
