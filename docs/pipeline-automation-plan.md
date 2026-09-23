# Pipeline automation — plan for stages 1–4

Status: **draft for review** (round 5 — round 1 review mapped in §12; owner decisions on unlock and litigation
reuse and the MCA-ROC user-flow spec mapped in §13). Scope: (1) pipeline coordinator, (2) chain the manual hand-offs,
(3) unattended reference-tool session, (4) exception handling. Out of scope here: intake API / bulk / schedule
(stage 5), notifications and webhooks (stage 6), hosting and monitoring (stage 7) — see §9.

Goal: once a request exists, it reaches "dossier ready" (and, where policy allows, "fully enriched") with no
human action, and a human is pulled in **only** for a named, actionable reason.

---

## 0. Corrections to the earlier gap analysis

Reading the code more closely changed two claims made in the first answer:

1. **"Re-run analysis after filings complete" is dropped.** `AnalysisOrchestrator.BuildContextAsync` and
   `DossierAssembler` read only the workbook tables (`FinancialYearData`, `RocCharges`, `Litigations`, …).
   Neither reads `McaFiling*` rows. Filings feed *Ask Documents* (chat) only. Analysis and the dossier are
   therefore complete as soon as ingestion + analysis + calculation assurance are.
2. **Litigation (BPR) does not feed the dossier.** It surfaces in the request's Litigation tab
   (`_LitigationTab.cshtml`). The dossier's litigation section is the workbook `Litigations` table. So
   litigation is an *enrichment* track, not a prerequisite of "dossier ready".
3. **Session handling is weaker than implied.** Auto-login exists but only at job start
   (`CheckSessionAsync`); §5 lists what is missing.

Consequence: the pipeline has a **core track** (fetch → ingest → analysis → calc-assurance → dossier) and two
**parallel enrichment tracks** (filings/chunking, litigation). Completion is defined per track (§3.4).

## 1. Verified current state (what the plan builds on)

| Area | Fact | Where |
|---|---|---|
| Fetch job | One `AutoFetchJob` per request; resumable checkpoints; atomic claim Queued→CheckingSession; startup recovery re-queues non-terminal jobs; `HeartbeatUtc` is written | `AutoFetchJobService`, `AutoFetchWorker`, `AutoFetchJob` |
| Ingest→analysis | Job enqueues analysis if `DataExtracted && !IsManualReviewRequired`; otherwise adds a warning only | `AutoFetchJobService.cs:188-191` |
| Analysis | Atomic claim on `RequestStatus==DataExtracted`; restart-not-resume recovery; enqueues calc ledger/checks and **AI audit** (async) | `AnalysisOrchestrator` |
| Dossier | PDF rendered **on first GET**, cached on disk per (ingestion run, analysis run), gated by `CalculationArtifactGateService.IsHeldAsync` | `DossierController`, `DossierCache` |
| Litigation search | Started only by `POST /Requests/{id}/Litigation/Search` behind the `InternalReviewer` cookie; eligibility logic lives **inside the controller** | `LitigationController.StartSearch` |
| Litigation chain | search job → snapshot persistence → order documents → order chunking are already queue-chained; AI analysis is a separate manual `POST …/Litigation/Analysis` (`CreateOrJoinAsync`, lease-fenced, own retry) | `Litigation*Service`, `LitigationAiAnalysisOrchestrator` |
| Refresh / unlock lifecycle | **Absent from the code**, but the real API contract is now captured and verified live (§5.7.0): `getAssetTeams`/`getCompanyPreview`/`addAsset`/`requestProbeDataUpdate`/`getDataEntryRequestStatus`/`getStatus`/`getDataStatus`. Auto-fetch still exports the workbooks immediately today: it never triggers or waits for a refresh, so it can store pre-refresh data (the user-flow spec forbids this: "no stale data"). "Not unlocked" surfaces only as a failed export. `CompanyRefreshPolicy` encodes the spec's 24 h / join-pending rule but nothing feeds it state | `ReferenceToolClient.cs:232-267`, `CompanyRefreshPolicy.cs` (unused), [reference-tool-refresh-unlock-contract.md](reference-tool-refresh-unlock-contract.md) |
| Litigation search job identity | **One row per request** (`UNIQUE(RequestId)`), re-run **in place** — the row is overwritten, so it cannot serve as spend history. The register POST has no vendor idempotency key; a crash after `RegistrationAttemptedUtc` with no `VendorJobId` already fails closed (spend outcome unknown) | `LitigationSearchJob`, `AppDbContext.cs:155` |
| Litigation AI run identity | Request-scoped; unique *active* run per request (filtered index on `Pending/InProgress`) and `UNIQUE(RequestId, RunNumber)`; **no snapshot reference and no trigger origin**, so "once per snapshot" is not enforceable today | `LitigationAiAnalysisRun`, `AppDbContext.cs:271-272` |
| Worker queue | `AutoFetchWorker` reads job ids from an **in-memory** channel, then claims in the DB (`Status==Queued`). A job dequeued but not claimed stays `Queued` in the DB yet is absent from the channel until the next restart | `AutoFetchWorker.cs:18-22` |
| Session | `_activeSessionCookie` is an **instance field** on a typed `HttpClient` client (registered transient) ⇒ every job scope logs in separately; `_loginGate` is per instance too | `ReferenceToolClient.cs:46-48`, `Program.cs:62` |
| Session loss mid-job | 401/403 during a download throws a non-retryable `ReferenceToolException`; there is no re-login. "Not unlocked" and "expired session" both arrive as HTTP 200 with non-workbook body | `ReferenceToolClient.cs:471`, `:232-267` |
| Failure typing | `ReferenceToolException` has only `Retryable: bool`; failure *kind* is only in the message string | `ReferenceToolModels.cs` |
| Pre-ingestion failure | Sets `RequestStatus=ExtractionFailed` even for a transient outage | `AutoFetchJobService.cs:233-239` |
| Unused policy | `CompanyRefreshPolicy` (24h freshness) is defined and tested-style pure code but called nowhere | `CompanyRefreshPolicy.cs` |

## 2. Design principles

1. **Observe, then act.** The coordinator ships first in *Observe* mode (computes and displays state, triggers
   nothing) so its judgement can be compared with reality on real requests before it is allowed to spend money.
2. **Derived state, not duplicated state.** Source-of-truth stays in the existing tables. The coordinator
   *reads* them each tick and writes only a projection plus the things only it knows (attempts, backoff,
   attention reasons, policy skips). No stage is rewritten to "report to" the coordinator.
3. **Level-triggered reconciliation** (like Kubernetes controllers): every tick asks "given the current facts,
   what is the next legal action?" This survives crashes and restarts and also adopts requests created by the
   manual upload path.
4. **Triggers reuse existing idempotent entry points** (`CreateOrResetJobAsync`, `CreateOrJoinAsync`,
   atomic claims). A duplicate trigger must collapse, never double-spend.
5. **Paid calls are admitted durably, not decided in memory.** Litigation search and litigation AI analysis
   cost money per call. A `PipelineDecider` check is advisory and can be stale; the only authority for "may this
   paid call be made now" is an atomic database admission (§4.0) that holds under concurrent reconcilers,
   multiple instances and crashes. Caps and once-only rules are database invariants, not application checks.
6. **Pure decision core.** `PipelineDecider` is a pure function (snapshot + policy + now → actions), unit-tested
   with table tests, the same shape as `CompanyRefreshPolicy`. I/O lives in a separate reader/executor.
7. **Stable reason codes.** Every attention/skip reason is a stable string code (referenced by tests, UI and
   later alert rules) — same convention as finding codes.
8. **Money-moving actions are human-approved today and flag-gated for tomorrow.** Unlocking a company spends
   credits. Today the system raises an alert and waits for an explicit approval; the same code path runs with
   the approval replaced by a policy check when auto-unlock is later switched on (§5.7). Turning the future
   behaviour on is configuration, not a redesign — but it is off by default and cannot be enabled without the
   admission ledger (§4.0).

---

## 3. Stage 1 — Pipeline coordinator

### 3.1 Model

Stages (enum `PipelineStage`): `Resolve`, `Unlock`, `Refresh`, `Fetch`, `Ingest`, `Analysis`, `CalcAssurance`, `Dossier`,
`Filings`, `Litigation`, `LitigationAnalysis`. (`Unlock` and `Refresh` model the reference report's lifecycle
from the MCA-ROC user-flow spec; §5.7. `Resolve` turns a target *name* into a CIN/LLPIN; §5A.)

Stage state (`PipelineStageStateKind`): `NotStarted`, `Waiting` (prerequisite unmet), `Running`, `Succeeded`,
`SucceededWithWarnings`, `RetryScheduled`, `NeedsAttention`, `Skipped` (policy/config), `Cancelled`.

Request outcome (`PipelineOutcome`): `InProgress`, `CoreReady`, `Complete`, `CompleteWithWarnings`,
`NeedsAttention`, `Cancelled`.

Tables (one EF migration):

- `PipelineRuns` — `PipelineRunId`, `RequestId` (filtered unique index `WHERE Outcome IN ('InProgress','CoreReady','NeedsAttention')` — at most one
  live run per request), `Trigger`
  (`AutoFetch` | `ManualUpload` | `Adopted` | future `Api`/`Schedule`), `PolicyJson` (snapshot of enabled
  optional stages *at creation*, so a later config change does not silently alter a running pipeline),
  `Outcome`, `CorrelationId`, `CreatedUtc`, `CoreReadyUtc`, `CompletedUtc`, `ReconcileLeaseOwner/Token/ExpiresUtc`,
  `RowVersion`.
- `PipelineStageStates` — `PipelineRunId`+`Stage` (PK), `State`, `SourceRef` (id of the ingestion/analysis/batch/
  job/run row that proves the state), `Attempts`, `NextAttemptUtc`, `ReasonCode`, `ReasonDetail`, `StartedUtc`,
  `UpdatedUtc`, `LastHeartbeatUtc`.
- `PipelineEvents` — append-only log of every coordinator decision (`Stage`, `Action`, `Actor` =
  `system|<reviewer>`, `ReasonCode`, `CorrelationId`, `AtUtc`). This is the "why did it do that" record; it
  complements (does not replace) the existing audit log for human actions.

### 3.2 Components (new folder `Services/Pipeline/`)

- `PipelineSnapshot` — immutable read model of the facts for one request (job status/checkpoints, request
  status + manual-review flag, latest ingestion/analysis run, calc-assurance + AI-audit run status, filing
  batch + chunking status, litigation job/snapshot/order-doc/analysis statuses).
- `PipelineSnapshotReader` — one query set per request (`AsNoTracking`), no writes.
- `PipelineDecider` — **pure**. `Decide(snapshot, currentStates, policy, nowUtc) → PipelineDecision`
  (new stage states + a list of `PipelineAction`s such as `StartLitigationSearch`, `RenderDossier`,
  `RetryStage(stage)`, `RaiseAttention(stage, code)`).
- `PipelineActionExecutor` — performs actions by calling the existing services; catches "already active"
  (`InvalidOperationException` from `CreateOrResetJobAsync`, unique-index `DbUpdateException`) and treats it as
  success (idempotent join).
- `PipelineReconcilerWorker` (`BackgroundService`) — tick `Pipeline:TickSeconds` (default 30); selects
  non-terminal runs whose lease is free via an `ExecuteUpdate` claim (same fenced-lease pattern as
  `LitigationAiAnalysisOrchestrator`); processes up to `Pipeline:MaxRunsPerTick`. An in-process
  `PipelineSignal` lets stage services nudge an immediate reconcile, but correctness never depends on it.
- `PipelineAdopter` — creates a `PipelineRun` for (a) `AutoFetchController.New/Retry`, (b) manual create/upload
  paths in `RequestsController`, (c) a sweep for requests created **on or after** `Pipeline:AdoptAfterUtc`.
  The cutoff is deliberate: without it, turning the feature on would fire paid litigation calls for every
  legacy request (including the COASTAL demo dataset).

### 3.3 Stage dependency graph (what "next legal action" means)

```
Resolve ─► Unlock ─► Refresh ─► Fetch ─► Ingest ─► Analysis ─► CalcAssurance ─► Dossier   (core → CoreReady)
                                            │
                                            ├────► Filings   (observe only: batch + chunking terminal)
                                            └────► Litigation ─► LitigationAnalysis        (enrichment,
                                                                        policy-gated; may be satisfied by reuse, §4.2a)
```

- `Resolve` succeeds when the request has a confirmed CIN/LLPIN: supplied by the requester, chosen by a human
  from ranked candidates, or auto-selected above the calibrated confidence bar (§5A). Otherwise
  `NeedsAttention(IDENTITY_AMBIGUOUS | IDENTITY_NOT_FOUND)` — sent to the internal-review ambiguity queue
  (§5A.3), never guessed. **No stage after it — in particular `Unlock`, which is what actually spends money —
  starts until it has succeeded** (the gate is stated again, explicitly, at `Unlock` itself in §5.7, since that
  is the stage where an identity mistake would cost real credits, not just produce wrong data).

- `Unlock` succeeds when the company is unlocked and inside its fixed 12-month window; otherwise
  `NeedsAttention(UNLOCK_APPROVAL_REQUIRED | UNLOCK_EXPIRED)` until approved (§5.7). `Refresh` succeeds when a
  refresh completed within the last 24 h (or one completes now); it may sit in `Waiting` for up to 36 h.
  `Fetch` is **never** allowed to run before `Refresh` has succeeded ("no stale data"). Both are
  `Skipped(MANUAL_SOURCE)` for manual-upload requests.
- `Fetch`/`Ingest` map onto `AutoFetchJob` (`RocDocumentId`, `IngestionRunId`). Manual-upload requests have no
  job: `Fetch` is `Skipped(MANUAL_SOURCE)` and `Ingest` is read from `LatestCompletedIngestionRunId`.
- `Analysis` succeeds on a `Completed|CompletedWithErrors` `AnalysisRun` whose `IngestionRunId` equals the
  request's latest completed ingestion run (the same lineage rule `DossierCache` uses).
- `CalcAssurance` succeeds when deterministic checks are persisted **and** the AI audit (if enabled) is terminal.
  This matters: the AI audit is enqueued async, and a hold can appear after analysis "completes".
- `Dossier` = pre-render succeeded and `IsHeldAsync == false`. If held ⇒ `NeedsAttention(CALC_GATE_HOLD)`.
- `Filings` needs no trigger (auto-fetch already hands the archive to the filing queue and chunking); the
  coordinator only reports it. `Skipped(NO_FILINGS_REQUESTED)` when the job had `IncludeFilings=false`.
- `Litigation`/`LitigationAnalysis` are §4.
- *Until #229/#266 ship (as implemented in #264's Observe mode):* nothing records unlock or refresh yet, so for an
  auto-fetch request `Unlock` is `Succeeded(INFERRED_FROM_EXPORT)` once the workbook export succeeded (the tool
  only exports an unlocked company) and `NotStarted(NOT_YET_TRACKED)` before that, and `Refresh` is
  `Skipped(Neutral, REFRESH_NOT_TRACKED)` — auto-fetch today exports the tool's current data without a refresh.
  Both are replaced by real lifecycle state when those issues land.

### 3.4 Completion semantics

The outcome is computed by one pure function, `PipelineDecider.Aggregate(stageStates, cancelled)`, with
**first-match precedence**:

| # | Condition | Outcome |
|---|---|---|
| 1 | run cancelled | `Cancelled` |
| 2 | any stage `NeedsAttention` | `NeedsAttention` |
| 3 | any stage `NotStarted`/`Waiting`/`Running`/`RetryScheduled`, and all core stages `Succeeded*` | `CoreReady` |
| 4 | any stage `NotStarted`/`Waiting`/`Running`/`RetryScheduled` (core not yet done) | `InProgress` |
| 5 | all stages terminal (`Succeeded`, `SucceededWithWarnings`, `Skipped`) **and** any `SucceededWithWarnings` or warning-kind `Skipped` | `CompleteWithWarnings` |
| 6 | all stages terminal, none of the above | `Complete` |

- `Skipped` carries a `SkipKind`: **`Neutral`** (policy off, `MANUAL_SOURCE`, `NO_FILINGS_REQUESTED`) or
  **`Warning`** (integration not configured while its policy is on, `LITIGATION_NO_IDENTIFIER`).
  A litigation stage satisfied by reuse (§4.2a) is `Succeeded` (with the source recorded), not `Skipped`. So `Complete` means *zero warnings of any kind*; rows 5 and 6 are disjoint.
- "Dossier is downloadable" is a separate fact from the outcome: `CoreReadyUtc` is set the first time all core
  stages are `Succeeded*` and is **never cleared** when the outcome later becomes `NeedsAttention` (an
  enrichment stage needing a human does not withdraw a valid dossier). The UI shows both.
- Tests: one table test per row, plus a generated-combination test asserting the function is total (exactly one
  row matches) and monotone (adding a `NeedsAttention` stage can never improve the outcome).

### 3.5 UI / API

- `GET /Requests/{id}/pipeline/status` (JSON, `no-store`, same shape conventions as
  `/autofetch/status`) and a stage strip on the request Details page (polls like the auto-fetch panel).
- `GET /Pipeline` board (InternalReviewer): filter by outcome, "stuck for > N minutes", reason code.
- Mutating actions (retry stage, skip stage, cancel) are `[Authorize(AuthenticationSchemes="InternalReviewer")]`,
  antiforgery-protected, take a mandatory reason string, and write both a `PipelineEvent` and an audit-log row.
  New routes must be registered in `AuditRouteRegistry` (it is an explicit allow-list — confirm behaviour for
  unregistered routes before coding).

### 3.6 Rollout controls

`Pipeline:Enabled` (default false), `Pipeline:Mode` = `Observe` | `Enforce` (default `Observe`),
`Pipeline:AdoptAfterUtc`, `Pipeline:TickSeconds`, `Pipeline:MaxRunsPerTick`. Enforce can be narrowed further per
action family (`Pipeline:Enforce:Dossier`, `:Litigation`, `:LitigationAnalysis`, `:Retries`) so the low-risk
actions (dossier pre-render) can go live before the paid ones.

---

## 4. Stage 2 — Chain the manual hand-offs

### 4.0 Paid-call admission (shared by every auto and manual admitted call)

**The decider proposes; the database admits.** Two reconcilers, two app instances, or a reconciler and a
reviewer's button can all pass an in-memory check at the same instant. One short SQL transaction is the only
authority; it atomically (a) claims the dedup scope, (b) takes a slot from the day counter, (c) records the
admission.

**Cost clarification (owner decision, round 5):** of the three `Kind`s below, only `ReferenceUnlock` (unlocking
a not-yet-unlocked company — the confirmed 1-credit spend, §5.7.0) and `LitigationAnalysis` (Vertex AI) carry a
real monetary cost. `LitigationSearch` is free — confirmed by the owner: it queries BPR's own litigation data
lake (~4.5 billion records, per the owner), not a live per-request external crawl, which is consistent with why
it carries no per-call charge. This mechanism still runs all three `Kind`s through the same admission path,
because the scope/counter machinery is doing two jobs at once: (1) guarding real spend for the two paid kinds,
and (2) preventing duplicate/racing work and enabling reuse for the free one — a free-but-redundant query is
still worth avoiding, and not only for politeness: `docs/litigation-data-lake-integration.md` still lists
"rate/concurrency limits" for this system as **genuinely unconfirmed**, and its order-PDF URLs are separately
confirmed to expire after ~7 days regardless of the lake being free to query (§4.1). So `LitigationSearchPerDay`
is a **load/rate-limit guard against an unconfirmed limit, not a spend guard** — it can default more generously
than the two real spend caps (§4.0's `50`/day default), but "free" is not the same claim as "unmetered," and the
cap stays a real, enforced value rather than being removed.

New tables (one migration, ships in PR C):

- `SpendScopes` — one row per `(Kind, ScopeKey)`, unique. Columns: `ActiveAdmissionId` (null when idle),
  `LastCommittedUtc`, `RowVersion`. Created on first use by insert-if-absent (a duplicate-key error means
  "someone else created it — re-read").
- `SpendCounters` — one row per `(Kind, DayKey)`, unique. Column: `Used`. The cap itself is read from config at
  admit time so changing it takes effect immediately.
- `PaidCallAdmissions` — append-only ledger: `AdmissionId`, `Kind` (`LitigationSearch`|`LitigationAnalysis`|`ReferenceUnlock`),
  `ScopeKey`, `DayKey`, `Trigger` (`Auto`|`Manual`), `RequestId`, `ClientId`, `State`
  (`Reserved`|`Committed`|`Released`), `ReferenceId` (job/run id), `ReservedUtc`, `ResolvedUtc`,
  `CorrelationId`. This is also the durable spend history that the overwrite-in-place job row cannot provide.

`IPaidCallAdmission.TryAdmitAsync(kind, scopeKey, trigger, freshnessWindow?, cap)` — one transaction, fixed lock
order scope → counter (no deadlocks):

1. Ensure the scope row exists.
2. Claim the scope with a single conditional UPDATE — the same one-winner idiom the repo already uses for job
   claims:
   `UPDATE SpendScopes SET ActiveAdmissionId=@new WHERE Kind=@k AND ScopeKey=@s AND ActiveAdmissionId IS NULL`
   `AND (LastCommittedUtc IS NULL OR LastCommittedUtc < @cutoff)` (the freshness predicate is omitted for
   `Manual`). 0 rows ⇒ `Denied(IN_FLIGHT)` or `Denied(FRESH)` (distinguished by a follow-up read).
3. `Auto`: `UPDATE SpendCounters SET Used=Used+1 WHERE Kind=@k AND DayKey=@d AND Used < @cap`; 0 rows ⇒ roll
   back, `Denied(COST_CAP_REACHED)`. `Manual`: increment **without** the `Used < @cap` predicate — a human is
   never blocked by the cap, but is always counted.
4. Insert the `Reserved` admission, set `ActiveAdmissionId`, commit.

Exactly one winner per scope under any concurrency and across instances; at most `cap` auto admissions per day.
A rolling freshness window works because it is evaluated against `LastCommittedUtc` in the winning UPDATE
(fixed date buckets would let two purchases straddle a bucket boundary).

**Resolution** (an idempotent sweep in the reconciler, also called eagerly by the executor; every transition is a
conditional `UPDATE … WHERE State='Reserved'`, so double-resolution is a no-op):

- *Search* → `Committed` once the job has `RegistrationAttemptedUtc` set: the purchase may have happened, so the
  system fails closed — this deliberately includes the existing "attempted but no `VendorJobId`" unknown-outcome
  state. Scope gets `LastCommittedUtc = RegistrationAttemptedUtc`, `ActiveAdmissionId = NULL`. The sensitive
  register path in `LitigationSearchJobService` is **not modified**; resolution reads its existing column.
- *Search* → `Released` only when provably nothing was bought: `RegistrationAttemptedUtc IS NULL` and the job is
  terminal or absent (auth failure, ineligible, crash before register), or a `Reserved` admission older than
  `Pipeline:ReservationTtlMinutes` (30) with no job. Release = `Used = Used - 1` (floor 0) on the admission's
  **own** `DayKey`, clear `ActiveAdmissionId`, never touch `LastCommittedUtc`.
- *Analysis* → `Committed` once the run has been claimed at least once (`AttemptCount > 0`); otherwise `Released`
  when the run is terminal or absent. *(Implemented in #265 — deviates from the original "token counts recorded"
  rule: the analysis orchestrator never writes token counts, and it persists case rows only after every case has
  been analysed, so a run that dies mid-way leaves no per-call evidence at all. The first claim is the earliest
  durable signal that a model call may follow; committing there over-counts a run that makes no call, which is
  the fail-closed direction.)*
- *Never linked* (a crash between creating/resetting the job or run and recording it on the admission) → the
  resolver looks for the request's job/run instead of releasing blindly: a search job attempted, or an analysis
  run claimed, at or after `ReservedUtc` commits the admission; only past `Pipeline:ReservationTtlMinutes` with
  no such evidence (and no still-queued job/run it could belong to) is it released.
- A vendor-side *failure after registration* is **not** a new purchase and **not** auto-repurchased: the stage
  goes to `NeedsAttention`; a human re-run is a `Manual` admission.

`DayKey` is the business day in `Pipeline:CapTimeZone` (default `Asia/Kolkata`), never server-local time.
Config: `Pipeline:Caps:LitigationSearchPerDay`, `Pipeline:Caps:LitigationAnalysisPerDay`, `Caps:UnlockPerDay` —
**every cap is a config value, read fresh at admit time (line above), never a compiled-in constant.** The two
real spend caps (`LitigationAnalysisPerDay`, `UnlockPerDay`) default to **0** — the same fail-safe convention
this repo already uses for every other feature (inert until explicitly configured) — so auto-spend admits
nothing until an operator sets a positive number. `LitigationSearchPerDay` (rate-limit only, no spend) can
default to a generous value instead, e.g. `50`, since nothing is protected by keeping it low.

**Per-kind scope rules** (all scopes are company-level, not client-level — the owner decisions in §13):

| Kind | Cost | `ScopeKey` | Window (evaluated against `LastCommittedUtc`) | Cap | Human approval |
|---|---|---|---|---|---|
| `LitigationSearch` | **Free** (rate-limit cap only) | `search\|{CanonicalIdentifier}\|{KeywordSetHash}` | **Rolling 7 days** from the purchase | `LitigationSearchPerDay` (default 50) | Not needed (policy-gated) |
| `LitigationAnalysis` | **Paid** (Vertex AI) | `analysis\|{OriginSnapshotId}` | None — a committed scope is never auto-admitted again | `LitigationAnalysisPerDay` (default 0 — fail-safe) | Not needed (policy-gated) |
| `ReferenceUnlock` | **Paid** (1 credit, §5.7.0) | `unlock\|{CanonicalIdentifier}` | **Fixed 12 months from the original unlock date.** `LastCommittedUtc` is set **only** by an unlock commit and is never touched by a refresh, so a refresh cannot extend the window | `UnlockPerDay` (default 0 — fail-safe) | **Required today** (single-use `UnlockApprovals` row consumed inside the admission transaction, §5.7); replaced by policy when `Pipeline:AutoUnlock` is later enabled |

The manual buttons go through the same path (`LitigationAutoStartService` with `Trigger=Manual`): a click
bypasses the *block* but not the *count* — **manual admissions still increment `Used` against the same daily
cap (owner decision, round 5: kept as originally proposed)**, so a busy manual day can exhaust the day's auto
budget; a click is also still refused while another admission for the same scope is in flight, and is always
recorded. This is what makes the ledger complete.

### 4.1 Litigation search auto-start

- Extract the eligibility + keyword-planning + job-creation block of `LitigationController.StartSearch` into
  `LitigationAutoStartService.TryStartAsync(requestId, trigger, ct) → StartResult` (`Started | AlreadyActive |
  Skipped(code) | NotEligible(code)`). The controller and the coordinator both call it, so the rules cannot
  drift. The controller keeps its HTTP mapping.
- **What "this company" means (dedup scope).** `ScopeKey = search|{CanonicalIdentifier}|{KeywordSetHash}` —
  **company-level, across clients** (owner decision, §13): the litigation report is company data, so one
  purchase serves every request for that company within the 7-day window.
  - `CanonicalIdentifier` = trimmed, upper-cased `AutoFetchCompanyIdentifier ?? Cin ?? Llpin`. A request with
    none of these is **not eligible** for auto-search (`Skipped(LITIGATION_NO_IDENTIFIER)`, a warning-kind skip)
    — a company name alone is not a safe identity to deduplicate or to buy on.
  - `KeywordSetHash` = SHA-256 of the sorted, normalised keyword values from `LitigationKeywordPlanner`. A
    materially different keyword set (e.g. new historical names after a re-ingest) is a genuinely different
    purchase; an identical set is not.
  - The scope is deliberately **not** client-scoped. Any two requests for the same company and keyword set —
    same client or different clients — share one scope and cannot buy twice inside the window. (This replaces
    the round-2 client-scoped design; it is a conscious relaxation of the client data boundary for litigation
    data only — the order PDFs and case data are public court records and carry nothing client-specific.
    Provenance is always recorded, §4.2a.)
  - What an admission outcome means for a request:
    - **Admitted** → this request buys the search.
    - **Denied `FRESH`** → an already-purchased report exists inside the 7 days: **reuse it** (§4.2a), no spend.
    - **Denied `IN_FLIGHT`** by another request → this request's stage is `Waiting(LITIGATION_JOINING_ACTIVE)`;
      each tick re-evaluates, and when the source completes the result is `FRESH` → reuse. If the source ends in a
      failure after registration → `NeedsAttention(LITIGATION_SOURCE_UNUSABLE)` (a human may re-run: a `Manual`
      admission).
    - **Window elapsed (> 7 days)** → admitted as a new purchase.
  - Billing attribution is out of scope, but every reuse is written to `PipelineEvents` with the requesting
    `ClientId`/`RequestId` and the source ids, so a later billing feature has the data ("every client pays even
    when the report is reused", per the user-flow spec).
- Preconditions (advisory in `PipelineDecider`, **enforced by §4.0**): `Ingest` succeeded; litigation API
  configured; policy `AutoLitigationSearch` on; freshness window `Pipeline:LitigationFreshnessDays` (**7**, per
  the owner decision; it also matches the ~7-day life of the vendor's order-PDF URLs); daily cap. Reuse (§4.2a)
  needs no cap slot and no purchase.
- Not configured / disabled ⇒ `Skipped(LITIGATION_NOT_CONFIGURED | LITIGATION_POLICY_OFF)` — never `Failed`.
- Provenance: `AppCustomerId` stays `RequestNumber`; the event log records `Actor=system`.

### 4.2 Litigation AI analysis auto-start

- Trigger when, for the latest snapshot: search job `Completed`, case persistence done, every order document
  is terminal (`Downloaded | Failed | Expired`), and order chunking is terminal.
- **Snapshot linkage — schema change that must merge before auto-analysis can be enabled.** Verified: today
  `LitigationAiAnalysisRun` has neither. Add `TriggerSnapshotId` (nullable FK → this request's
  `LitigationReportSnapshot`), `OriginSnapshotId` (lineage: the originally *purchased* snapshot — equal to
  `TriggerSnapshotId` unless the snapshot was itself reused, §4.2a; both columns set
  on every new run, null only on legacy rows) and `Trigger` (`Auto`|`Manual`|`Reused`, default `Manual` for
  existing rows), plus a **filtered unique index `(OriginSnapshotId) WHERE Trigger = 'Auto'`** — keyed on the
  origin so that N requests sharing one purchased report still yield at most one paid auto analysis. The database — not the
  decider — guarantees at most one auto run per snapshot; the existing unique-active-per-request index stays.
  `CreateOrJoinAsync(requestId, trigger, triggerSnapshotId)` treats a unique-violation on the new index as
  "already exists → join".
- Admission: `ScopeKey = analysis|{OriginSnapshotId}`, **no freshness window** (a committed scope is never
  auto-admitted again), daily cap per §4.0. The ledger gives the cap and accounting; the index gives the
  invariant — both must hold (defence in depth).
- A terminal `Failed` auto run does **not** get a second auto run (the unique index would reject it, on purpose).
  The stage goes to `NeedsAttention(RETRIES_EXHAUSTED)`; a human *Retry* creates a `Manual` run (recorded,
  counted, not cap-blocked). The run's own internal retries (`FailOrRetry`) are unchanged.
- `TriggerSnapshotId` records **what triggered the run** (the newest fully-imported snapshot at admission), not
  an evidence boundary: the analysis reads all of the request's cases, and each case row's `EvidenceHash`
  already records exactly what evidence was sent. A later snapshot with new cases legitimately triggers a new
  auto run and a new admission.
- The 7-day order-document expiry means the chain must not stall between persistence and download. The
  coordinator flags `Litigation` `NeedsAttention(ORDER_DOWNLOAD_STALLED)` if any order document is still
  `Pending` at day 5.
- Policy `AutoLitigationAnalysis` is separate from `AutoLitigationSearch` (different vendor, different cost).
- **To confirm before coding:** the exact terminal-state definition for `LitigationOrderChunk` (the trigger
  condition above depends on it). The snapshot-reference question is resolved: it is absent and is added above.

### 4.2a Reuse of an already-analysed litigation report (owner decision, §13)

When another request — any client — already holds a litigation report for the same scope (§4.1), this request
**takes that already-analysed report instead of buying and analysing again**, provided it is within the
one-week timeline.

- **Source selection.** The most recent request with, for the same `search|{CanonicalIdentifier}|{KeywordSetHash}`
  scope: a `Completed` snapshot whose retrieval timestamp is ≤ 7 days before *now*, and — when the source has
  one — a `Completed` (or `CompletedWithErrors`) AI analysis run. Older than 7 days ⇒ not reusable ⇒ a fresh
  purchase (§4.1).
- **Mechanism: copy-on-reuse, with provenance.** A single transaction copies the source's snapshot, cases, case
  matches, orders, order documents, order chunks and — if present — the AI run with its case and portfolio
  analyses into the new request. The new rows keep existing UI/queries request-scoped and unchanged. Provenance
  columns on the copied snapshot and run: `ReusedFromRequestId`, `ReusedFromSnapshotId`, `OriginSnapshotId`,
  `RetrievedUtc` (the *source's* retrieval time — never reset). The copied AI run has `Trigger=Reused`,
  status copied, **no spend**, `EvidenceHash`/`PromptHash`/`ResponseHash` carried byte-for-byte so its
  provenance is verifiable against the original.
- **Order documents** already `Downloaded` are copied (file copy; a content-addressed shared store is deferred,
  §10). Documents `Failed`/`Expired` in the source stay so in the copy — the vendor URLs are past their life, so
  reuse never re-attempts downloads.
- **Source has a search but no analysis yet** (analysis policy off, or in flight): the search is reused; the
  analysis stage proceeds under §4.2 with `analysis|{OriginSnapshotId}` — so if the source's analysis is in
  flight the joiner sees `IN_FLIGHT` → `Waiting` → then copies the result; if none is running, exactly one
  request (the first admitted) pays for it.
- **Visible to the reader.** The Litigation tab and any export show "Report retrieved <date> — reused from
  another request", because the data can be up to 7 days old. The 7-day bound is a hard reuse limit, not a
  staleness guarantee, and this must be disclosed to the analyst.
- **Idempotency.** Copy is guarded by `UNIQUE(RequestId, OriginSnapshotId)` on the copied snapshot, so a
  retried or racing reuse is a no-op join.
- **Per-client settings still apply at render time** (`IncludeLitigationInDossier` etc.); nothing
  client-specific is stored in the shared/copies.

### 4.3 Dossier pre-render

- Extract render + atomic write + cache path from `DossierController.Download` into
  `DossierArtifactService.EnsureRenderedAsync(requestId, flavour)`; controller and coordinator share it, so the
  on-disk key `(flavour, ingestionRunId, analysisRunId)` is unchanged and existing PDFs stay valid.
- Runs only after `CalcAssurance` is `Succeeded*`. Respects `IsHeldAsync` exactly as the controller does
  (held ⇒ no file served, stage `NeedsAttention(CALC_GATE_HOLD)`).
- Client setting `IncludeLitigationInDossier` is *already* applied at render time via the model; the existing
  `InvalidateForRequestAsync` path stays the invalidation mechanism.
- Failure (renderer exception, disk full) is `Transient` ⇒ retry per §6; a deterministic renderer failure after
  the retry cap ⇒ `NeedsAttention(DOSSIER_RENDER_FAILED)`.

### 4.4 Resume after human resolution

When a reviewer clears a manual-review flag or attaches a corrected source (`AddSourceWorkbooks` already
enqueues analysis), the next tick sees the new facts and continues. No coordinator-specific "resume" endpoint is
needed; the *Resolve* button on the board just deep-links to the existing flow.

### 4.5 Explicitly not in stage 2

Re-analysis after filings (see §0); putting litigation into the dossier PDF (a product decision, not an
automation one — deferred, §9).

---

## 5. Stage 3 — Unattended reference-tool session

Requirement: with `ReferenceTool:Username/Password` configured, no job ever fails because a cookie expired, and
when the tool is genuinely unreachable or the credentials are wrong the system **pauses and says so** instead of
failing every request.

### 5.1 Shared session state

- New singleton `ReferenceToolSession` holds cookie, user id, `AcquiredUtc`, `Generation` (int) and the
  single-flight login gate. `ReferenceToolClient` (transient) reads/writes it. Fixes today's per-instance
  cookie and per-instance gate. Two concurrent jobs then share one login.
- Seed from `ReferenceTool:SessionCookie` if present; otherwise log in lazily.

### 5.2 Re-login on session loss, everywhere

- Add a `SessionRecoveryHandler : DelegatingHandler` in the typed client's pipeline. On a 401/403, **or** on the
  "logged-out" signatures the client already recognises (200 + non-JSON/HTML body, `error` field), it:
  acquires the single-flight gate, re-logs-in **only if `Generation` has not already advanced** (so N parallel
  downloads that all fail cause one login), re-stamps the `Cookie` header and replays the request **once**.
- Non-replayable requests (streamed bodies) are not used by this client (all GET / form POST), so replay is
  safe. Downloads are idempotent GETs.
- Bounded: at most one re-login per request, and a global login-rate limit
  (`ReferenceTool:MaxLoginsPerHour`, default 6) to protect the account from a login loop.

### 5.3 Typed failure kinds

Replace message-sniffing with `ReferenceToolFailureKind` on `ReferenceToolException`:
`AuthRejected` (bad credentials — **permanent, do not retry**), `SessionExpired` (recoverable),
`CompanyLocked` (not unlocked — approval-gated, §5.7), `RateLimited`, `Unavailable` (5xx/timeout/DNS),
`ContractChanged` (unexpected shape — needs a developer), `Other`. `Retryable` is derived from the kind. The
existing 200-with-HTML "locked or expired" ambiguity is resolved by attempting one re-login: if the same
response returns after a fresh login it is `CompanyLocked`, otherwise it was `SessionExpired`.

### 5.4 Health and circuit breaker

- Table `IntegrationHealth` (`Name` = `ReferenceTool` | `LitigationApi` | `Vertex`, `State` =
  `Healthy|Degraded|Open`, `ConsecutiveFailures`, `LastSuccessUtc`, `LastError`, `OpenedUtc`, `NextProbeUtc`).
  Persisted so it survives restarts and is visible to both workers and the UI.
- `ReferenceToolHealthProbe` (hosted, every `ReferenceTool:HealthProbeMinutes`, default 10): cheap
  `getUserDetails` call through the recovery handler. Success closes the breaker; `AuthRejected` opens it
  immediately (no retries — protects the account from lockout); `Unavailable` opens after
  `Pipeline:BreakerThreshold` (default 3) consecutive failures.
- **Breaker open ⇒ pause, never strand.** Exact atomicity between "check the breaker" and "claim the job" is not
  achievable across two tables and is not what matters. The invariant to guarantee is: *no job is ever (a)
  `Queued` in the database but absent from the in-memory channel, or (b) `Failed`/`ExtractionFailed` because a
  breaker was open, without a scheduled retry.* Four mechanisms:
  1. `AutoFetchWorker` awaits the breaker gate **before dequeuing**, so an item is never taken off the channel
     while the breaker is open (nothing to lose).
  2. The claim statement carries the predicate itself — `… WHERE Status = Queued AND NOT EXISTS (open
     ReferenceTool breaker)` in the same `ExecuteUpdate`, one statement, one snapshot. If it claims 0 rows and
     the job is still `Queued`, the worker re-schedules it (re-enqueue after `NextProbeUtc`); it never drops it.
  3. A job claimed a moment *before* the breaker opened fails with a breaker-class kind. The failure handler
     does **not** write `ExtractionFailed` or a terminal `Failed`; it leaves the job retryable with checkpoints
     (existing `RequeueAsync` semantics) and the coordinator sets the stage `RetryScheduled(TOOL_UNAVAILABLE)`
     **without consuming an attempt**, re-queuing when the breaker closes.
  4. A periodic sweep (`AutoFetch:RequeueSweepMinutes`, default 2) re-enqueues any `Queued` job older than the
     interval while the breaker is closed. Duplicate enqueues are harmless (atomic claim). This repairs *any*
     lost enqueue, not only breaker-related ones, and ships in PR A because it needs no coordinator.
- Half-open: after `NextProbeUtc`, one probe; success drains the queue in order.
- The same table is written by the litigation client (`AuthRejected`/`Unavailable`) so §4 auto-starts pause
  when the litigation API is down rather than creating jobs that fail. A paused auto-start does **not** hold an
  admission: the breaker check happens *before* `TryAdmitAsync`, and an admission taken just before the breaker
  opened is `Released` by the §4.0 sweep if no registration was attempted.
- **`IntegrationHealth` writes are single-statement atomic, never read-modify-write.** Probes, `AutoFetchWorker`,
  the litigation client and the coordinator all update the same row concurrently.
  - *Failure:* one parameterised statement — `UPDATE … SET ConsecutiveFailures = ConsecutiveFailures + 1,
    State = CASE WHEN @authRejected OR ConsecutiveFailures + 1 >= @threshold THEN 'Open' ELSE 'Degraded' END,
    OpenedUtc = CASE WHEN State <> 'Open' THEN @now ELSE OpenedUtc END,
    LastTransitionUtc = CASE WHEN <state changes> THEN @now ELSE LastTransitionUtc END`. Raw SQL, because EF
    `ExecuteUpdate` cannot express the `CASE`.
  - *Success:* resets `ConsecutiveFailures` only from `Healthy|Degraded`. An `Open` breaker is closed **only**
    by the probe — an in-flight call that happened to succeed does not close it.
  - *Half-open:* the probe first claims with `UPDATE … SET NextProbeUtc = @now + @probeLease WHERE State='Open'
    AND NextProbeUtc <= @now` — one winner, so concurrent probes cannot stampede the tool.
  - *Stale-result fencing:* every call records its start time and passes it in; a failure whose call **started
    before `LastTransitionUtc`** is ignored, so a slow request that failed under old conditions cannot re-open a
    breaker a probe just closed. The same rule protects a success report from re-closing a newly opened one.
  - `Name` is unique; `RowVersion` exists for UI reads only — correctness comes from statement semantics.

### 5.5 Surfacing without stage-6 notifications

- `IPipelineAlertSink` with a single implementation now (structured log with a fixed event id per reason code +
  a banner on `/Pipeline` and the Details page). Stage 6 later adds email/webhook sinks without touching callers.
- ASP.NET health-check endpoint `/health` exposing breaker state, oldest queued job age, and count of
  `NeedsAttention` runs — pollable by any external monitor.

### 5.6 Operational requirements (documented, not code)

- Dedicated service account for the tool, credentials in user-secrets/environment only (extend README
  "Configuring secrets"; never committed, never logged; no vendor name in docs).
- **Verify empirically before relying on it:** whether the tool allows concurrent sessions per account (a second
  login may invalidate the first). If it does invalidate, the shared singleton + single-flight gate is required,
  not optional, and `AutoFetchWorker.MaxConcurrentJobs=2` is safe only because both share one session.

### 5.7 Reference-report lifecycle: unlock and refresh

Source: the MCA-ROC user-flow spec (report state machine, 24 h refresh rule, 4–36 h wait, fixed 12-month unlock
window, "never store pre-refresh data"). Assumption: the spec governs the reference-tool workflow that
auto-fetch drives (the spec's "crawler clicks Refresh on portal").

### 5.7.0 Now captured: the real endpoint contract

[reference-tool-refresh-unlock-contract.md](reference-tool-refresh-unlock-contract.md) — captured and verified
live against production on 2026-09-22 (one real, owner-approved 1-credit unlock; a live refresh cycle; a
locked, an unlocked, and an expired company all observed). This replaces every "unverified"/"probe the tool's
status" hand-wave below with a real, named contract:

| Need | Action | Cost | Contract |
|---|---|---|---|
| Is it unlocked, since when | `getAssetTeams(bid)` | 0 | `teams[0].addedAt` — an ISO timestamp if unlocked, `null` if locked **or** expired (expired is indistinguishable from never-unlocked at this call; §5.7 already computes `Expired` itself from `UnlockedUtc + 12mo`, so this is consistent, not a gap) |
| Free identity check before spending | `getCompanyPreview(bid)` | 0 | Returns legal name, CIN, address, capital on **any** company, locked or not — this is the free pre-unlock identity cross-check §5A.3 asked for; wire it in there too |
| MCA-side maintenance advisory | `getUpgradeStatusForUnlockingAsset` / `getUpgradeStatusForCompanies` | 0 | Not in the original brief — the tool's own client checks this before unlock and before refresh. `mca_status != "NORMAL"` ⇒ treat as `Transient` (§6.1) and defer, not `NeedsAttention`: it reflects the government MCA portal's own maintenance window, which self-resolves |
| Spend the unlock | `addAsset(teamId, bid, legalName)` | **1 credit** | `teamId` comes from the same `getAssetTeams` response (present even when locked). `statusCode: true` ⇒ `getAssetTeams` immediately reflects the new `addedAt` — no separate confirmation poll needed |
| Trigger a refresh | `requestProbeDataUpdate(bid, userName)` | 0 | Confirmed free regardless of how often called — no cost reason to under-trigger it |
| Poll a refresh | `getDataEntryRequestStatus(bid)` | 0 | `"REQUESTED"` while running, `"NO PENDING REQUEST"` when done — this **is** `CompanyRefreshPolicy`'s `JoinActiveRefresh`/`ReuseFreshSnapshot` distinction made concrete |
| Detailed progress (optional, for the board) | `probeRequestService.php?action=getStatus(id)` | 0 | 6 named stages (`DataEntryRequest → DocCollected → FESGroup → CESGroup → DataCurationGroup → DataDistributionGroup`) — richer than the board needs today, but free to surface as a progress bar |
| Freshness / "is a refresh needed" | `getDataStatus(bid)` | 0 | `downloaded`/`qa.date` is the 24h clock `CompanyRefreshPolicy.Decide` reads |

Answers two open questions outright: **§9 #13 (is a refresh billable) — no, confirmed 0 credits, live, regardless
of frequency.** And the unlock window rule is confirmed exactly as specified: `validTill = addedAt + 1 year - 1
day`, and a refresh never touches `addedAt`/`validTill` — `CompanyRefreshPolicy`'s `Expired` extension (§5.7
below) needs no change.

- **Identity gate — never spend on an uncertain company (owner instruction, round 4).** `Unlock` requires
  `Resolve` to have `Succeeded` (§3.3): a request whose identity is `Ambiguous` or `NotFound` stays
  `Waiting` on `Resolve`'s own `NeedsAttention(IDENTITY_AMBIGUOUS | IDENTITY_NOT_FOUND)` and **never reaches the
  `Unlock` stage at all** — there is no path from an unresolved name straight to a spend. This is stricter than
  the §5A.3 trust ladder (which further limits *which resolved identities* may spend unattended): the ladder
  governs confidence once resolved, this gate governs whether it is resolved at all, and both must pass. Only
  requests with a certain, confirmed CIN/LLPIN ever reach `Unlock`; everything else is a human's decision on the
  `/Pipeline` board's ambiguity queue (§5A.3), not the automation's.
- **Lifecycle table** `CompanyReportLifecycles` — one row per canonical identifier, **global** (the unlocked
  report is shared underneath, per the spec): `State` (`NeverRequested|Locked|Unlocking|Unlocked|Refreshing|
  RefreshPending|RefreshFailed|Expired`), `UnlockedUtc` (original unlock date, immutable until a re-unlock),
  `LastRefreshRequestedUtc`, `LastRefreshCompletedUtc`, `ActiveRefreshId`, `RefreshDeadlineUtc` (requested + 36 h),
  `RowVersion`. It feeds `CompanyRefreshState`, so **`CompanyRefreshPolicy.Decide` is wired in as written**
  (`Locked | ReuseFreshSnapshot | JoinActiveRefresh | StartRefresh`), extended with `Expired`
  (`now > UnlockedUtc + 12 months`, strictly from the original date; a refresh never extends it).
- **Refresh start is a one-winner claim:** `UPDATE … SET ActiveRefreshId=@new WHERE Identifier=@i AND
  ActiveRefreshId IS NULL AND (LastRefreshCompletedUtc IS NULL OR LastRefreshCompletedUtc < @now-24h)`. Other
  requests for the same company **join** the pending refresh instead of triggering another (spec).
- **Parked, never blocking.** A job waiting for an approval or a refresh must not hold one of
  `AutoFetchWorker`'s 2 slots for up to 36 h. New job statuses `WaitingForApproval` and `WaitingForRefresh`:
  the job releases its slot; the reconciler polls lifecycle state every `Refresh:PollMinutes` (default 10) and
  re-queues the job when the gate opens. Startup recovery already re-queues non-terminal jobs and simply
  re-evaluates the gate first.
- **"No stale data" gate:** `Fetch` exports the workbooks only when `LastRefreshCompletedUtc` is within 24 h.
  Nothing is exported, downloaded or stored before that. When another request's refresh completes, every joined
  request proceeds. (This closes the pre-existing gap in §1: today the export runs immediately.)
- **Timeout:** past `RefreshDeadlineUtc` the lifecycle becomes `RefreshFailed` ⇒ `NeedsAttention(REFRESH_TIMEOUT)`
  + alert; a retry (board action, or one automatic retry) moves it back to `Refreshing`.
- **Unlock — approval-gated today:**
  1. *Detect.* Lifecycle `Locked`/`Expired`, or a typed `CompanyLocked` from the client (§5.3). Call
     `getAssetTeams(bid)` first: if `teams[0].addedAt != null`, the company is **already unlocked by anyone** —
     **adopt it with no spend** (`Unlocked`, `UnlockedUtc = addedAt`) — the spec's "reuse the unlocked report".
     Only `addedAt == null` proceeds to the alert below.
  2. *Alert.* Otherwise stage `Unlock` = `NeedsAttention(UNLOCK_APPROVAL_REQUIRED | UNLOCK_EXPIRED)` and an alert
     via `IPipelineAlertSink`. The approval screen shows the identity the CIN came from (method, score, §5A),
     company name/state/status, and the cost note.
  3. *Approve.* `POST /Pipeline/{runId}/unlock/approve` (InternalReviewer, antiforgery, mandatory reason) inserts
     `UnlockApprovals` (`ApprovalId`, `Identifier`, `RequestId`, `ApprovedBy`, `ApprovedUtc`, `ExpiresUtc` =
     +24 h, `ConsumedAdmissionId` null): single-use, bound to one company, expiring.
  4. *Execute.* The reconciler sees an unexpired, unconsumed approval and calls `TryAdmitAsync(ReferenceUnlock,
     unlock|{id}, Manual)`. **Inside the same transaction:** `UPDATE UnlockApprovals SET ConsumedAdmissionId=@a
     WHERE ApprovalId=@x AND ConsumedAdmissionId IS NULL AND ExpiresUtc > @now` must change exactly 1 row, else
     the transaction rolls back — two reconcilers cannot both spend on one approval. The admission is
     `Committed` once the unlock call has been attempted (fail closed); it sets `UnlockedUtc`, and the lifecycle
     goes `Unlocking → Unlocked → Refreshing` (spec: unlock is followed by a refresh).
  5. *Shared outcome.* Scope is company-level, so one approval unlocks for every request waiting on that
     company; the ledger admits it once. An approval that expires unused returns the stage to
     `UNLOCK_APPROVAL_REQUIRED` and re-alerts.
- **Future auto-unlock (built, disabled).** `Pipeline:AutoUnlock:Enabled` (default **false**), `Caps:UnlockPerDay`,
  optional `Pipeline:AutoUnlock:ClientIds` allow-list (e.g. internal cost-centre users). It is the same admission
  path with `Trigger=Auto` and no approval row, and it additionally requires a confident identity (§5A.3 trust
  ladder). Enabling later is configuration; it is covered by the same tests as the approved path.
- **Spec items not built here:** the free pre-login public view, per-client credit/wallet deduction and the
  product-subscription matrix have no counterpart in this portal. The events in §4.1 keep the data a later
  billing feature would need.
- **Captured — PR R is unblocked.** Every call this stage needs is now named, verified live, and redaction-clean
  in [reference-tool-refresh-unlock-contract.md](reference-tool-refresh-unlock-contract.md) (§5.7.0). No further
  vendor capture is needed to start PR R.

## 5A. Identity resolution — target name → CIN/LLPIN (added round 3)

Requirement: when someone supplies a target **name** (possibly ambiguous), the system resolves it against our own
company master data and selects the correct CIN, and **does not guess when it cannot be sure**. This is a money
problem as much as a UX one: the CIN drives the paid unlock (§5.7), the reference-tool fetch and the litigation
keyword search (§4.1). A wrong CIN buys the wrong company's data and produces a wrong-company dossier that looks
entirely legitimate.

### 5A.1 Data and current gap

`CompanyMasterRecords` (~3.7M rows) already carries `Identifier`, `RecordType` (Company/Llp/Fcrn), `Name`,
`RegistrationDate`, `Status`, `State`, `District`, `PinCode`, `Roc`, `Category`, `Class`, `ListingStatus`, capital
and `Address` — enough to disambiguate. Today's lookup (`AutoFetchController.SearchLocalMasterDataAsync`) is
`Name.StartsWith(query)` over an index on `(RecordType, Name)`: exact/prefix only, alphabetical, no ranking, no
ambiguity signal. "ABC Pvt Ltd" will not find "ABC PRIVATE LIMITED"; word order and typos fail. Two caveats:
former names are not in the master (only `CompanyNameHistories` for already-ingested companies), and
`CompanyMasterSync` is off by default, so the master may be stale (a new incorporation can be absent).

### 5A.2 Resolver (`CompanyNameResolver`: pure ranking + thin I/O)

1. **Normalise (pure, table-tested).** Case/Unicode fold, punctuation, `&`→`AND`, `PVT`→`PRIVATE`,
   `LTD`→`LIMITED`, `CO`→`COMPANY`. Produce `NameNormalized`, `NameCore` (legal suffix stripped) and a parsed
   `EntityForm` (Private/Public/OPC/LLP/…). Stored as **indexed columns** on `CompanyMasterRecords` — backfilled,
   and maintained by the import tool and the sync worker — so lookups are seeks, not scans of 3.7M rows.
2. **Retrieve ≤ 50 candidates:** exact `NameNormalized` → exact `NameCore` → core prefix → token overlap. Token
   retrieval uses SQL Server Full-Text if the deployment SKU has it (**unverified for SQLEXPRESS**), otherwise an
   inverted `CompanyNameTokens` table built at import. No fuzzy work in SQL.
3. **Rank in memory.** Features: normalised-exact, core similarity (token-set + edit distance), entity-form match,
   status (Active preferred; struck-off / amalgamated / under-liquidation **penalised, not excluded** — the
   requester may genuinely want one), and any supplied hints (state, district/pincode, incorporation year, PAN,
   listed flag). Score and per-feature breakdown are kept per candidate.
4. **Decide (pure, versioned `AlgorithmVersion`).**
   - `Resolved(AutoSelected)` only if **all** hold: top score ≥ `T_high`; margin over the runner-up ≥ `M`; the top
     is an exact normalised match or hint-confirmed; it is not a tool-only hit.
   - Exact duplicates (same normalised name) with no distinguishing hint are **always** `Ambiguous`. One
     exception, policy-gated (`Resolve:AllowSoleActive`): exactly one Active among otherwise inactive twins
     (`RESOLVED_SOLE_ACTIVE`, alternatives recorded).
   - `Ambiguous` → ranked candidates, no choice made. `NotFound` → suggestions, plus the option to search the
     reference tool live; tool-only hits always need human confirmation.
   - A supplied CIN/LLPIN short-circuits: pattern-validate and confirm it exists in the master
     (`Method=UserProvidedCin`).
5. **Persist `IdentityResolutions`** (audit and reproducibility, like `RuleEngineVersion`): input name and hints,
   normalised input, `Status`, `Method` (`UserProvidedCin | AutoSelected | HumanSelected`), chosen identifier,
   score, margin, top-N candidates with features, `AlgorithmVersion`, thresholds used, `MasterSnapshotDate`,
   who/when. The request receives `Cin`/`Llpin`/`AutoFetchCompanyIdentifier`/`EntityType` **only** when resolved,
   in one transaction. A collision with the existing client-scoped unique index becomes
   `NeedsAttention(DUPLICATE_REQUEST)` pointing at the existing request, not a thrown exception.

### 5A.3 Coordinator and intake integration

- `Resolve` is the first stage (§3.3). Board action **Select this CIN** on an ambiguous item (InternalReviewer,
  audited, sets `Method=HumanSelected`) over a candidate table: name, CIN, state, district, registration date,
  status, category, listed, similarity, reasons.
- **Intake:** `AutoFetchController.New` accepts a name-only submission plus optional hints; the interactive
  search endpoint uses the same resolver and returns ranked candidates with disambiguators instead of
  alphabetical prefix hits. A user picking a hit is `HumanSelected`.
- **Trust ladder for spend.** Today the unlock is human-approved and the approval screen shows how the company was
  identified (§5.7). The future `AutoUnlock` and auto litigation search require `Method ∈ {UserProvidedCin,
  HumanSelected}` or an `AutoSelected` resolution above a stricter `T_spend`; a lower-confidence auto-selection
  may proceed only through the free steps.
- **Safety nets after selection stay, and one is now concrete:** the name cross-check against the tool's own
  search hit (existing), plus — now that §5.7.0 has captured it — a **free** `getCompanyPreview(bid)` call
  (0 credits, works on a locked company too) as the last check immediately before `Resolve` hands off to
  `Unlock`, comparing its returned CIN against the resolved one. Then the post-ingestion identity check
  (`IDENTITY_MISMATCH`). A mismatch on an `AutoSelected` resolution is recorded as a **false accept**, reopens
  `Resolve` as `NeedsAttention`, and feeds the metrics.

### 5A.4 Calibration before auto-select may be enabled

A wrong accept is the costly error, so auto-select ships **disabled** (`Resolve:AutoSelect:Enabled=false`: the
system suggests, a human confirms). An offline harness (PR I1, no behaviour change) replays a labelled set —
existing requests where the requester's name and the final confirmed CIN are both known, plus a synthetic set
(suffix variants, word order, typos, LLP vs company, same-name duplicates, struck-off twins) — and reports
precision and recall per threshold. Auto-select may be enabled only when precision on the auto-selected subset
meets the target (proposed ≥ 99.5%, reported with a confidence interval and by category); `T_high`, `M` and
`T_spend` are set from that data, not guessed. Live metrics afterwards: auto-select rate, human-override rate,
false-accept count.

### 5A.5 Deferred within this feature

Alias learning from human choices (as a ranking hint only, never an auto-select), phonetic/transliteration
matching for regional spellings, director-name disambiguation, and GSTIN/PAN→CIN mapping beyond use as a hint.

---

## 6. Stage 4 — Exception handling

### 6.1 Failure taxonomy (`PipelineFailureClassifier`, pure)

| Class | Examples | Coordinator response |
|---|---|---|
| `Transient` | tool 5xx/timeout, `RateLimited`, Vertex 429/5xx, disk-full-then-freed, renderer crash | auto-retry with backoff |
| `Recoverable-auth` | `SessionExpired` | handled inside the session layer; never reaches the coordinator unless re-login itself fails |
| `NeedsHuman-data` | identity mismatch (manual review), workbook parse/validation failure, calc-gate hold, wrong entity type | `NeedsAttention(code)`, no retry |
| `Approval-gated` | company locked or unlock window expired (`UNLOCK_APPROVAL_REQUIRED`, `UNLOCK_EXPIRED`) | `NeedsAttention` + alert; proceeds only on an explicit single-use approval (§5.7) |
| `Ambiguous-identity` | name matches several plausible companies or none (`IDENTITY_AMBIGUOUS`, `IDENTITY_NOT_FOUND`) | `NeedsAttention` with ranked candidates; a human selects the CIN (§5A) |
| `NeedsHuman-config` | `AuthRejected`, integration not configured (when policy demands it) | breaker opens / `NeedsAttention`; no retry |
| `NeedsDeveloper` | `ContractChanged`, unexpected exception type | `NeedsAttention` after 1 retry, event logged with exception type |

Stable reason codes (initial set): `IDENTITY_MISMATCH`, `INGEST_FAILED`, `UNLOCK_APPROVAL_REQUIRED`, `UNLOCK_EXPIRED`, `REFRESH_TIMEOUT`, `MCA_MAINTENANCE` (from `getUpgradeStatusForUnlockingAsset`/`getUpgradeStatusForCompanies` ≠ `NORMAL` — `Transient`, deferred not alerted, §5.7.0), `IDENTITY_AMBIGUOUS`,
`IDENTITY_NOT_FOUND`, `DUPLICATE_REQUEST`, `LITIGATION_JOINING_ACTIVE`, `LITIGATION_SOURCE_UNUSABLE`, `AUTH_REJECTED`,
`TOOL_UNAVAILABLE`, `CALC_GATE_HOLD`, `DOSSIER_RENDER_FAILED`, `RETRIES_EXHAUSTED`, `STAGE_STALLED`,
`ORDER_DOWNLOAD_STALLED`, `CONTRACT_CHANGED`, `MANUAL_SOURCE`, `NO_FILINGS_REQUESTED`,
`LITIGATION_NOT_CONFIGURED`, `LITIGATION_POLICY_OFF`.

### 6.2 Auto-retry with backoff

- Per-stage `Attempts` and `NextAttemptUtc` in `PipelineStageStates`. Schedule: 2 min, 10 min, 30 min, 2 h, each
  ±20% jitter; cap `Pipeline:MaxCoordinatorAttempts` (default 4) ⇒ `NeedsAttention(RETRIES_EXHAUSTED)`.
- **No retry multiplication.** Stages already retry internally (`LitigationAiAnalysis` `FailOrRetry`,
  download attempts, litigation `MaxAttempts`). The coordinator retries only a stage whose *own* retries are
  exhausted (terminal `Failed`), and counts internal attempts toward the cap, so the worst case is bounded and
  a paid call is not repeated 3×4 times.
- Retry entry points (all existing, all idempotent): `AutoFetchJobService.RequeueAsync` (keeps checkpoints),
  analysis reset-to-`DataExtracted` + enqueue (extract the body of `RecoverStaleWorkAsync` into a per-request
  `RetryAsync`), `DossierArtifactService.EnsureRenderedAsync`. **Paid stages are excluded from automatic
  coordinator retry:** litigation search and litigation AI analysis are retried only by a human (a `Manual`
  admission, §4.0). Their *pre-purchase* failures (auth, breaker, cap) are deferrals that re-run the admission,
  which is safe because nothing was bought; a failure *after* purchase goes straight to `NeedsAttention`.
- A breaker-open integration defers retries (state `RetryScheduled`, reason `TOOL_UNAVAILABLE`) without
  consuming an attempt.

### 6.3 Stall detection

- Per-stage staleness thresholds (options): fetch/filing-download use `AutoFetchJob.HeartbeatUtc` (default 20
  min — downloads flush progress), analysis uses `AnalysisRun.StartedDate` (default 15 min), litigation and AI
  analysis use lease expiry.
- **Parked states are not stalls.** `WaitingForApproval`, `WaitingForRefresh` and `Waiting(LITIGATION_JOINING_ACTIVE)`
  are exempt from heartbeat staleness; each has its own deadline instead — refresh 36 h (`REFRESH_TIMEOUT`),
  joining-a-purchase `Pipeline:JoinTimeoutMinutes` (default 60, then `LITIGATION_SOURCE_UNUSABLE`), approval
  none (it waits for a human, but re-alerts every `Pipeline:ApprovalReminderHours`, default 24).
- **Runtime stall ⇒ alert + `NeedsAttention(STAGE_STALLED)` + a manual *Restart stage* button, not an
  automatic restart.** An in-process worker may still be alive and slow; restarting it would double-execute
  (and the `AutoFetchJob` claim only matches `Queued`). Automatic restart happens only where the stage owns a
  fenced lease (litigation AI analysis) and at process startup, which is what the existing recovery sweeps
  already do. Automatic runtime restart of unfenced stages is deferred (§9).

### 6.4 Manual-review queue

- The `/Pipeline` board's *Needs attention* view lists runs by reason code with age, request, client and the
  stage's `ReasonDetail`. Row actions: **Open** (deep-link to the existing resolution flow — Details, Attach
  source, Reingest), **Retry stage**, **Skip stage** (only for enrichment stages; core stages cannot be
  skipped), **Cancel pipeline**. Every action requires a reason and is audited.
- `IDENTITY_MISMATCH` today only appends a job warning; the coordinator promotes it to a first-class attention
  item with the mismatch detail from `ManualReviewReason`.

### 6.5 Cost and safety rails

- Paid-call daily caps, dedup and once-only rules are enforced by the §4.0 admission transaction (never by the
  decider alone). A cap denial puts the stage in `RetryScheduled(COST_CAP_REACHED)` until the next `DayKey`,
  consuming no attempt.
- `Pipeline:MaxConcurrentRuns` bounds how many requests are actively enriched at once (protects Vertex quota and
  the reference tool account).
- Kill switch: `Pipeline:Mode=Observe` (or `Enabled=false`) takes effect on the next tick without a deploy
  (options reload) and leaves all state intact.

---

## 7. Delivery plan

| PR | Content | Depends on | Behaviour change |
|---|---|---|---|
| **A** (stage 3, [#263](https://github.com/codepaha/MCAROC_Analysis/issues/263)) | `ReferenceToolSession` singleton, `SessionRecoveryHandler`, typed failure kinds, `IntegrationHealth` (atomic statements, stale-result fencing) + probe + breaker, `/health`, worker pause (gate before dequeue, predicate in the claim), breaker-class failures left retryable, `Queued` re-enqueue sweep, suppress `ExtractionFailed` on breaker failures | — | Yes, but strictly fewer failures |
| **B** (stage 1, [#264](https://github.com/codepaha/MCAROC_Analysis/issues/264)) | Entities + migration, `PipelineDecider` + reader, Observe-mode reconciler, adopter, status endpoint, Details strip, `/Pipeline` board (read-only) | — (parallel with A) | None (observe only) |
| **C1** (stage 2, schema, [#265](https://github.com/codepaha/MCAROC_Analysis/issues/265)) | Paid-call admission (`SpendScopes`, `SpendCounters`, `PaidCallAdmissions`, `TryAdmitAsync`, resolution sweep); `LitigationAiAnalysisRun.TriggerSnapshotId` + `Trigger` + filtered unique index; manual buttons routed through admission | — (can start with A/B) | Manual paid calls become ledgered; no auto behaviour |
| **C2** (stage 2, triggers) | `DossierArtifactService`, `LitigationAutoStartService`, AI-analysis auto-start, executor actions behind per-family Enforce flags | B, **C1 (hard)** | Yes, flag-gated |
| **R** (stage 1/3, [#229](https://github.com/codepaha/MCAROC_Analysis/issues/229) + [#266](https://github.com/codepaha/MCAROC_Analysis/issues/266)) | `CompanyReportLifecycles`, `UnlockApprovals`, `ReferenceUnlock` admission kind, wire in `CompanyRefreshPolicy`, refresh claim/join/poll, parked job statuses, "no stale data" gate before export, unlock approval endpoint + alert; new client methods for the §5.7.0 contract | A, B, C1 (tool API contract already captured, §5.7.0 — no longer a blocker) | Yes: stops storing pre-refresh data; locked companies become approval items |
| **C3** (stage 2) | Litigation reuse (copy-on-reuse with provenance, join-active waiting, `OriginSnapshotId`, `Reused` runs), Litigation-tab provenance banner | C1, C2 | Yes, flag-gated |
| **I1** (identity) | `NameNormalized`/`NameCore`/`EntityForm` columns + backfill + import/sync maintenance; token index or Full-Text; offline evaluation harness and labelled set | — | None (offline) |
| **I2** (identity) | `CompanyNameResolver`, `IdentityResolutions`, ranked interactive search, name-only intake, "Select this CIN" | I1 | Yes: better search; suggest-only (auto-select off) |
| **I3** (identity) | `Resolve` coordinator stage, ambiguity queue, `Resolve:AutoSelect` (off until §5A.4 evaluation passes) | B, I2 | Yes, flag-gated |
| **D** (stage 4) | Classifier, backoff/attempts, stall detection, attention queue actions, `PipelineEvents`, cost caps, `RetryAsync` extractions | B, C | Yes, flag-gated |

Rollout: A → B (Observe on real requests for a few days; compare against reality) → C1 → R → C2 with only
`Enforce:Dossier` → `Enforce:Litigation` → `Enforce:LitigationAnalysis` → C3 → D with `Enforce:Retries`. The identity track (I1 → I2 → I3) is independent
of A–D until I3, which needs B; `Resolve:AutoSelect` stays off until the §5A.4 evaluation passes.
**Hard gate:** `Enforce:Litigation` and `Enforce:LitigationAnalysis` refuse to start (fail-fast at startup, with a
clear message) unless the C1 tables and index exist — a paid auto-trigger without durable admission must be
impossible to enable by configuration alone.
Follows the repo's usual shape: one epic issue with these four as children, branch per PR, CI on the
self-hosted runner.

## 8. Test and verification plan

- **Unit (pure):** `PipelineDecider` table tests covering every dependency edge, every policy combination,
  and every completion/outcome rule; `PipelineFailureClassifier`; backoff schedule with jitter bounds;
  `CompanyRefreshPolicy` still green. These are the bulk of the coverage.
- **Session layer:** stub `HttpMessageHandler` scenarios — cookie expires mid-download (N parallel requests ⇒
  exactly one login), bad credentials (⇒ breaker opens, zero retries), 200-with-HTML locked vs expired,
  login-rate limit reached.
- **Integration (SQLEXPRESS, existing test project):** claim/lease races (two reconcilers, one run), kill the
  process mid-stage and confirm resume with **no duplicate paid call**, adoption cutoff, Observe mode performs
  zero writes to non-pipeline tables. Remember the local `AUTO_CLOSE` gotcha (disable per machine).
- **Paid-call admission (real SQL Server, real parallelism — not mocks):**
  - N=50 parallel `TryAdmitAsync` for one scope ⇒ exactly 1 winner, 49 `IN_FLIGHT`/`FRESH`.
  - Cap=k with n>k parallel admissions across different scopes ⇒ exactly k admitted, the rest
    `COST_CAP_REACHED`; a `Manual` admission at cap succeeds and is counted.
  - Two reconcilers (separate scopes/connections) on the same request ⇒ one job created, one ledger row.
  - Crash-injection: kill between `Reserved` and job creation (⇒ `Released` by sweep, counter restored on the
    original `DayKey`); between `RegistrationAttemptedUtc` and `VendorJobId` (⇒ `Committed`, no auto retry).
  - Rolling window: admission at t=0 and t=6d23h denied, t=7d+ admitted (no bucket-boundary double purchase).
  - Same client or different clients, same company and keyword set ⇒ **one scope**: exactly one purchase, the
    others reuse (§4.2a).
  - Auto-analysis: two racing creators for one snapshot ⇒ one row (unique-index violation is joined, not
    surfaced); a second snapshot ⇒ a new auto run.
- **Breaker races:** interleave a claim with a breaker-open at every step (before dequeue, after dequeue, after
  claim, mid-stage) and assert the §5.4 invariant — no job is `Queued`-but-not-enqueued and none is
  `Failed`/`ExtractionFailed` due to an open breaker without a scheduled retry. `IntegrationHealth`: 100
  concurrent failure/success/probe writers ⇒ counter equals the number of counted failures, exactly one
  `Open` transition, a stale failure (started before `LastTransitionUtc`) never re-opens a closed breaker.
- **Unlock / refresh (with a stub tool):** two reconcilers racing on one approval ⇒ exactly one unlock and one
  consumed approval; expired or other-company approval ⇒ rejected; unlock at month 11 ⇒ reused, at month 12+1 day
  ⇒ `Expired`, and a refresh never moves `UnlockedUtc`; N requests for one CIN ⇒ one refresh (`JoinActiveRefresh`);
  the stub refuses export before refresh completes ⇒ **no workbook is stored pre-refresh**; refresh past 36 h ⇒
  `REFRESH_TIMEOUT`; a company already unlocked by someone else ⇒ adopted with **zero** admissions.
- **Parked jobs:** 3 jobs waiting on refresh with 2 worker slots ⇒ a 4th, unblocked job still runs; restart while
  parked ⇒ gate re-evaluated, no duplicate refresh.
- **Litigation reuse:** copy integrity (row counts, evidence/prompt/response hashes byte-equal, provenance columns
  set, source `RetrievedUtc` preserved); reuse at 6 d 23 h, fresh purchase after 7 d; source in flight ⇒ joiner
  waits then reuses; N requests from M clients ⇒ exactly one purchase and one paid analysis; racing copies of the
  same origin ⇒ one row (`UNIQUE(RequestId, OriginSnapshotId)`); expired/failed source documents stay so in the
  copy and are never re-downloaded.
- **Identity resolution:** normaliser table tests (Pvt/Private, Ltd/Limited, &/And, punctuation, Unicode, word
  order); same-name duplicates ⇒ always `Ambiguous` without a hint; hint confirmation resolves one; struck-off
  twin penalised but listed; LLP vs company with the same name; tool-only hit never auto-selected; stale master
  recorded in the resolution; CIN-supplied short-circuit; duplicate-request collision ⇒ `DUPLICATE_REQUEST`, not an
  exception. **The §5A.4 harness is itself a gate:** auto-select cannot be enabled while precision on the
  auto-selected subset is below target; a CI check fails the build if thresholds change without a fresh report.
- **Performance:** interactive resolution p95 < 300 ms over the full 3.7M-row master (seeks only, ≤ 50 candidates).
- **Live E2E:** small company end-to-end unattended to `CoreReady`; then the COASTAL-scale company for
  duration/throughput; force a session expiry mid-filings-download; force a breaker open and confirm queued
  jobs wait and then drain.
- **Acceptance for the epic:** a CIN submitted with everything configured reaches `CoreReady` with zero human
  actions, `Complete` when enrichment policies are on, and any injected fault ends in either self-recovery or a
  `NeedsAttention` item with a stable code — never a silent stall or a repeated paid call.

## 9. Decisions needed from you

1. ~~Auto-litigation cost policy~~ — **Partially resolved (round 5):** search is free (confirmed), so
   `AutoLitigationSearch`'s cap is a rate-limit, not a spend guard (default `50`/day, §4.0) — **still open:**
   on by default, or per client? **Also still open:** the real spend cap value for `LitigationAnalysisPerDay`
   (defaults to `0` — fail-safe — until a number is set).
2. **Auto litigation AI analysis** (paid Vertex): auto-run, or stop after search/orders and leave analysis manual?
3. ~~Locked companies~~ — **Resolved (§13):** alert now and unlock only after an explicit approval; auto-unlock
   later behind `Pipeline:AutoUnlock` (§5.7).
4. **Definition of "done":** is `CoreReady` (dossier downloadable) the headline completion, with enrichment
   tracks reported separately as proposed?
5. **Concurrency of sessions:** can the tool account be logged in from two places at once? (I can test this;
   it decides whether a dedicated automation account is mandatory.)
6. **Adoption cutoff date** for `Pipeline:AdoptAfterUtc`.
7. **Litigation in the dossier PDF** — confirm out of scope for this epic.
8. ~~Cross-client reuse of a purchased litigation report~~ — **Resolved (§13):** reuse the already-analysed
   report across clients within a 7-day timeline (§4.2a).
9. ~~Cap semantics~~ — **Resolved (round 5):** manual admissions are never blocked but *do* count toward the
   day's `Used` — kept as proposed, no separate counters.
10. ~~Cap day boundary~~ — **Defaulted (round 5):** `Asia/Kolkata`, as proposed — low-stakes and it's a config
    value (`Pipeline:CapTimeZone`), so this is a easy override later, not a redesign, if wrong.
11. ~~The unlock "alert"~~ — **Resolved (round 5): banner for now.** Banner + `/Pipeline` board + log + `/health`
    only; no email sink pulled forward from stage 6. Revisit if approvals sit unnoticed in practice once PR R
    is live.
12. ~~Approver policy~~ — **Resolved (owner, 2026-09-23):** any authenticated user of the internal application may
    approve (it is internal-only today; revisit if it is ever opened to clients); an approval **does not expire**
    until consumed; one approval covers every request waiting on that company (still at most one credit spent).
13. ~~Is a refresh billable at the tool?~~ — **Resolved (§14):** confirmed live, 0 credits, no cap/ledger needed.
14. **Reuse details (§4.2a):** confirm (a) "already analysed report" = the search results **and** the AI analysis,
    (b) the one-week timeline runs from the *source report's* retrieval, (c) analysts must see "reused, retrieved
    on <date>".
15. **Name-resolution policy (§5A):** approve the "suggest-only until the offline evaluation passes" rollout, the
    proposed 99.5% precision target for auto-select, whether `RESOLVED_SOLE_ACTIVE` auto-selection is allowed at
    all, and which intake hints to accept (state, pincode, incorporation year, PAN).
16. ~~Use the tool's free pre-login view to confirm identity before an unlock?~~ — **Resolved (§14):**
    `getCompanyPreview` captured and wired into `Unlock`'s detect step and §5A.3's pre-spend safety net.
17. ~~Action for you: capture from the tool…~~ — **Done (§14):** [reference-tool-refresh-unlock-contract.md](reference-tool-refresh-unlock-contract.md).
18. **Confirm the assumption** that the user-flow spec governs the reference-tool workflow that auto-fetch drives.

## 10. Deferred (with reasons)

| Item | Reason |
|---|---|
| Auto-restart of stalled unfenced stages at runtime | Double-execution risk; needs lease fencing on `AutoFetchJob` first |
| Notifications / webhooks | Stage 6; `IPipelineAlertSink` is the seam |
| Intake API / bulk CSV / scheduled refresh (incl. wiring `CompanyRefreshPolicy`) | Stage 5; `PipelineRun.Trigger` already reserves the values |
| Litigation content inside the dossier PDF | Product decision, not automation |
| Re-embed on chunk-version bump | Already recorded as deferred (owner to discuss) |
| Multi-instance hosting guarantees for in-process queues | Stage 7; reconciler lease and paid-call admission are multi-instance-safe, the existing in-memory queues are not (the §5.4 re-enqueue sweep mitigates but does not replace a durable queue) |
| Content-addressed shared store for reused litigation order PDFs | Copy-on-reuse (§4.2a) duplicates files for up to 7 days per reuse; a shared store is an optimisation, not a correctness need |
| Enabling auto-unlock | Built behind `Pipeline:AutoUnlock` (§5.7) but off; the owner will enable it later |
| Per-client billing / wallet, pre-login public view, product-subscription matrix (from the user-flow spec) | No counterpart in this portal; §4.1 events preserve the data |
| Alias learning, phonetic/transliteration matching, director-based disambiguation for name resolution | §5A.5 — ranking hints only; needs the §5A.4 evaluation data first |
| Automatic re-purchase after a vendor-side failed search | Unknown-outcome risk with no vendor idempotency key; human decision by design |

## 11. Unverified assumptions (check during implementation)

- `AuditRouteRegistry` behaviour for routes not in the allow-list.
- Terminal-state definition of `LitigationOrderChunk` (the snapshot question is resolved: it is **not** stored —
  §4.2 adds it).
- That EF Core's `ExecuteUpdate` translation of the single-row admission/claim predicates behaves under
  SQL Server `READ COMMITTED` as assumed (single-statement atomicity); the §8 parallel tests are the proof.
- ~~Reference-tool unlock / refresh / status / unlock-date calls~~ — **resolved, §14.**
- Whether Full-Text Search exists on the deployment SQL Server SKU (decides token index vs Full-Text, §5A.2).
- Coverage and freshness of `CompanyMasterRecords` (LLP/FCRN completeness; last import date) and whether the import
  tool and `CompanyMasterSyncWorker` can maintain the new normalised columns.
- Whether a labelled name→CIN set of sufficient size can be assembled from existing requests (§5A.4).
- Whether `ReferenceToolClient`'s "logged-out" detection covers every endpoint, not just the ones read here.
- Reference-tool registry paging has never been verified against the live tool (already noted in memory) — the
  E2E in §8 is the first real check.

## 12. Review round 1 — disposition

| # | Review point | Disposition | Where |
|---|---|---|---|
| 1 | Paid-call caps not concurrency-safe | **Accepted.** Durable atomic admission (scope claim + counter + ledger in one transaction); caps and once-only are DB invariants; hard startup gate on Enforce flags | §2 (5), §4.0, §6.5, §7, §8 |
| 2 | Freshness scope undefined | **Accepted.** Scope = client + canonical CIN/LLPIN + keyword-set hash; no identifier ⇒ not eligible; rolling window on `LastCommittedUtc`; cross-client reuse left as an explicit decision (**superseded by §13: reuse is now the design**) | §4.1, §9 #8 |
| 3 | Once-per-snapshot AI contract unresolved | **Accepted; verified absent in schema.** `TriggerSnapshotId` + `Trigger` + filtered unique index; auto-analysis blocked until it merges (C1 hard dependency of C2) | §1, §4.2, §7 |
| 4 | Outcome semantics overlap | **Accepted.** First-match precedence table, `SkipKind`, `CoreReadyUtc` kept separate, disjoint rows 5/6, totality/monotonicity tests | §3.4 |
| 5 | Breaker vs job claim atomicity | **Accepted, reframed.** Exact atomicity is unattainable across two tables; guarantee the *invariant* (no stranded/wrongly-failed job) via gate-before-dequeue, predicate in claim, retryable failure handling, re-enqueue sweep. Verified the stranding hazard exists today | §1, §5.4, §8 |
| 6 | `IntegrationHealth` concurrent writes | **Accepted.** Single-statement atomic updates, one-winner half-open probe, stale-result fencing | §5.4, §8 |
| — | Additions from this round (not raised in review) | Manual paid calls also ledgered; `Released` on provable non-spend, `Committed` on unknown outcome (fail closed); no auto re-purchase after vendor-side failure; new decisions #8–#10 | §4.0, §9, §10 |

## 13. Owner decisions and new inputs (round 3)

| # | Input | Disposition | Where |
|---|---|---|---|
| 1 | Unlock: the system may unlock itself in future; today alert and unlock only once allowed | **Adopted.** Approval-gated unlock now (single-use, expiring, company-bound approval consumed inside the admission transaction); `Pipeline:AutoUnlock` built but off | §2 (8), §4.0, §5.7 |
| 2 | Litigation: fetch the already-analysed report; timeline one week | **Adopted as cross-client reuse.** Company-level scope, rolling 7-day window, copy-on-reuse with provenance, join-active waiting, `OriginSnapshotId`; supersedes round-2 client-scoped design | §4.0, §4.1, §4.2, §4.2a |
| 3 | User-flow spec (MCA-ROC) | **Adopted:** `Unlock`/`Refresh` stages, `CompanyRefreshPolicy` wired in, fixed 12-month window, 24 h refresh + join, 36 h timeout, "no stale data" gate, parked jobs. Found the code has **none** of this today | §1, §3.3, §5.7 |
| 4 | Resolve an ambiguous target **name** to the correct CIN from our database | **Adopted as a new first stage `Resolve`** with a normalised/ranked resolver, hints, persisted auditable resolutions, human selection for ambiguity, a trust ladder for spend, and an offline precision gate before auto-select | §3.3, §5A, §7 (I1–I3), §8 |
| — | Assumption made | The user-flow spec governs the reference-tool workflow that auto-fetch drives | §9 #18 |
| — | Blockers surfaced | Unlock/refresh/status calls must be captured from the tool; FTS availability; master-data freshness; labelled evaluation set | §9 #17, §11 |

## 14. Reference-tool unlock/refresh contract — capture disposition (round 4)

[reference-tool-refresh-unlock-contract.md](reference-tool-refresh-unlock-contract.md), captured against
`docs/reference-tool-unlock-refresh-capture-brief.md` (§9 #17), unblocks PR R. Verified independently before
relying on it — both the capture and a process problem during hand-off:

- **The capture itself is real and goes beyond what was asked.** Every call the brief requested is present and
  verified live (one real, owner-approved 1-credit unlock on a real company; a live refresh cycle; a locked, an
  unlocked, and an expired company all observed directly). Two findings weren't asked for and are genuinely
  useful: `getCompanyPreview` — a free, 0-credit identity check that works even on a locked company, folded into
  the §5.7 detect step and the §5A.3 pre-spend safety net — and `getUpgradeStatusForUnlockingAsset`/
  `getUpgradeStatusForCompanies`, an MCA-side maintenance advisory the tool's own client checks before spending;
  now a `Transient`/deferred reason code (`MCA_MAINTENANCE`, §6.1) rather than something PR R would otherwise
  have had to discover the hard way.
- **A real PII leak reached a local commit, then reached the working tree again after the fix, before being
  caught both times.** The first delivered version put a real colleague's name, corporate email and internal
  user id into the committed file 14 times, contradicting its own "zero leakage" banner — caught by grepping the
  committed content directly rather than trusting the summary. The stated fix (an amended commit) was itself
  correct on inspection (`git show HEAD:…` — zero hits), but the working-tree file no longer matched that
  commit: an editor had written the old, unredacted draft back over it, so the same PII was sitting unstaged in
  the repo again. Caught the same way — diffing the working tree against `HEAD` before accepting the "resolved"
  report a second time — and the actual fix (`git checkout -- <path>` restoring the clean commit) was verified
  independently after being reported, not assumed from the description. Nothing reached `origin` at any point
  (`git ls-remote` confirmed empty for the branch throughout), so no shared history needed cleaning.
- **Net effect:** the contract doc is sound and is now folded into §1, §5.7/§5.7.0, §5A.3, §6.1, and §9 #13/#17.
  The leak is fully corrected on disk and never left this machine — flagged here for the record, not as an
  outstanding issue, per this project's practice of tracking every finding to its resolution rather than
  dropping it once fixed.

## 15. Owner decisions (round 5) — cost model and cap policy

| # | Input | Disposition | Where |
|---|---|---|---|
| 1 | Alert channel: banner for now | **Adopted.** No email sink pulled forward from stage 6; banner + board + log + `/health` only | §9 #11 |
| 2 | Keep the daily cap including manual admissions | **Adopted, confirmed as proposed.** Manual never blocked by the cap, always counted toward it | §4.0, §9 #9 |
| 3 | Make the cap configurable | **Confirmed — already the design, made explicit.** Every cap is a config key read fresh at admit time, never compiled in; the two real spend caps (`LitigationAnalysisPerDay`, `UnlockPerDay`) default to **0** (fail-safe, matching this repo's existing inert-until-configured convention); the rate-limit-only `LitigationSearchPerDay` defaults to a generous `50` | §4.0 |
| 4 | Cost = unlocking a new company + AI analysis; the rest is free | **Adopted and confirmed.** Of the three admission kinds, `ReferenceUnlock` and `LitigationAnalysis` are real spend, `LitigationSearch` is not. `LitigationSearch`'s cap is a rate-limit guard against BPR's own **unconfirmed** rate/concurrency limits, not a spend guard — the dedup/reuse machinery around it stays, for correctness and order-PDF-freshness reasons independent of cost | §4.0 |
| 5 | Litigation search runs against "our own data lake … around 4.5 billion records" | **Adopted as grounding, scope kept narrow.** Read as: BPR's litigation search is a query against a large pre-indexed data lake, not a live external crawl — consistent with, and explains, the free-cost finding above. Deliberately **not** read as license to drop the existing vendor-style handling of BPR (secrets in user-secrets only, rate/concurrency/licensing terms still unconfirmed per `docs/litigation-data-lake-integration.md`) — that doc's own open questions are unaffected by this fact and are not something this plan resolves | §4.0 |

Still open after this round: the actual `LitigationAnalysisPerDay` cap number (defaults to 0 — inert — until
set); whether `AutoLitigationSearch` is on by default or per-client (§9 #1, second half).
