# Application login (issue #308)

The portal now requires a session for application pages, reports, uploads, downloads, and job endpoints. The application user has no reviewer or analyst role. Existing reviewer and analyst login paths retain their authorization rules. An analyst cookie continues to restrict legacy routes; signing in successfully at `/login` clears analyst and reviewer cookies to switch to the application account.

## Provision the initial account

After building/publishing, use PowerShell on the host (.NET Framework 4.8 or PowerShell 7):

```powershell
./scripts/Set-ApplicationLogin.ps1 -ApplicationDirectory '<portal source or published directory>'
```

Enter the owner-supplied initial password at the masked prompt. The default login ID is `usermcaroc@ct.com`. The script saves an ASP.NET Core Identity password hash in `appsettings.ApplicationLogin.json` in the specified application directory. Repeating provisioning leaves existing credentials unchanged. This host-local file is ignored by Git and excluded from build/publish output. Restrict its filesystem permissions to the host operator and application service account. Keep the file on the host across deployments.

Alternatively, supply `ApplicationAuth__Username` and `ApplicationAuth__PasswordHash` through host environment configuration. Environment variables override the local file. Without a configured hash, login fails closed. Never put a plaintext password into configuration, source, logs, or a public issue.

To change the password, repeat the command with `-Rotate`. Existing application cookies are rejected when the configured username or hash changes. Environment-supplied credentials require restarting the service after changing the environment. Cookies last at most eight hours and are nonpersistent.

## Hosting checks

Serve HTTPS; the authentication cookie is always Secure and HttpOnly. When hosting behind a reverse proxy, configure forwarded headers only for trusted proxy addresses in the hosting environment so the app recognizes the original HTTPS scheme. Keep the ASP.NET data-protection key ring persistent and restricted to the service identity (see existing Program.cs data-protection configuration).

`/login`, `/analyst/login`, bundled static assets, and `/health/live` are anonymous. `/health` requires login because it includes operational error details. Generated/client documents must remain outside `wwwroot`; their controller downloads require authentication. No anonymous exception is provided for report generation.

Verify on the actual HTTPS host: anonymous home/report/download/API access is rejected; correct credentials sign in; wrong credentials fail; logout and expiry block subsequent access; reviewer and analyst permissions still hold; the minimal liveness endpoint remains available. Deployment and production smoke verification are separate from local implementation checks.
