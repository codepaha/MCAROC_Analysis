---
name: run-mcaroc-portal
description: Build, run, and drive the MCAROC.Portal ASP.NET app (the MCA/ROC analysis portal). Use when asked to start the portal, screenshot or curl a page, check that a change works in the running app, or smoke-test its routes end to end.
---

This is an ASP.NET Core MVC web app (net10.0, Kestrel). It is driven with `curl` against real routes and
the real dev SQL Server database — there is no separate test UI to click through. The driver is
`.claude/skills/run-mcaroc-portal/smoke.sh` (`build` / `start` / `smoke` / `stop`); run it from Git Bash.

All paths below are relative to the repo root (`E:\MCAROC_Analysis`).

## Prerequisites

Already set up on this dev machine — verified present this session:

- **.NET SDK 10.0** (`dotnet --version` → `10.0.401`).
- **SQL Server** reachable at `.\SQLEXPRESS` with Windows-integrated auth (`sqlcmd -S ".\SQLEXPRESS" -Q "SELECT 1"` succeeds), database `MCAROC_Analysis` already created and populated with real analysed requests.
- The connection string is already in `MCAROC.Portal/MCAROC_Analysis/appsettings.Development.json` — nothing to configure for a local run.

Vertex AI / InstaFinancials / reference-tool secrets (see the repo README, "Configuring secrets") are
**not** required to build, start, or smoke-test the app — those features no-op or fail closed until
configured; they don't block startup or the routes this driver checks.

## Run (agent path)

```bash
bash .claude/skills/run-mcaroc-portal/smoke.sh build   # dotnet build; ~15-40s
bash .claude/skills/run-mcaroc-portal/smoke.sh start   # launches, polls until healthy, then returns
bash .claude/skills/run-mcaroc-portal/smoke.sh smoke   # drives real routes, prints PASS/FAIL per line
bash .claude/skills/run-mcaroc-portal/smoke.sh stop    # kills the process listening on the port
```

Call each of these as an **ordinary foreground command** — see Gotchas below for why `start` must not
also be wrapped in your own background-job mechanism.

`start` launches on `http://localhost:5219` (override with `PORT=...`), backgrounds the actual `dotnet`
process itself, polls `/` until it returns 200 (up to 40s), then returns. Console output goes to
`/tmp/mcaroc-portal.log` (override with `LOG_FILE=...`).

`smoke` is the harness — verified this session, every line real:

| check | expects |
|---|---|
| `GET /` | 200 — dashboard home |
| `GET /Requests` | 200 |
| `GET /Requests/AutoFetch` | 200 |
| `GET /Requests/999999` (nonexistent id) | 404, not 500 |
| `GET /registry` (an `[Authorize(AuthenticationSchemes="InternalReviewer")]` route) | 302 to `/internal/login`, not 500 |
| `POST /Requests/AutoFetch` with a blank CIN, no antiforgery token | 400 |
| `GET /Requests/{id}` for a real, already-analysed request pulled live from the dev DB | 200 |
| `GET /Requests/{id}/dossier` for that same request | 200, `application/pdf`, and the bytes are actually a PDF (checked with `file`) |

If the dev DB has no request in `AnalysisCompleted`/`DataExtracted` status, the last two checks print
`SKIP` instead of failing — a fresh/empty DB is a valid state, just untested by this script.

`stop` finds the real Windows PID by the port it's listening on (`Get-NetTCPConnection`) and
`taskkill //F`s it — the bash job id from `start` is not this PID (see Gotchas).

## Run (human path)

`dotnet run` from `MCAROC.Portal/MCAROC_Analysis/` opens `http://localhost:5219` (per
`Properties/launchSettings.json`, `launchBrowser: true`). Ctrl-C to stop. Meaningfully the same app as
above; the difference is just that it blocks the shell and opens a browser, which is why the agent path
above exists.

## Test

```bash
cd MCAROC.Portal/MCAROC_Analysis.Tests && dotnet test --nologo --logger "console;verbosity=minimal"
```

212 test files, 1,705 tests. Ran clean this session: `Passed! - Failed: 0, Passed: 1705, Skipped: 0,
Total: 1705, Duration: 6 m 45 s`. This runs against the same local SQL Server the app itself uses (real
integration tests, not mocks-only) — budget ~7 minutes, not seconds, and expect no console output at
`verbosity=minimal` until the final summary line (it isn't hung — this machine's local `.\SQLEXPRESS` with
Windows-integrated auth ran the whole suite with no SSPI/Kerberos block, unlike a condition seen from a
different host; see `AGENT_CHANNEL.md`).

## Gotchas

- **Don't double-background `start`.** It already launches the real `dotnet` process with `&` + `disown`
  and returns once healthy. If you *also* run `start` itself through a tool's own "run in background"
  feature, the two backgrounding layers can fight: the outer wrapper reports the command as "exited"
  while the inner `dotnet` process is still alive and orphaned, holding port 5219 with no tracked handle
  to it left anywhere. Hit exactly this while building this skill — the fix was calling `start` as a
  plain foreground command (it returns in a few seconds anyway once the health poll succeeds).
- **The PID that matters is not the bash job's PID.** Because of the backgrounding above, `$!` is the
  subshell, not `dotnet`'s actual child process. `stop` (and `start`'s "already running" check) instead
  resolve the real PID from whatever is listening on port 5219 via
  `Get-NetTCPConnection -LocalPort 5219 -State Listen` — use that approach if you need the PID for
  anything else (e.g. attaching a debugger).
- **SQL Server Express's `AUTO_CLOSE ON` default** can stall a request past the 30s command timeout the
  first time the connection pool drains to zero after being idle (surfaces as a generic
  `TaskCanceledException`, unrelated to what the request actually did). Already documented in the repo
  README with the one-time fix (`ALTER DATABASE ... SET AUTO_CLOSE OFF`) — not something this session's
  run hit, but worth knowing if a route that passed in `smoke` suddenly times out after the app sits idle.

## Troubleshooting

- **`Failed to bind to address http://127.0.0.1:5219: address already in use`** on `start`: something is
  already listening on the port — most likely an orphaned instance from the double-backgrounding gotcha
  above. Run `stop` first (it kills whatever's listening on the port, orphaned or not), then `start`
  again. If `stop` says "Nothing listening" but the bind error persists, another process outside this
  driver owns the port — find it directly:
  `powershell -Command "Get-NetTCPConnection -LocalPort 5219 -State Listen | Select OwningProcess"`.
