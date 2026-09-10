# Phase 8 — Portal data-parity + editorial restyle

**Goal.** Every data element of the two MCA/ROC workbooks is captured by a parser **and** rendered
somewhere the user can see it; the portal is restyled to the editorial identity; and we adopt the
the reference tool UX patterns that are worth adopting — while keeping the things that make us different (AI
synthesis, deterministic Review Priority, in-app document viewer).

**Source of truth for "nothing missed":** [`data-coverage-catalogue.json`](./data-coverage-catalogue.json).
Every sheet → every field → `{parser, entity, entityField, status, portal, dossier, gap}`. A field
whose `status` is not `live` is a task. Keep the catalogue updated as work lands — a PR that touches
ingestion or a tab must move the affected rows to `live` (or explain why not).

This runs **after** the open PRs land: #27 (dossier PDF), #28 (dev re-ingest), #29 (legal-history
parser). It supersedes and absorbs the old "Track B" portal-restyle plan and the the reference tool dossier
plan (the reference-report analysis (internal), P0–P6).

---

## 1. What we take from the reference tool, and what we leave

(Full list in the catalogue's `benchmark_take_leave`.)

### Take

| Pattern | Where it lands |
|---|---|
| Per-tab **CONTENTS jump-nav** on the long tabs | C2 |
| **"Reference Document(s)"** rows — link each data block to the exact MCA form + underlying PDFs | C3 |
| **Explicit degenerate-state text** — name *why* Review Priority gated off (like the reference tool's "score not available because 1…5") | C8 |
| **Colour as a signal** — green (satisfied/valid/active), red (ceased/invalid), amber (modification) | C4 |
| **Inline annotations** — `OLD BANK (now NEW BANK)`, `UNKNOWN CHARGE HOLDER`, invalid email in red | C4 |
| **Grouped charge table** with inline `[+]` expand | C5 (complements the drawer) |
| Peer **actual-vs-median** paired columns + **5-closest-peers bar chart** | A4 + B-fin |
| **Split Pending / Disposed** as sub-sections | B3 |
| **Unit scaler** — Crore / Lakh / INR (not Million) | C5 |
| **Multi-select → "Request a Zip"** on documents | C7 |
| Consistent **"As per our records, X has no Y"** empty states | across B |
| **4 provenance timestamps** in the fixed header ("Unlocked on" / "Last updated N days ago") | A1 |
| **Full shareholding-pattern category grid** (the reference tool shows it, we drop it) | A2 |

### Leave

- **No 0–100 / 1–5 score or gauge.** Keep the deterministic **Review Priority** + flag strip. the reference tool's
  own score is `null` for COASTAL (a distressed company) — the deterministic classification + a
  named reason is the better answer.
- **No "no synthesis at all."** Our AI Analysis tab / Section 1 / 5-slot summary is the product;
  the reference tool has none.
- **No download-only documents.** We build the **split-screen in-app PDF viewer** — the reference tool only
  downloads/zips. Real differentiator (C7).
- Disclaimer-on-every-page; the 200-page dump feel.
- Data beyond the MCA/ROC Excel (RBI publishing lists, SMTP validation, carrier lookup, site
  scraping) — out of scope now.

---

## 2. Workstreams

### A — Data completeness (parsers + entities)

Nothing from the Excel may be left un-parsed. Each item = a new/extended entity + migration +
parser change + reconciliation-test update.

| # | Gap | Scope |
|---|---|---|
| **A1** | G1 | Company identity/contact/provenance: business address, website, emails (+ reachability), phone, narrative, entity type, listing status, last AGM, LEI+status, **4 "as of" timestamps**, MCA "Sum of Charges" figure. Extend `CompanyProfile` (or new `CompanyContact` + `IngestionRun` provenance columns) + `CompanyProfileParser`. |
| **A2** | G3 | `ShareholdingPatternRow` entity (holderClass, category, equityShares, equityPct, prefShares, prefPct, fy) + parser for the Structure sheet's PROMOTERS/PUBLIC grids. |
| **A3** | G4, G5 | `CompanyNameHistory` (name, tillDate); `PrincipalBusinessActivity` (groupCode, groupDesc, activityCode, activityDesc, turnoverPct). Parse in `FinancialParametersParser`/`CompanyProfileParser`. |
| **A4** | G6 | `PeerCompany` (legalName, cin, city, revenue, fy) + extend `PeerComparisonParser`. |
| **A5** | G7, G13 | `EpfoEstablishment` (establishmentId, name, city, dateOfSetup, principalBusinessActivities, address, exemptionStatus, workingStatus, flags); add `EpfoContribution.Trrn`. |
| **A6** | G9, G10, G11 | Column drops: extend `Shareholding` (designation, cessation, relationship, country/city, PUC, SOC, incorpDate, status, activeCompliance, remarks); `RelatedCorporate` (activeCompliance, remarks); `GstRegistration` (centreJurisdiction, stateJurisdiction, legalNameOfBusiness); split `AuditorObservation.AuditorName` → name/membershipNo/firmName/firmRegNo. |
| **A7** | — | Extend `SourceReconciliationTests`: for each sheet, assert its declared columns each resolve to a mapped entity field OR an explicit `dropped-by-design` allowlist. Fail CI when a new unmapped column appears. Wire the catalogue JSON into the test as the expectation. |

### B — Render audit (portal shows everything captured)

| # | Gap | Scope |
|---|---|---|
| **B1** | G8 | **Financials tab** — render the *full* statement: typed columns + `FinancialFact` rows (BS/P&L/CF) + Ratios block + `FinancialParameter` (all years) + a "cash-flow year inferred" caption where `CashFlowYearInferred`. S/C toggle must expose consolidated. |
| **B2** | G8, A2, A3 | **Corporate tab** — render `CompanyOfficer` rows; `CompanyNameHistory`; `PrincipalBusinessActivity`; the full `ShareholdingPatternRow` grid; the A6 shareholding columns. |
| **B3** | G12 | **Litigation tab** — split **Confirmed / Probable / Unverified** into their own sub-sections, each with a plain-English disclaimer (benchmark pattern). Split Pending/Disposed within each. |
| **B4** | G7 | **Compliance tab** — `EpfoEstablishment` header card per establishment; **summarise** the 1,277-row suit-filed set (peak amount per bank, latest quarter) rather than dumping it; keep the full set one click away. |
| **B5** | — | **Charges** — confirm `_ChargeDrawer` + the dossier `ChargeCard` render every one of the 18 `RocChargeEvent` fields verbatim, **including satisfied-charge prose** (only in the charge workbook's "…in Details" sheets). |
| **B6** | A1 | **Fixed header** — the 4 provenance timestamps + contact block + narrative. |

### C — Editorial restyle + benchmark-inspired UX  (the old "Track B" + the "take" list)

> **Viz principle (owner decision, 2026-09-10): dependency-free, print-safe, one data contract.**
> - **Portal viz = server-rendered inline `<svg>`** partials. No Chart.js, no `<canvas>`. SVG is
>   vector + part of the DOM, so a browser Print / "Save as PDF" of a portal page renders it crisp
>   (canvas charts rasterise blurry or come out blank if print fires before JS). No JS dep, no CSP
>   concern, theme-aware via `currentColor`, deterministic for visual-diff review.
> - **Dossier PDF viz = QuestPDF native primitives** (`.Background()` rectangle rows for bars,
>   `.Table` for trend grids, `.Canvas()`/`.Line()` for a sparkline). QuestPDF has no browser — it
>   already cannot consume Chart.js/SVG; this is the existing constraint, not a regression.
> - **Compatibility guarantee:** both render from the **same computed numbers** — a shared viz
>   view-model (extend `DossierComputations`) emits `{label, value, median?, max}` tuples; the Razor
>   partial and the QuestPDF composer both consume it, no viz logic in either. Never embed a portal
>   SVG into the PDF as an image — redraw natively from the numbers.
> - Every SVG partial: explicit `viewBox`, no JS layout, `@media print` simplification (drop hover
>   affordances, force the light palette). Migrate the 3 existing Chart.js dashboard charts to inline
>   SVG and delete `wwwroot/lib/chart.js/` (part of C9).

| # | Scope |
|---|---|
| **C1** | Design tokens (Fraunces / IBM Plex, maroon, hairlines), `_Layout` shell, footer, `site.js` split. |
| **C2** | Per-tab CONTENTS jump-nav + scroll-spy (replaces the Phase-6 sub-tab strip). |
| **C3** | "Reference Document(s)" provenance rows — wire curated blocks to `RequestDocument` / `DocumentChunk` (form name + page). |
| **C4** | Colour-as-signal + inline annotations (holder rename, unknown holder, invalid email, ceased director). |
| **C5** | Grouped charge table w/ `[+]` expand; per-section ₹ unit scaler (Crore/Lakh/INR). |
| **C6** | Shared viz view-model + **inline-SVG** partials: `_Sparkline` · `_MiniBars` · `_SplitBar` · `_RateBar` · `_PeerCompare` (actual-vs-median) · `_ClosestPeers` (5-bar). Each has a degenerate state + `@media print` rules. |
| **C7** | **Split-screen in-app PDF viewer** (`GET /Requests/{id}/documents/{docId}.pdf#page=N` + PDF.js — the *only* new JS lib, and it degrades to a plain download link) + omni-present docked chat (`_ChatPanel`, `POST /Chat/AskJson`) + `Ctrl+K` + per-tab suggested prompts. Citations (`RetrievedSource` already carries `ChunkId/PageNumber`) open the viewer at the page. |
| **C8** | Review-Priority degenerate state — name the gating reason(s) (`CORP_FINANCIAL_DATA_STALE`, …), benchmark-style. |
| **C9** | Dashboard / Search History / New Search restyle; **migrate the 3 Chart.js dashboard charts → inline-SVG partials**, remove `wwwroot/lib/chart.js/` + `dashboard.js` Chart helpers. |
| **C10** | Dark mode / responsive / a11y / `prefers-reduced-motion` / focus rings / **print stylesheet** (`@media print` for every tab + viz). |

### D — Dossier PDF polish  (the reference tool-plan P0–P6, after #27 merges)

P0 provenance + hierarchical TOC · P1 current-slice/full-history annexures · P2 two-pass charges +
expanded cards · P3 inline flags · P4 litigation uncertainty buckets · P5 ratios/peer/subtotal ·
P6 empty-states. Also feed A1 provenance + A2/A3/A4/A6 new data into the annexures.

**P5 viz** = QuestPDF native (`.Background()` rows for the peer bars, `.Table` for the ratio grid) —
same shared viz view-model as C6, drawn with QuestPDF primitives. No image embedding.

---

## 3. Sequencing

```
now:   land PR #27, #28, #29
       |
Wave 1 (data — unblocks everything, mostly independent, Codex-friendly):
       A1  A2  A3  A4  A5  A6     then  A7 (recon test)
       |
Wave 2 (render audit — needs Wave 1 entities; per-tab):
       B1  B2  B3  B4  B5  B6
       |
Wave 3 (restyle — needs a stable data surface):
       C1 -> C2 C3 C4 C5 -> C6 C7 C8 -> C9 C10
       |
Wave 4 (dossier): D (P0..P6)   — can start in parallel with Wave 3 once #27 is in
```

Each A/B item is one migration + parser/view change + tests, independently shippable. C1 gates the
rest of C. D is parallelisable.

## 4. How Codex and Antigravity help

- **Codex** — code review on every PR (as today), and can take **Wave 1 (A1–A7)** and **Wave 2
  (B1–B6)** issues directly: they are well-specified, test-backed, mostly mechanical (entity +
  migration + parser + view + reconciliation assertion). Each issue links the exact catalogue rows.
- **Antigravity** — (a) browser capture of more the reference tool screens / other reference companies when a C
  issue needs a visual reference; (b) can drive the dev re-ingest + screenshot the portal before/after
  a change for visual-diff review; (c) prototype the C7 PDF viewer against a real filing.
- **This session** — owns the plan, the catalogue, and integration; reviews Codex/Antigravity output
  against the catalogue; keeps `data-coverage-catalogue.json` current.

## 5. Definition of done (Phase 8)

1. `data-coverage-catalogue.json` — **every** field row is `live` or `dropped-by-design` (with reason).
2. `SourceReconciliationTests` fails if a new unmapped column appears (A7).
3. Re-ingest COASTAL → walk all 7 tabs: every sheet's data is visible; no garbage; empty states
   consistent; provenance timestamps shown.
4. Portal on the editorial identity, light + dark; benchmark-inspired patterns from the "take" list in.
5. Dossier PDF carries the new data (D).
6. Side-by-side vs the the reference tool capture (`the local reference-app capture`) — we match on completeness and
   beat on synthesis + the document viewer.
