# Litigation data-lake integration

## Product outcome

Every MCA/ROC request starts a request-scoped litigation search after its company identity is persisted.
The existing Litigation tab shows de-duplicated, provenance-backed results.  The dossier includes only
confirmed or analyst-reviewed cases; all returned candidate matches remain visible with their match basis.

The workflow downloads **all orders and judgments returned for the completed search**, not a hand-picked
subset.  Each downloaded document is then extracted, chunked and embedded for MCA ROC Copilot, with a
citation that identifies the case, vendor document identifier, source URL/identifier, retrieval time,
SHA-256 and page number.

## Request-to-report workflow

1. Persist the MCA request and resolve its legal name and MCA name history.
2. Build an immutable keyword plan.  The plan contains the legal name, former legal names, conservative
   `Limited`/`Ltd` variants and approved aliases.  Approved aliases are the only source of abbreviations,
   phonetic terms and Hindi spellings; their source and reviewer are retained.  Do not infer `HFCL` from
   `Hero FinCorp`, or transliterate a name without review.
3. Persist and enqueue one `LitigationSearchJob`.  It registers every keyword with the vendor, records
   every vendor job ID, waits/polls according to the vendor contract, downloads the completed export and
   records the raw-export hash.
4. Normalize and de-duplicate the case rows.  A stable vendor case ID is the primary key; otherwise use a
   collision-safe composite of court, normalized case number, filing year and normalized parties.  Retain
   every source-row reference and all query terms that found the case.
5. Persist case-match decisions separately from cases.  `Confirmed`, `Candidate`, `Rejected` and
   `NeedsReview` are match decisions, not guesses derived from a keyword.  Only `Confirmed` and reviewed
   matches can feed metrics, findings or the executive summary.
6. Once case ingestion is complete, enqueue one `LitigationOrderDownloadJob` for every vendor order and
   judgment reference in the result.  It is all-or-nothing in scope, but allows terminal warnings per
   unavailable document.  It persists total/listed/downloaded/failed bytes and a checkpoint per document.
7. Validate MIME/signature, hash and store each document, then run OCR/text extraction and embedding.  The
   Copilot retrieval boundary remains `RequestId`; it must never search another client's litigation docs.
8. Project confirmed cases into the existing Litigation tab and dossier; render query coverage, sources,
   retrieval timestamp, document coverage and candidate-match disclaimer in the client report.

## New durable records

`LitigationSearchJob`, `LitigationSearchKeyword`, `LitigationVendorJob`, `LitigationCase`,
`LitigationCaseMatch`, `LitigationOrderDocument` and `LitigationOrderDownloadJob` are separate from the
workbook-derived `Litigations` table.  The latter requires an MCA `IngestionRunId` and cannot safely
represent externally retrieved cases or documents.

The current `DocumentChunk` schema is specific to `McaFilingDocument`/`McaFiling`.  Do not insert
litigation orders by fabricating MCA filing IDs.  Add a generic, request-scoped document-source contract
or a parallel litigation chunk table and make the retriever/citation model understand both sources.

## Vendor contract required before the HTTP client is enabled

- corporate/LLP entity types (the supplied collection only demonstrates `individual`);
- authentication response, expiry/refresh and HTTPS endpoint;
- job response schema, completion/polling endpoint and report expiry;
- actual JSON and XLSX schemas (the saved XLSX example conflicts with a `file_format: JSON` registration);
- order/judgment listing and bulk-download endpoint, stable case/document IDs and pagination;
- rate/concurrency limits, file-size limits, retention, licensing and client-display permissions.

Credentials belong in server secret storage, never in the Postman collection or source-controlled config.

## First implementation increment

`LitigationKeywordPlanner` is implemented with tests.  It preserves alias provenance, carries MCA name
history, de-duplicates terms deterministically and generates only legal-suffix variants.  The next
increment is the EF-backed keyword plan and durable job schema, followed by the vendor client once the
contract above is confirmed.

## Acceptance checks

- Bharat Petroleum, Hero FinCorp and HDFC samples retain every approved alias, including Hindi variants,
  with its source and no duplicate term.
- A job retry never creates duplicate cases or duplicate order documents.
- A failed/all-orders download reports exact failures and does not claim document coverage is complete.
- Candidate matches never affect a risk score, finding or AI summary.
- Copilot answers cite only documents belonging to the active request and cite exact document/page evidence.
- The report tells clients which keywords were searched, the source, retrieval time and document coverage.
