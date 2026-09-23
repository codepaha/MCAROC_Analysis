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
smoke testing. Assignment/reassignment and the analyst queue are separate issue #270 slices.
