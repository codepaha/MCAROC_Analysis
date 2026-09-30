# Calculation Assurance (#164) — Rollout Runbook

This is the operational guide for #164: deterministic audit, AI discrepancy triage, and delivery hold.
The feature's own code was reviewed and merged across six PRs (#170, #171, #173, #177, #178, #181) — this
document is the last piece from the original plan's §8 sequencing: how to actually turn it on somewhere.

**Current state as of this writing: fully inert everywhere.** `CalculationAssurance:Mode` defaults to
`"Off"` in the committed `appsettings.json`. Nothing in this feature does anything — no ledger rows, no
checks, no AI calls, no holds — until an operator deliberately configures one environment.

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

## 3. Signing in to the review panel

The separate reviewer login (`/internal/login`, `InternalAuth:*` config, `mcaroc_internal_auth` cookie) has been
removed. The review panel at `/internal/calc-audit/{requestId}` is available to the single portal user who signs
in at `/login` (`ApplicationAuth:Username` / `ApplicationAuth:PasswordHash`; see `docs/application-login-hosting.md`).
The restricted Analyst role cannot reach it. Rotating the credential is done there, not here. The name shown in
"Signed in as ..." is the signed-in user's name; the reviewer name recorded on a confirm/reject action is still
typed into that form by hand (see the known limitation in §6). When user access management is
introduced with client onboarding, this panel is one of the pages that will need role checks.

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
  resolve form is manually typed and not cross-checked against the signed-in user. With a
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
