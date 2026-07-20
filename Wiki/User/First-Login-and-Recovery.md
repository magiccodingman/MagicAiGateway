# First login and administrator recovery

## Initial administrator

On the first successful database startup, DB.API creates the configured administrator only when no matching user exists. The defaults are:

- username: `admin`
- bootstrap password: `put_your_password_here`

Override the password through protected configuration before first startup whenever possible. DB.API hashes it with ASP.NET Core's password hasher before storing it. The configuration value is never copied back out of the database and never overwrites an existing administrator on later starts.

The administrator is created with `MustChangePassword=true`. The first Web setup flow must permit only bootstrap status, login, and password replacement until the password is changed. The backend reports `PasswordChangeRequired`; future Web session handling must treat that as a restricted setup identity rather than normal administrator access.

After password replacement, DB.API rotates the user's security stamp, clears the required-change flag, and increments the security revision.

## Lost administrator password

Recovery is disabled by default. To enable it temporarily:

1. Set `Security:AdminRecovery:Enabled=true`.
2. Set a strong one-time value in `Security:AdminRecovery:OneTimeToken`.
3. Ensure `Security:AdminRecovery:ListenUrl` remains a loopback URL. The default is `http://127.0.0.1:7764`.
4. Restart DB.API.
5. Send one request to the recovery listener with `X-Magic-Recovery-Token` and a temporary password of at least 12 characters.
6. Disable recovery and remove the token before restarting again.
7. Log in and replace the temporary password immediately.

Example from the DB.API host or container namespace:

```bash
curl -X POST http://127.0.0.1:7764/recovery/v1/admin/password \
  -H 'Content-Type: application/json' \
  -H 'X-Magic-Recovery-Token: REPLACE_ME' \
  -d '{"newTemporaryPassword":"REPLACE_WITH_A_STRONG_TEMPORARY_PASSWORD"}'
```

The endpoint requires a genuine loopback socket, the dedicated recovery port, the configured token, and an unused process-local recovery gate. A successful reset always sets `MustChangePassword=true`, rotates the security stamp, and invalidates the old password. The token and passwords must not appear in logs.
