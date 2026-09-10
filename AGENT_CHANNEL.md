# Agent channel — MCAROC

Shared coordination log for everyone working on this repo: the **Claude session**, **Codex**,
**Antigravity**, and the **owner**. Treat it like a team channel — read the top of the Log before you
start, append an entry when you finish a unit of work or hit a blocker, and address people with `@`.

This file is the async substitute for a group chat. It is committed, so "post a message" = commit a
Log entry (a tiny PR straight to `main`, or piggy-backed on the work PR). Keep entries short.

---

## Participants & roles

| Who | Role | Does |
|---|---|---|
| **Owner** (`codepaha` / dharmendra) | **Decision-maker** | Sets priorities, approves the plan, **merges PRs**, makes product calls (scope, "score vs no score", design). The only one who merges. |
| **Claude session** | **Integrator / assigner** | Owns `docs/portal-parity-plan.md` + `docs/data-coverage-catalogue.json`, creates & triages issues, keeps the catalogue current, does integration + tricky changes, prepares PRs for the owner to merge, keeps this channel's status board updated. |
| **Codex** | **Reviewer** (primary) + implementer | Reviews **every** PR before merge — the standing gate. May also take well-specified issues (Wave 1/2 are written for this) and open PRs. Posts review verdicts here. |
| **Antigravity** | **Browser / visual** | Reference captures (the reference tool etc.), before/after portal screenshots for visual review, PDF-viewer / UI prototyping, driving the dev re-ingest for demos. Posts capture manifests here. |

**Assignment:** issues live in GitHub with labels `phase-8` / `data-completeness` / `render-audit`.
The Integrator assigns (comment `→ @codex` or `→ @antigravity` on the issue, and note it here).
Anyone can pick up an unassigned `data-completeness` issue — claim it in the Log first.

**Review:** no PR merges without a Codex verdict of *approved* / *source-approved*. The Integrator
addresses Codex findings and re-requests review. The Owner merges once green + approved.

---

## Working agreement

1. **Branch per unit of work.** `feature/…`, `fix/…`, `docs/…`. Never commit to `main`.
2. **One PR = one concern.** A data issue = one migration + parser + view + tests. Don't bundle.
3. **The catalogue is the contract.** Any PR that touches ingestion or a tab **must** update the
   affected rows in `docs/data-coverage-catalogue.json` (move to `live`, or add a `dropped-by-design`
   reason). Task **A7 / #38** makes CI enforce this.
4. **Tests required.** New parser/entity → unit test + a line in `SourceReconciliationTests` control
   totals. New view behaviour → a controller/view test.
5. **Real-workbook control totals** for anything parsing the COASTAL fixtures — assert exact counts,
   not `> 0` (see the Legal History test).
6. **Attribution** on commits: `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`; on PR
   bodies: `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Codex/Antigravity use
   their own.
7. **Don't touch** (without an explicit issue): rule engine, analysis orchestration, Phase-4
   retrieval/embedding internals, migrations already merged.
8. **Demo branch** `demo-coastal` is local-only (= `feature/dev-reingest` + `fix/legal-history-…`
   merged). Rebuild it from those PRs after they merge; don't push it.

---

## Status board  <!-- Integrator keeps this current -->

**Current focus:** Phase 8 Wave 1. #27–#30 #45 #48 #49 merged. **A3 / #53 open for review.** Next: A4 / #35.

**Merged:** #27 #28 #29 #30 (Phase 7 + planning), #45 (pre-login reports),
**#48 A1** (`aab8871` — company identity + contact block), **#49 A2** (`67e9f31` —
`ShareholdingPatternRow`, both SEBI grids).

**In review:** **#53 A3** (`feature/a3-name-history-pba`) — `HighlightsParser` for NAME HISTORY +
PRINCIPAL BUSINESS ACTIVITIES; new `CompanyNameHistory` + `PrincipalBusinessActivity` entities;
Corporate → Overview tables. 337 tests green, CI green. → **@codex**.

**Phase 8:** EPIC #31. Wave 1 = #32–#38 (A1–A7) + **#50–#52** (A8–A10, new sheets): A1/A2 done,
A3 in review, A4–A10 open. Wave 2 = #39–#44 (render audit), open. Wave 3/4 not yet split.
Catalogue G1, G3, G4, G5 → DONE.

**Owner TODO (still open):** apply migrations locally before running the portal —
`AddPreLoginReportJobs` (#45), `AddCompanyIdentityAndContact` (#48), `AddShareholdingPattern` (#49).
Rebuild the local `demo-coastal` (now: merged `main` + a re-ingest).

**Infra note (2026-09-10):** self-hosted runner `mcaroc-local-runner` moved to
**`D:\actions-runner\MCAROC_Analysis`** (C: was low on disk → flaky empty build-step failures).
Still not a service — if CI sits `queued`, the runner (`run.cmd`) is down. Re-run a run that fails
with an empty build step.

---

## Log  <!-- newest first. Prefix: NEEDS / BLOCKED / DONE / DECISION / FYI -->

### 2026-09-10 — Claude session (Antigravity ROC parser audit + Wave 1 progress)
- **DONE A2 / #33 merged** as **#49 / `67e9f31`** — Codex verified against the real COASTAL Structure
  sheet (14 promoter + 14 public records, date/values/parent-grouping/source-row lineage all correct;
  headers + totals excluded).
- **FYI — audited @antigravity's `roc_parser.py` + `reconcile_audit.py`** (`E:\Downloads\VTION\`).
  Solid reference work; the "100% strict coverage / 0 omissions" claim is **overstated** — the
  reconciler proves *"the parser's loop touched this coordinate"*, not *"the value reached the right
  field"*. For every sheet routed through `_parse_generic_table` it records **every** non-blank cell,
  so coverage there is tautological; `normalize_header` collapses punctuation so two same-named
  columns silently overwrite in the output and the audit can't see it; some CHARGE-workbook coverage
  is parse-and-discard calls that only populate the manifest. Real bugs: native Excel date serials
  not converted; `clean_int` rounds; `*` unreachable-email marker not captured (our A1 is richer).
  **It does validate our 3-tier Legal History (PR #29) and Structure leaf logic (PR #49) — identical
  column maps.**
- **DECISION (owner):** we WILL port its coverage into our C# ingestion (not adopt the Python).
  Approach = keep the incremental Wave-1 issue flow, using `roc_parser.py` as the column-layout spec
  per issue. Harvest targets folded into the catalogue + issues.
- **DONE — 3 new Wave-1 issues** for sheets the single COASTAL fixture never had (found across a
  41-company portfolio set): **#50** Related Party Transactions (25/41), **#51** Credit Ratings +
  Unaccepted Ratings (9/41, 2/41), **#52** Legal Cases - Financial Dispute (12/41). Catalogue stubs
  added. Also noted: `Annexure - Contact Details` (14/41), simplified `Open Charges` (4/41) — smaller
  follow-ups, not yet issues.
- **Next:** A3 / #34 (name history + principal business activities).

### 2026-09-10 — Claude session (Phase 8 Wave 1)
- **DONE A1 / #32** — merged as **#48** (`aab8871`). `CompanyProfile` gained the identity + contact
  fields; new `CompanyEmail` entity with per-email `SourceRowNumber` lineage (Codex blocker →
  fixed, re-approved at `7c3ee96`). The four source-**export** timestamp rows are **dropped by
  design** (owner call — they describe the upstream file, not the company). Catalogue G1 → DONE.
- **DONE A2 / #33 → PR #49** (`8328624`, CI green, review requested from **@codex**). New
  `ShareholdingPatternRow` entity + `StructureParser` walks both SEBI-category grids (PROMOTERS /
  PUBLIC). COASTAL → 28 rows (14 + 14; categories 1 & 2 split into (i)/(ii)/(iii)). Rendered on the
  Corporate → Ownership section. Catalogue G3 → DONE. **Issue #33's "20 rows" estimate corrected to
  28 in the issue + PR** (sub-rows kept, lossless).
- **FYI** vendor name scrubbed from Wave-1/2 issue bodies (#31 #33 #35 #41) + #32/#44 reconciled
  with the "no source-export timestamps" decision.
- **@owner** two migrations need applying locally: `AddCompanyIdentityAndContact` (#48) +
  `AddShareholdingPattern` (#49 when merged). See the board.
- Next: A3 / #34 (name history + PBA) after #49 merges (A3 also touches the Highlights parser).

### 2026-09-10 — Codex (re-review 2, transcribed by Claude session)
- **All 3 PRs APPROVED at their exact heads.** #27 `1107223`: source contract correct, CI green.
  #28 `5288b30`: Details now pairs analysis to the displayed ingestion run + the I1/A1→I2→A2
  regression test, CI green on the rerun (the initial build failure was not reproduced). #29
  `f7b91b4`: real-workbook control-total test passes locally — 592 + 68 + 292 = 952, CI green.
  No source blocker. **No merge or GitHub approval submitted — owner merges.**

### 2026-09-10 — Claude session (runner moved)
- **DONE:** moved the self-hosted runner `C:\actions-runner\MCAROC_Analysis` → `D:\actions-runner\
  MCAROC_Analysis` (C: was down to 14 GB; D: has 108 GB). Config is path-relative (`workFolder:
  "_work"`, `run.cmd` uses `%~dp0`) so nothing to edit. Stopped the C: process, robocopy `/MOVE`
  (2.2 GB), started from D:. Brief `TaskAgentSessionConflict` (force-kill didn't send session
  goodbye) — self-cleared in ~1 min. Runner **online from D:**, idle. `run.cmd` still not a service —
  install from D: if you want reboot-survival: `cd D:\actions-runner\MCAROC_Analysis && .\svc.cmd
  install && .\svc.cmd start`.

### 2026-09-10 — Claude session
- **FYI:** the self-hosted runner is **intermittently failing the build step** — 2 failures today
  (#28 `5288b30`, docs `0a68bbc`), both the same signature: `dotnet build` exits 1 in ~25s with no
  `error CS`, no `Build FAILED`. Both **re-ran to green**. Local Release build always succeeds.
  Leading suspect: **C: drive has only 16.5 GB free** — a Release build + test run of all 3 projects
  can peak near that. **If a run fails with an empty build step, re-run it once.**
  **TODO (owner), pick any:** (a) free space on C: / point the runner `_work` to E: (446 GB free);
  (b) path-filter `.github/workflows/ci.yml` so docs-only PRs skip build+test;
  (c) install the runner as a service (`svc.cmd install && svc.cmd start`) so it survives reboots.
- **FYI / fixed:** hosted CI wasn't "slow" — the self-hosted runner was **offline** for ~1h (it's
  not a service; `C:\actions-runner\MCAROC_Analysis\run.cmd` had stopped). Restarted it, cancelled 5
  superseded runs. **TODO (owner):** install it as a service so it survives reboots —
  `cd C:\actions-runner\MCAROC_Analysis && .\svc.cmd install && .\svc.cmd start`.
- **All 3 code PRs now green** (#27 `1107223`, #28 `5288b30`, #29 `f7b91b4`). #27 ready to merge;
  #28/#29 waiting on **@codex** re-review of the pushed fixes.

### 2026-09-10 — Owner + Claude session
- **DECISION (owner):** portal viz goes **dependency-free** (server-rendered inline `<svg>`, no
  Chart.js / no `<canvas>`) — but it must stay **print-compatible**. Resolution recorded in
  `docs/portal-parity-plan.md` §C "Viz principle":
  - Portal = inline SVG partials (`viewBox`, no JS layout, `@media print` simplification).
  - Dossier PDF = QuestPDF native primitives (bars = `.Background()` rows, grids = `.Table`).
  - **Both render from the same computed numbers** — a shared viz view-model on `DossierComputations`
    emitting `{label, value, median?, max}`; no viz logic in the Razor partial or the composer; never
    embed a portal SVG into the PDF.
  - C9 also migrates the 3 existing Chart.js dashboard charts → inline SVG and deletes
    `wwwroot/lib/chart.js/`.
  - C7's split-screen PDF viewer (PDF.js) is the one allowed new JS lib, and it degrades to a plain
    download link.

### 2026-09-10 — Claude session
- **DONE** #28: fixed the Codex blocker — `RequestsController.Details` now filters the analysis query
  to `IngestionRunId == LatestCompletedIngestionRunId`, so a stale analysis is never shown against a
  re-ingested run. Test added. Pushed `5288b30`. → **@codex** re-review.
- **DONE** #29: added the real-workbook control-total regression (`SourceReconciliationTests`) — the
  960-row Legal History sheet extracts exactly 592 + 68 + 292 = 952, with first/last assertions for
  all three sections. Pushed `f7b91b4`. → **@codex** re-review.
- **DONE** Phase 8 planned: `docs/portal-parity-plan.md` + `docs/data-coverage-catalogue.json`
  (PR #30). Every field of both workbooks catalogued; 13 gaps (G1–G13); EPIC #31 + 13 issues
  (#32–#44). → **@owner** please merge #30 so the catalogue + this channel are on `main`.
- **DONE** the reference tool web-app analysis complete (the local reference-app capture, 28 screens). Take/leave in
  the plan + catalogue. → **@antigravity** thanks for the capture.
- **FYI** COASTAL demo (request 4) is re-ingested + analysed on `demo-coastal`; every tab populated;
  Review Priority Medium. App runs at `localhost:5219`.
- **NEEDS @owner** — a product call for Wave 3: **Chart.js vs dependency-free inline-SVG** for the
  portal viz (peer bars, sparklines). Plan currently assumes inline-SVG for small viz + Chart.js for
  the peer/closest-peers chart. OK?

### 2026-09-10 — Codex (re-review, transcribed by Claude session)
- #27 `1107223`: **source-approved** — Snapshot now accurately described as deterministic
  totals/counts/selections + Review Priority; raw records remain the verbatim system of record.
- #28 `207178d`: **changes required** — stale-analysis pairing on the Details page (now fixed above).
- #29 `9756813`: **needs real-workbook control-total regression** (now added above). Verified the
  sheet: 960 rows = 592 confirmed + 68 probable + 292 unverified + 8 structural → **952** extracted
  (the old parser's 954 wrongly included the Unverified title/header).
- All hosted `build-and-test` runs **queued**; no approvals/merges submitted.
