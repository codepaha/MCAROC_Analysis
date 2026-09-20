# Litigation data-lake integration

This is the design doc and phased roadmap for epic #239. It is revised as each phase lands — sections
describing not-yet-built behavior are explicitly marked **Planned**, so this file never claims something is
active before it is. The authoritative product decisions live on #239 itself; this doc must not drift from
them again (see the note at the bottom on what changed and why).

## Product outcome

BPR is a request-scoped module that runs alongside the MCA ROC request but produces its own **separate
Litigation Report** (PDF and CSV) — it is never merged into or rendered inside the MCA ROC dossier. Every
case BPR returns is treated as a confirmed business result: the report does **not** carry
Confirmed/Probable/Candidate labels or any match-decision workflow. De-duplication is internal and
conservative — a shared, valid CNR plus a compatible proceeding type is the only automatic identity signal
(`LitigationCaseIdentity.CanAutoDedupe`); CSP ID is retained as the BPR/provider case identity, but is never
used as a merge key, since its scope in the vendor contract has not been confirmed.

**Planned** — once all-orders retrieval and text retention are built (#243/#244): every order and judgment
BPR returns for a completed search is downloaded, not a hand-picked subset. Retained order text is
extracted, chunked and embedded so the MCA ROC Copilot can answer with exact case/order/page citations,
strictly scoped to `RequestId` — never fabricated onto MCA filing IDs, since litigation orders are not MCA
filings (see "New durable records" below).

## Confirmed BPR API contract

The vendor's own Postman collection (kept outside this repository — its embedded secret key and JWTs are
never copied into source, config, or a commit) confirms three endpoints:

- `POST sec/authenticate` — body `{id, secret_key}`.
- `POST bprjob/register` — body `{entity_type, keywords, application_customer_id, file_format, exact_match, formats}`,
  `Authorization` header carries the **raw JWT with no `Bearer ` prefix** (confirmed from the vendor's own
  example requests — an easy mistake to make since it differs from the usual convention).
- `GET report/job/{id}` — same raw-JWT `Authorization` header.

Still genuinely unconfirmed, even with the collection in hand:

- The authenticate/register response field names for the token and vendor job id — no example response was
  ever captured for either call.
- Whether `report/job/{id}` distinguishes "still processing" from "complete" via HTTP status, a JSON status
  field, or content type. The one captured example response is a raw XLSX binary for a job that was
  registered with `file_format: JSON` — a confirmed, unresolved discrepancy, not a hypothetical one.
- Corporate/LLP entity types (the collection's only example uses `entity_type: "individual"`).
- Rate/concurrency limits, file-size limits, retention, licensing and client-display permissions.

`BprLitigationClient` (#241) handles both response-shape gaps defensively — trying a short list of plausible
field names and failing loudly naming what it actually received, and sniffing the report response's
bytes/content-type/JSON shape rather than trusting configuration — instead of assuming a schema that was
never confirmed. Credentials belong in server secret storage (user-secrets/environment), never in the
Postman collection or a committed config file.

## Roadmap

Phase numbers match the tracked issues under epic #239; "Done" items are merged, "Planned" items are not
started or are mid-review — check the epic and `AGENT_CHANNEL.md` for current status rather than trusting a
stale copy of this table.

| Phase | Scope | Status |
|---|---|---|
| Foundation | Keyword planning (`LitigationKeywordPlanner`), CNR-first identity/de-dup gate (`LitigationCaseIdentity`), BPR nested-JSON report parser (`BprLitigationReportParser`), QuestPDF case-card + CSV report shell (`LitigationReportArtifacts`) | **Done** — pure domain logic, no schema, no HTTP calls, no live data wired in yet |
| #241 LIT-01 | BPR API client + durable job lifecycle (authenticate → register → poll → retain raw report) | In progress |
| #242 LIT-02 | Persist BPR cases; CNR-first de-dup; retain CSP as provider identity | Planned |
| #243 LIT-03 | All-orders retrieval, text retention, bulk ZIP delivery | Planned |
| #244 LIT-04 | Request-scoped litigation evidence for the MCA ROC Copilot (a parallel chunk/citation model — never fabricated MCA filing IDs) | Planned |
| #245 LIT-05 | Evidence-grounded Gemini case + portfolio analysis | Planned |
| #246 LIT-06 | Litigation tab — court grid + case-card UI | Planned |
| #247 LIT-07 | Wire the standalone PDF/CSV report shell to persisted live data | Planned |
| #248 LIT-08 | End-to-end acceptance + operational hardening | Planned |

## Keyword planning (done)

`LitigationKeywordPlanner` builds the auditable keyword set for one company: legal name, MCA name history,
and only *approved* aliases — abbreviations, phonetic spellings and Hindi terms are never guessed (it will
not infer "HFCL" from "Hero FinCorp"). The only automatically generated variants are conservative
`Limited`/`Ltd` legal-suffix forms. Approved-alias curation (source, reviewer) has no admin store yet — flagged
as a real gap in #241's PR, not currently covered by any of #241–#248.

## New durable records (planned, #242 onward)

`LitigationSearchJob` (#241, request-scoped job lifecycle) is separate from the workbook-derived `Litigation`
entity — the latter requires an MCA `IngestionRunId` and cannot represent an externally retrieved BPR case.
#242 adds the case/party/advocate/order persistence layer on top of #241's job; #243 adds order-document
retention. Exact entity names and shapes are #242/#243's design decisions, not fixed by this doc.

The current `DocumentChunk` schema is specific to `McaFilingDocument`/`McaFiling`. #244 must not insert
litigation orders by fabricating MCA filing IDs — it needs a generic, request-scoped document-source
contract, or a parallel litigation chunk table, with the retriever/citation model understanding both
sources.

## Acceptance checks

- Bharat Petroleum, Hero FinCorp and HDFC samples retain every approved alias, including Hindi variants,
  with its source and no duplicate term (covered by `LitigationKeywordPlannerTests`).
- Only a shared, valid CNR plus a compatible proceeding type auto-identifies two observations as the same
  case; CSP ID is never used as a merge key (covered by `LitigationCaseIdentityTests`).
- A job retry never creates a duplicate vendor registration or duplicate persisted cases/order documents.
- A failed/incomplete all-orders download reports exact failures and never claims document coverage is
  complete.
- Copilot answers cite only documents belonging to the active request and cite exact document/page evidence.
- The report discloses which keywords were searched, the source, retrieval time, and document/case coverage
  — with no Confirmed/Probable/Candidate labeling anywhere in the client-facing artifact.

---

**Revision note (2026-09-20):** the previous version of this doc predated #239's confirmed product
decisions and described a design that #239 explicitly superseded — litigation feeding into the MCA ROC
dossier, `Confirmed`/`Candidate`/`Rejected`/`NeedsReview` match-decision states, and candidate matches
excluded from metrics. None of that reflects the agreed contract (BPR-returned cases are already confirmed;
litigation ships as a separate report; de-duplication is CNR-first only). It also described the full
end-to-end job/persistence/Copilot pipeline as if already active, when only the foundation layer above was
built. Rewritten as a phased roadmap against the actual epic (#239, tasks #241–#248) and aligned with the
CNR-first identity contract `LitigationCaseIdentity` already implements.
