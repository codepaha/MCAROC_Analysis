# Calculation Assurance (#164) — Rollout Runbook

This is the operational guide for #164: deterministic audit, AI discrepancy triage, and delivery hold.
The feature's own code was reviewed and merged across six PRs (#170, #171, #173, #177, #178, #181) — this
document is the last piece from the original plan's §8 sequencing: how to actually turn it on somewhere.

**Current state as of this writing: fully inert everywhere.** `CalculationAssurance:Mode` defaults to
`"Off"` in the committed `appsettings.json`, and `InternalAuth:ReviewerUsername`/`ReviewerPasswordHash` are
committed empty. Nothing in this feature does anything — no ledger rows, no checks, no AI calls, no holds,
and nobody can sign in to the review panel — until an operator deliberately configures one environment.

## 1. What each mode does

| `CalculationAssurance:Mode` | Ledger + deterministic checks | AI second-line review | Delivery gate |
|---|---|---|---|
| `Off` (default) | Never runs — zero DB queries | Never runs | Never blocks a download |
| `ObserveOnly` | Runs and persists normally | Runs and persists normally | Evaluated but never actually blocks; a would-be-held case is only logged (`ObserveOnly: would have held request {RequestId}...`) |
| `Enforced` | Runs and persists normally | Runs and persists normally | Actually blocks a held report's download (returns the same `404` a missing request would) |

`ObserveOnly` is the required first step in any real environment — it produces every ledger row, check
result, and (if `AiAuditEnabled`) AI candidate exactly as `Enforced` would, but never blocks a customer
download while you're still learning what the checks actually find on your real data.

## 2. Turning it on — step by step

1. **Never set `Mode` in the committed `appsettings.json`.** Set it in that one environment's own config
   layer (environment variable `CalculationAssurance__Mode`, an App Service / container app setting, or
   equivalent) — the repo default must stay `Off`.
2. Set `CalculationAssurance:Mode` to `ObserveOnly` in that environment. **This generally requires an
   application restart/recycle to take effect** — environment variables and App Service settings are read
   once at process startup, not watched for changes (see the restart-dependency note under "Rotating the
   credential" in §3, which applies identically here). Don't assume the flip is live; verify it (a quick
   test request, or watching for the mode-specific log lines in §4) before relying on it.
3. Set up the reviewer credential (§3 below) so someone can actually sign in and see what's happening —
   `ObserveOnly` still needs a human looking at the panel, it just doesn't block delivery on its own.
4. Let it run for a real analysis cycle or two. Watch the signals in §4.
5. Once you're confident (see the checklist in §4), flip that same environment's `Mode` to `Enforced`.
6. **Before flipping to `Enforced`, read the "fail-closed on missing snapshot" note in §5 — it is not
   optional.** It will hold reports you did not expect to be held on the very first request after the
   flip, unless you've planned for it.

## 3. Setting up the reviewer credential

There is no seed data, no admin UI, and no self-service reset for this — the credential lives entirely in
config (deliberate: with exactly one reviewer role today, a DB table for it would just be another place a
hash could leak into source control).

1. Pick a username and a strong password for the shared reviewer account. (Decision #2 from the original
   issue: single reviewer role today; see §6 for what raising this to two reviewers later would take.)
2. Generate the password hash. There's no CLI wired up for this — the simplest way is a one-off script
   referencing the existing hasher directly:

   ```csharp
   // scratch.csx or a throwaway Program.cs in a scratch console project that references MCAROC_Analysis
   Console.WriteLine(MCAROC_Analysis.Services.InternalAuth.InternalReviewerCredentialChecker.Hash("your-chosen-password"));
   ```

   This prints a string shaped `iterations.saltBase64.hashBase64` (PBKDF2-SHA256, 210,000 iterations,
   32-byte output) — that whole string is the value for `ReviewerPasswordHash`, not the raw password.
3. Set these three values in that environment's config (user-secrets locally, environment variables or an
   App Service configuration blade / secret store in a real deployment — **never** commit real values to
   `appsettings.json`):
   - `InternalAuth__ReviewerUsername`
   - `InternalAuth__ReviewerPasswordHash` (the string from step 2)
   - `InternalAuth__ReviewerDisplayName` (shown in the panel's "Signed in as ..." line and recorded as
     `ReviewerName` on every `CalculationDiscrepancyApproval` row — see the known limitation in §6)
4. Sign in at `/internal/login`. The cookie (`mcaroc_internal_auth`) is `HttpOnly`, `Secure`, and
   `SameSite=Strict`, expires after 8 hours, and is scoped to this one feature only — no other page in the
   app is affected by this scheme being registered (`AddAuthentication()` is called with no default scheme
   name, deliberately).
5. Login attempts are rate-limited to 5 per 5 minutes per client IP (`429` beyond that) — if you're testing
   and lock yourself out, wait 5 minutes rather than restarting the app to reset it (the limiter is
   in-memory per-process, so a restart does reset it, but don't rely on that in a real deployment with
   multiple instances).

### Rotating the credential

Generate a new hash (step 2 above) and update `ReviewerPasswordHash` in that environment's config. No code
change and no migration — but **whether it takes effect without a restart depends entirely on which config
provider holds it**, not on anything this app does at login time:

- **Environment variables** (`InternalAuth__ReviewerPasswordHash` set directly on the process/container) are
  read once when the host starts. ASP.NET Core's environment-variable provider does not watch for changes —
  a rotated value has no effect until the app process restarts.
- **Azure App Service Application Settings** are also environment variables under the hood, with the same
  no-reload behavior in the app itself — but changing a setting in the Azure portal/CLI triggers App
  Service to recycle the app for you, so it works, just because Azure restarts the process, not because
  the app noticed the change.
- **User secrets** (local development) are likewise read once at startup — restart `dotnet run` after
  changing them.
- The only way a rotation would apply live, with no restart, is a config source that explicitly supports
  reload (e.g. Azure App Configuration or Key Vault wired up with its reload-on-change provider) — this app
  does not currently use one for `InternalAuth`.

**After rotating, always verify with a fresh sign-in attempt** (confirm the old password now fails and the
new one succeeds) rather than assuming the change is live — if the environment needs an explicit
restart/recycle and it wasn't done, the old credential will silently keep working. The same restart
dependency applies to changing `CalculationAssurance:Mode` (§2) — after flipping it, verify with a quick
check (e.g. confirm a fresh `ObserveOnly`/`Enforced`-only log line appears, or that the delivery gate's
behavior actually changed on a test request) rather than assuming the flip took effect immediately.

## 4. What to check in `ObserveOnly` before ever proposing `Enforced`

Everything below is visible via application logs (structured log messages, no dashboard exists yet) and by
signing in to `/internal/calc-audit/{requestId}` for individual requests.

- **Missing-snapshot rate.** Search logs for `Calculation-assurance snapshot missing for request`. Every
  hit is a *currently-live* report whose snapshot predates this feature (or predates `Mode≠Off` in this
  environment) — under `Enforced`, every one of these gets held on the next download attempt, with no
  exception path, until it's re-analyzed. Size this population before flipping. If it's large, plan a
  deliberate re-analysis pass first (or accept the initial hold wave and communicate it).
- **Would-be-held rate.** Search for `ObserveOnly: would have held request`. This is the real signal for
  "how often would `Enforced` actually block a download right now." A near-zero rate after the
  missing-snapshot population is cleared is what you want to see before flipping.
- **Deterministic check trigger rate**, per `CheckKey` — sign in and look at individual snapshots'
  `CalculationCheckResults`, or query the table directly. A check that fires on every single request is
  probably measuring something real but common (worth a policy conversation, not a bug); a check that never
  fires is either genuinely clean data or has a bug worth a second look before it's trusted to gate
  anything.
- **AI audit run status distribution** — `CalculationAiAuditRuns.Status`: `Completed` vs
  `CompletedWithErrors` (malformed/rejected model response — check `RejectedCandidatesJson` for why) vs
  `SkippedAiUnavailable` (exhausted retries — check `FailureReason`, this is a Vertex AI availability/quota
  signal, not a data-quality one). A high `SkippedAiUnavailable` rate means the AI second-line review isn't
  actually running for most requests — deterministic checks and the delivery gate are unaffected by this
  (the AI worker never gates anything on its own; see `CalculationArtifactHoldReason` — there is deliberately
  no "AI hasn't finished yet" hold reason), but it does mean you're not getting the AI candidate coverage
  you might expect.
- **Discrepancy volume reaching the reviewer queue** — `CalculationDiscrepancies` where `Status = Open`
  (AI candidates awaiting triage) — if this is piling up faster than the reviewer can act on it, that's an
  operational capacity question, not a code question.

## 5. The fail-closed-on-missing-snapshot consequence (read before flipping to `Enforced`)

`CalculationArtifactGateService` treats a request with **no** `CalculationAuditSnapshot` at all for its
current `(RequestId, IngestionRunId, AnalysisRunId)` tuple as the maximal case of "not evaluated," which
under `Enforced` means **held**, identically to a confirmed Critical discrepancy — same `404`, no
distinction exposed to the caller. This is deliberate (the issue's own policy: `NotEvaluated` is never a
passing result) but it means: the instant you flip `Enforced` in an environment, **every currently-live
report that was last analyzed before this feature existed (or before `Mode≠Off` in that environment) becomes
undownloadable** until it's re-analyzed under `Mode≠Off`. There is no grandfathering and no exception route
for this case (exceptions only exist for a confirmed Material discrepancy, not for "never audited").

Plan for this explicitly: either run a deliberate re-analysis pass over currently-live requests before
flipping, or flip anyway and be ready to explain a wave of held reports and drive re-analysis reactively.
The `ObserveOnly` missing-snapshot log lines from §4 are exactly how you size this before deciding.

## 6. Known limitations / deferred items

- **`ReviewerName` on every approval is free text, not derived from the login.** Signing in only gates
  *access* to the panel; the "Your name" field on every triage/confirm/reject/accept-exception/mark-fixed/
  resolve form is manually typed and not cross-checked against `InternalAuth:ReviewerDisplayName`. With a
  single shared reviewer credential (decision #2), this is a real but bounded gap — anyone with the one
  shared login can attribute a decision to any name. If this stops being acceptable (e.g. once real
  per-person accounts exist), the form should prefill from `User.Identity.Name` and the server should
  reject a mismatch, rather than trusting the submitted value.
- **No in-UI emergency override action.** The schema supports it
  (`CalculationAssuranceOverrideAudit`), but there's no `/override-mode` route or form — an emergency
  `Mode` change today is a config change plus a manual row insert into
  `CalculationAssuranceOverrideAudit` (previous mode, new mode, who, why, when) for the audit trail, not a
  button in the panel.
- **Dual-approval is schema-ready but has no admin UI.** `CalculationDiscrepancy.RequiredApprovals`
  defaults to `1` everywhere and there's no UI to raise it per-discrepancy or globally — doing so today
  means a direct data change. The workflow service already enforces the gate correctly once
  `RequiredApprovals` is `2` (tested: `CalculationDiscrepancyWorkflowServiceTests`), so raising it is safe
  whenever there's a reason to.
- **No dashboard.** Every signal in §4 is log-search or direct-query today; there's no aggregated
  ObserveOnly telemetry view.
