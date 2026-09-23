# Initial Analyst provisioning

Phase one permits exactly one portal-managed Analyst. Provision it only from an administrator-controlled,
interactive console on the deployed host after the `AddAnalystAccess` migration is applied.

```powershell
dotnet run --project MCAROC.Portal/MCAROC_Analysis -- --provision-analyst
```

The command prompts for the login name, display name, and password; it never accepts a password as an
argument, does not echo it, and writes only the framework password hash to the database. It refuses to run
with redirected input/output and refuses to create a second Analyst. Do not place identities or passwords in
appsettings, user-secrets exports, source, issue comments, or shell history.

After provisioning, confirm the command emitted only `Initial Analyst account created.` and that the audit
log contains an `AnalystProvisioned` event with the command actor. Use a synthetic account for deployment
smoke testing.

## Assign a request to the initial Analyst

Run this from the same administrator-controlled interactive console. The command only works when exactly
one active Analyst exists, checks that the request exists, serializes changes against other assignment
commands, and records the operator, prior/new Analyst IDs, time, and sanitized optional reason in the audit
log. Enter an internal operator identifier, not an email address; do not put credentials, personal data, or
other sensitive details in the reason.

```powershell
dotnet run --project MCAROC.Portal/MCAROC_Analysis -- --assign-analyst-request
```

Re-running it with the same request and reason is a no-op. Changing the reason updates the assignment
metadata and creates an audit event. This remains an operations command; it does not create a public or
analyst-facing assignment endpoint.
