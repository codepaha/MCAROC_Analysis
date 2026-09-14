# Document-to-Data Linking Plan

## Purpose

Create an evidence layer that links supplied MCA filing documents to data already
ingested from ROC and charge workbooks. A link must be explainable, reproducible,
request-scoped, and reversible. The system must never force every document into a
link: an unlinked result is valid when there is no sufficient evidence.

This plan is deliberately separate from extraction. It adds provenance and review
without changing the underlying workbook-derived entities or treating OCR/AI
suggestions as facts.

## Supplied pilot corpus

The first pilot is **COASTAL PROJECTS LIMITED** (`U45203OR1995PLC003982`):

| Asset | SHA-256 | Observed scope |
|---|---|---|
| `COASTAL PROJECTS LIMITED Documents.zip` | `1DABC81A46C44C68AA864DEB5A91E446663CDA61B30CA903BD2CC593DD87C1FA` | 19 nested MCA archives containing 814 PDFs |
| `U45203OR1995PLC003982.xls` | `17694BEE869A76ED0AE8ACF9C519983F58E9550E1B259361C12C3898F72575EA` | ROC/company, financial, compliance, and charge summary data (26 sheets) |
| `U45203OR1995PLC003982-charge.xls` | `16F2249A1382293721761D00804A004900E7E324E9B81BE5D0601E80CE286FEC` | detailed open/satisfied charge data (5 sheets) |

The nested archive names (`70908`, `70909`, and so on) are treated as external
identifiers, not assumed to be MCA SRNs until a document or a portal export proves
that mapping.

## Existing foundation to reuse

- `RequestDocument` identifies the original ROC/charge workbooks and request
  ownership.
- Ingested entities already carry `SourceDocumentId`, `SourceSheetName`, and
  `SourceRowNumber`.
- `IngestionRun` records the source ROC and charge document IDs for the active
  run.
- `SourceRow` retains workbook-row lineage, and dossier/calculation provenance
  already carries document, sheet, and row references.
- `McaFilingDocument` represents PDFs extracted from a filing batch, including
  deduplication through `DuplicateOfDocumentId`. Its `FormType` is document-level;
  CIN, SRN, parsed company name, and filing-level dates must be read through its
  parent `McaFiling`, not assumed to be fields on the PDF row.

The new feature must reuse these IDs. It must not add parallel copies of workbook
data or infer cross-request links.

## Scope and non-goals

### In scope

- Request-scoped links from an MCA filing PDF to an ingested entity or a specific
  entity field.
- Evidence locators: PDF page, text quote/span, and optional bounding box; workbook
  document, sheet, and row references.
- Deterministic auto-linking, reviewer confirmation/rejection, and durable
  unlinked/ambiguous outcomes.
- A reconciliation view and export suitable for audit.

### Out of scope for the first release

- Altering an extracted value because a PDF appears to disagree with it.
- Creating a link merely from company/CIN equality.
- Cross-company or cross-request document reuse.
- Making an AI/OCR suggestion authoritative without deterministic evidence or
  reviewer confirmation.
- Reprocessing historical documents that have no readable text or stable
  identifier solely to improve coverage.

## Link model

Add a migration-owned `DocumentDataLink` table. The migration must be scheduled in
the schema-owner lane under the repository's one-migration-at-a-time rule.

| Field | Meaning |
|---|---|
| `DocumentDataLinkId` | Primary key |
| `RequestId` | Mandatory request scope and authorization boundary |
| `IngestionRunId` | Mandatory source-data version; the run whose entity is being evidenced |
| `FilingDocumentId` | Immutable actual `McaFilingDocument` containing the evidence |
| `CanonicalFilingDocumentId` | Current canonical PDF identity used for deduplicated display and active-link uniqueness |
| `TargetEntityType`, `TargetEntityId` | Existing ingested entity being evidenced |
| `TargetField` | Nullable field/property identifier; null means entity-level evidence |
| `LinkKind` | `Source`, `Supports`, `Contradicts`, `Duplicate`, or `Supersedes` |
| `Status` | `AutoAccepted`, `PendingReview`, `Confirmed`, `Rejected`, `SupersededByDuplicate`, `UnlinkedNoCandidate`, `UnlinkedAmbiguous`, `UnlinkedInsufficientText`, `UnlinkedOutOfScope`, or `InvalidDocument` |
| `MatchMethod` | `DirectProvenance`, `ChargeCompositeKey`, `FilingMetadata`, `FinancialPeriod`, `TextEvidence`, or `Manual` |
| `Confidence` | `High`, `Medium`, or `Low`; never use confidence alone as proof |
| `EvidenceJson` | Versioned evidence payload: identifiers, normalized values, PDF page/text quote, and source workbook sheet/row |
| `RuleVersion`, `InputHash` | Reproducibility of an auto-link decision |
| `CreatedUtc`, `ReviewedUtc`, `ReviewedBy`, `ReviewReason` | Audit trail |

Database constraints and target validation:

- Foreign keys to `Requests`, `McaFilingDocuments`, and `IngestionRuns`. Add the
  alternate key `(IngestionRunId, RequestId)` to `IngestionRuns` and a composite
  foreign key from the same pair on `DocumentDataLink`; this makes a link's source
  run database-enforced request scope rather than a convention.
- `TargetEntityType`/`TargetEntityId` is deliberately a polymorphic reference:
  SQL Server cannot express a foreign key to several entity tables. It is the
  narrow exception, not a substitute for referential integrity. A database check
  constraint permits only a migration-owned fixed list of target type codes; adding
  a type requires an explicit migration. `DocumentLinkTargetResolver` maintains
  the matching `HashSet` allow-list and, in the write transaction, resolves the
  concrete row and verifies its `RequestId` and `IngestionRunId` before insert or
  update. No arbitrary type name or ID is accepted from the API.
- Unique filtered index for active/non-rejected links on
  `(CanonicalFilingDocumentId, IngestionRunId, TargetEntityType, TargetEntityId,
  TargetField, LinkKind)`. This is the actual concurrent idempotency boundary;
  pre-query-and-insert handling alone is insufficient.
- Check constraints for known enum values and non-empty `EvidenceJson` for
  accepted/confirmed/contradictory links.
- A canonical-document rule: new links resolve and store the current canonical
  root. The canonical identity must be in the same request as the evidence PDF.

## Matching hierarchy

The linker evaluates rules in order and records every candidate and its evidence.
Later, weaker methods never overwrite an earlier deterministic result.

### 1. Direct provenance

Use a document ID, worksheet/source row, or filing-document ID already present in
the source data. This is the highest-confidence source link.

### 2. Charge composite key

For charge PDFs, require a normalized match on all available strong keys against
the request's ingested `RocCharge` and `RocChargeEvent` rows:

- charge ID (`RocCharge.RocChargeNumber`);
- event type (`RocChargeEvent.EventType`: `Creation`, `Modification`, or `Satisfaction`);
- charge event date (`RocChargeEvent.EventDate`) or MCA registration filing date (`RocChargeEvent.FilingDate`);
- holder name (`RocChargeEvent.HolderNameNormalized` or `RocCharge.LatestChargeHolderNormalized`) and amount (`RocChargeEvent.ChargeAmount` or `RocCharge.CurrentAmount`) as corroboration.

`RocChargeEvent` correlation fields:
- `EventDate`: Date of the deed/instrument creation, modification, or satisfaction.
- `FilingDate`: Date the form was registered with the ROC (e.g. column 4 "FILING DATE" in the Open Charges Sequence sheet).
- Both dates are distinct `DateOnly?` values. A charge PDF document may state either the deed execution date (`EventDate`) or the MCA filing/registration date (`FilingDate`), or both.

Matching key specification:
`ChargeId + EventType + (EventDate OR FilingDate) + [Amount, Holder corroboration]`

The matcher distinguishes between date match modes (`EventDateMatched`, `FilingDateMatched`, or `BothDatesMatched`) and records this distinction in `EvidenceJson`.
When a candidate supplies both dates, all supplied dates must match without contradiction. A match on one date cannot override a conflict on the other; any contradictory date pair prevents an exact deterministic link.

A composite key match is sufficient only when it resolves to a single same-request
charge event row. If it resolves to zero or more than one row, leave the document
unlinked or pending review.

SRN is not a charge-side signal in this corpus: `RocCharge` and `RocChargeEvent`
do not contain it. An archive filing's SRN may describe the parent filing, but it
must not be represented as corroborating the ROC charge row and is strictly excluded
from charge-side candidate matching.

### 3. Filing metadata

Match verified document metadata such as CIN, MCA form name, SRN, filing date,
financial year, director DIN, or GSTIN to a single compatible entity. This is an
explicit join from `McaFilingDocument` to its parent `McaFiling`: the document has
`FormType`, while filing metadata such as parsed CIN, company name, and SRN belongs
to the parent. CIN alone is only a request-scope check; it never creates a link.

### 4. Financial statement evidence

Link a financial filing only where the document identifies a reporting period and
the extracted statement/line item has a consistent period and value. The evidence
must name the exact PDF page and the workbook/entity source row.

### 5. Text/OCR-assisted candidates

Native PDF text is preferred. OCR runs only when native extraction is missing or
unusable. Text/OCR can create a `PendingReview` candidate when it produces a
stable identifier plus supporting context. It cannot auto-accept an association
based solely on a fuzzy company name, approximate amount, or semantic similarity.

## Unlinked-document policy

Every processed canonical filing PDF receives one terminal classification for a
given rule version:

| Outcome | Required behavior |
|---|---|
| `UnlinkedNoCandidate` | Preserve the document and record that no compatible same-request target exists |
| `UnlinkedAmbiguous` | Store candidate IDs/evidence; require reviewer selection or rejection |
| `UnlinkedInsufficientText` | Keep native-text/OCR diagnostics; do not invent a link |
| `UnlinkedOutOfScope` | Keep as a request document, excluded from current entity types |
| `InvalidDocument` | Record signature/readability failure and retain ingestion diagnostics |
| `Duplicate` | Resolve to the canonical filing PDF and show its link/review state, while retaining the original PDF and its extraction record |
| `Rejected` | Preserve the rejected candidate and reviewer reason; do not immediately recreate it under the same rule version |

The UI must expose an unlinked queue with filters for archive, form, classification,
reason, text availability, and reviewer state. Coverage reporting must distinguish
"unlinked by design" from "linker could not decide."

### Reviewer Rejection Persistence (`DocumentLinkDecisionService`)

`DocumentLinkDecisionService` is responsible for enforcing reviewer rejection
persistence across candidate generation runs. When evaluating candidates:
1. It queries prior decisions with status `Rejected` for the target request, matching:
   - `CanonicalFilingDocumentId`
   - `IngestionRunId`
   - `TargetEntityType`
   - `TargetEntityId`
   - `TargetField`
   - `LinkKind`
   - `RuleVersion`
   - `InputHash`
2. **Same-rule-version suppression**: If a rejected candidate matches the exact
   `RuleVersion` and `InputHash`, candidate regeneration is suppressed. The document
   retains its terminal `Rejected` state and is not placed back in the reviewer queue.
3. **Newer rule-version re-evaluation**: If a candidate is evaluated under a newer
   `RuleVersion` (e.g. improved extraction heuristics or revised tolerances) or
   with an altered `InputHash` (e.g. corrected OCR/text extraction),
   `DocumentLinkDecisionService` permits creating a new candidate with status
   `PendingReview`, enabling independent review while preserving the historical
   rejection record.

## Processing workflow

1. **Manifest.** Enumerate the outer ZIP, every nested archive, and every PDF.
   Store archive path, PDF name, byte length, SHA-256, duplicate relation, and
   extraction status. Do not modify the supplied archive.
   - **Manifest-time duplicate bypass**: Any PDF identified as a duplicate at
     manifest time (`DuplicateOfDocumentId != null`) completely bypasses the
     matching hierarchy. Only the canonical PDF (`DuplicateOfDocumentId == null`)
     receives a linking pass. All manifest-time duplicates resolve directly to
     the canonical PDF's linking and review outcome.
2. **Content extraction.** Obtain native text and filing metadata. Queue OCR only
   for non-text PDFs, with bounded retries and a durable failure state.
3. **Candidate generation.** Apply the ordered matching hierarchy against the
   request's active `IngestionRun` only. Persist that run on every link and build
   versioned `EvidenceJson` records.
4. **Decision.** Auto-accept only exact deterministic matches; write medium or
   conflicting candidates as `PendingReview`; write one of the unlinked terminal
   classifications where appropriate.
5. **Review.** Permit authorized reviewers to confirm, reject, relink, or classify
   out-of-scope. Preserve all prior review decisions and reasons.
6. **Presentation.** Surface document citations on dossier entities/findings and a
   reverse view on each document. A contradiction is evidence, not an automatic
   overwrite or a hold.
7. **Rerun.** A new rule version can create a new candidate pass without deleting
   historical decisions. Same-version reruns are idempotent through the unique
   constraint and input hash.

### Re-ingestion and late deduplication

Re-ingestion creates a new `IngestionRun`; it never retargets a historical link.
The reconciliation UI defaults to the active run, while prior-run links remain
auditable and may be compared or retired through an explicit reviewer workflow.

If a PDF is later identified as a duplicate after links already exist, a serializable
canonicalization transaction (`DocumentLinkCanonicalizationService`) locks both document
rows and their active links. It updates `CanonicalFilingDocumentId` to the canonical root
without changing the immutable `FilingDocumentId` evidence provenance. If this creates
the same active unique identity, one link remains active and the other is marked
`SupersededByDuplicate`, with a first-class `DocumentDataLinkSupersession` audit record.

#### `DocumentDataLinkSupersession` Schema Contract

The supersession relationship is modeled as a dedicated audit and referential integrity
table:

| Field | Type | Description |
|---|---|---|
| `DocumentDataLinkSupersessionId` | `long` (PK, Identity) | Primary key |
| `RequestId` | `long` (FK to `Requests`) | Request authorization boundary |
| `IngestionRunId` | `long` (Composite FK with `RequestId`) | Ingestion run scope |
| `SupersededLinkId` | `long` (FK to `DocumentDataLinks`) | The link whose status becomes `SupersededByDuplicate` |
| `SurvivingLinkId` | `long` (FK to `DocumentDataLinks`) | The link that remains active |
| `SupersededUtc` | `DateTime` | Exact UTC timestamp of the canonicalization transaction |
| `Actor` | `string` (max 100) | Actor triggering the supersession (e.g. `"System.Canonicalization"` or reviewer username) |
| `Reason` | `string` (max 500) | Business rationale / trigger (e.g. `"Late duplicate discovery: FilingDocument 456 -> 123"`) |
| `EvidencePreservationJson` | `string` | Complete JSON snapshot of the superseded link's `EvidenceJson` and review history |

Integrity rules:
- Foreign keys: Both `SupersededLinkId` and `SurvivingLinkId` reference `DocumentDataLinks` with `ON DELETE NO ACTION` (cascading deletes are forbidden).
- Self-reference check constraint: `SupersededLinkId <> SurvivingLinkId`.
- Referential immutability: Supersession records are permanent; deletion is disabled.
- Evidence preservation: The superseded link's complete evidence, confidence, review metadata, and match parameters—including its original pre-supersession status and canonical document ID—are snapshotted into `EvidencePreservationJson` strictly before mutating the entity.
- Transactional scope: The database implementation wraps canonicalization in a serializable transaction with row-level locks, validated by concurrent collision tests.

## Security and integrity controls

- Require `RequestId` equality for document, target entity, source run, and every
  read path. Return no link/citation across requests.
- Test both layers of this boundary: a direct database insert with a mismatched
  request/run pair must fail the composite foreign key, and an integration test
  must attempt a concrete cross-request polymorphic target and prove that
  `DocumentLinkTargetResolver` rejects it. Add concurrent tests proving the
  canonical unique index leaves exactly one active link.
- Treat PDF text, OCR text, file names, and document metadata as untrusted display
  content; encode output and cap stored evidence/quote lengths.
- Do not expose a raw storage path in the UI/API; use the existing authorized
  document-view/download path.
- Keep file hash, extractor version, rule version, and input hash on every
  auto-produced decision.
- Use database constraints plus concurrent integration tests; an application
  pre-query is not sufficient duplicate protection.

## Pilot deliverables

1. A read-only inventory for all 19 archives and 814 PDFs.
2. A deterministic charge-link pass, starting with the detailed charge workbook.
3. A financial-filing candidate pass using reporting period and PDF page evidence.
4. A reconciliation report containing totals for accepted, pending, ambiguous,
   duplicate, invalid, and each unlinked reason.
5. A reviewer sample of at least 20 auto-accepted links, 20 unlinked results, and
   every ambiguous/contradictory result before broad backfill.

### Pilot Deliverable 2: Coastal Corpus Deterministic Charge Linking Reconciliation

The deterministic charge-link pass over the canonical Coastal Projects Limited corpus (`COASTAL PROJECTS LIMITED Documents.zip`) against ingested ROC charge events (`U45203OR1995PLC003982.xls` and `U45203OR1995PLC003982-charge.xls`, representing 213 charges and 337 events) produces the following immutable partition across all 814 manifest entries:

| Outcome | Reason | Count | Notes |
|---|---|---|---|
| `ManifestDuplicateBypassed` | `ManifestDuplicate` | 169 | Byte-identical duplicate PDFs bypassing matcher hierarchy to canonical entries |
| `UnlinkedOutOfScope` | `NonChargeDocument` | 105 | Classified as constitutional, annual return, or non-charge filing |
| `AutoAccepted` | `ExactMatchBothDates` (41)<br>`ExactMatchEventDate` (82)<br>`ExactMatchFilingDate` (37) | 160 | Deterministic composite key matches with zero date contradictions |
| `PendingReview` | `DateMismatch` (10)<br>`DateContradiction` (4) | 14 | Candidates requiring reviewer adjudication |
| `UnlinkedNoCandidate` | `MissingChargeId` (359)<br>`ChargeNotFoundInWorkbook` (7) | 366 | 359 lack CID token; 7 generic files in mixed folder fallback |
| **Total** | | **814** | Complete partition of Coastal corpus |

#### 160/14 Reconciliation & Strict Date Contradiction Rule

Under strict date contradiction prevention, if a candidate filename supplies both an event date and a filing date, and one matches a charge event row in the workbook while the other contradicts the charge event row, the match is rejected as `DateContradiction` and routed to `PendingReview`.

This strict rule shifted exactly 4 entries from `AutoAccepted` to `PendingReview` (reconciling the earlier 164 Auto / 10 Review distribution to 160 Auto / 14 Review):
1. `70912_.../Charge Documents/ee0ce4b44acb7f00276dd4494bec1b70v1-Form CHG-1-050315-110814-ChargeId-10215822.pdf`: EventDate 11-08-2014 matches Modification event, but FilingDate 05-03-2015 contradicts workbook FilingDate 20-04-2015.
2. `70927_.../Charge Documents/715292c61e2799fd200d720521d44a3dv1-Form 8-260810-300610-ChargeId-10234873.pdf`: EventDate 30-06-2010 matches Creation event, but FilingDate 26-08-2010 contradicts workbook FilingDate 27-08-2010 (1 day delta).
3. `70928_.../Charge Documents/cbbc6e6caac8272496c1dc80f98db69bv1-Form 8-300410-260410-ChargeId-10158037.pdf`: EventDate 26-04-2010 matches Modification event, but FilingDate 30-04-2010 contradicts workbook FilingDate 03-05-2010 (3 days delta).
4. `70928_.../Charge Documents/d6e43786704b7bf513b6287edb431069v1-Form 8-050510-190310-ChargeId-10215822.pdf`: EventDate 19-03-2010 matches Creation event, but FilingDate 05-05-2010 contradicts workbook FilingDate 07-05-2010 (2 days delta).

## Acceptance criteria

- Every canonical PDF has exactly one current processing classification; duplicate
  PDFs resolve to their canonical document.
- Manifest-time duplicate PDFs (`DuplicateOfDocumentId != null`) completely bypass
  the matching hierarchy; only canonical PDFs receive a linking pass.
- Charge matching distinguishes between `EventDate` (instrument execution) and
  `FilingDate` (ROC registration), recording the exact date match mode in
  `EvidenceJson`. SRN is strictly excluded from charge-side matching.
- Late duplicate discovery canonicalization produces a `DocumentDataLinkSupersession`
  audit record linking `SupersededLinkId` to `SurvivingLinkId`, preserving an
  immutable snapshot of evidence and review history, and preventing cascading deletions.
- Every accepted/confirmed link has a request-scoped target and reproducible
  evidence locator.
- Every link names its exact ingestion run; active-run views do not silently mix
  superseded entities with current data.
- No document is auto-linked solely from CIN, file name, fuzzy name, or amount.
- The same corpus and rule version yield the same decisions and do not duplicate
  rows when processed concurrently.
- A cross-request target attempt is rejected.
- Unsupported target type codes and a mismatched request/run insert are rejected
  by database constraints; a concrete cross-request polymorphic target is rejected
  by the resolver integration test.
- Reviewer rejection survives rerun of the same rule version; candidate regeneration
  is suppressed under `DocumentLinkDecisionService` unless evaluated under a newer rule
  version or altered input hash.
- A document with unreadable text or no suitable entity remains visible and
  auditable as unlinked.
- The pilot reconciliation report separates exact links from review candidates and
  unlinked-by-design documents.

## Rollout sequence

1. Review/approve this plan and confirm the target request/database environment.
2. Create the schema migration in the designated migration lane; replay it on a
   fresh SQL Server test database.
3. Implement manifest and deterministic charge matching with focused integration
   tests.
4. Add extraction/OCR queue and unlinked classifications.
5. Add reviewer UI/API and dossier citation rendering.
6. Run the COASTAL pilot, review the sampled reconciliation set, tune only
   deterministic rules, then decide whether to backfill further corpora.
