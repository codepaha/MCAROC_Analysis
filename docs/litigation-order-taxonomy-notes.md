# Litigation Order Outcome Taxonomy & Corpus Usability Notes

**Issue Reference:** #337 (Epic #195)  
**Evaluator:** Antigravity  
**Date:** 2026-10-01 (IST)  
**Fixture:** `MCAROC.Portal/MCAROC_Analysis.Tests/Fixtures/litigation_order_outcomes_coastal_sample.json` (66 hand-labelled orders)

---

## 1. Executive Summary

As part of Epic #195, a stratified evaluation set of **66 real court orders** from the Coastal Projects litigation corpus was sampled across 6 judicial and administrative forums. Each order was hand-labelled against the 9-outcome `LitigationOrderOutcome` taxonomy, recording operative order excerpts, exact fine amounts, classification confidence, OCR/text extraction quality, and evidence citations.

The goals of this empirical evaluation were:
1. Verify the sufficiency and precision of the 9-outcome taxonomy defined in PR #332 (`LitigationOrderClassification`).
2. Audit real-world text extraction quality across court forums (native digital text vs. OCR vs. crawler failures).
3. Investigate scraper duplication patterns (specifically in insolvency/tribunal forums).
4. Identify real-world taxonomy gaps to inform future schema migrations.

---

## 2. Forum Sample Breakdown & Text Usability

The 66 evaluated orders span the following judicial forums:

| Forum | Sample Count | Primary Text Quality | Usability Assessment |
|---|---|---|---|
| **High Court** | 20 | NativeClean (95%), ScannedOcr (5%) | **Excellent.** Digital PDF text generated directly by High Court registry systems. High lexical clarity; minor CP1252/Unicode curly quote artifacts (`\u2018`, `\u2019`, `\u201c`, `\u201d`) easily handled. |
| **District Court** (ecourts) | 14 | Mixed Native / OCR | **Good to Fair.** Mixed digital orders and scanned order sheets with embedded OCR layers. Occasional OCR typos (e.g. "arc" for "are", broken numeric figures), but operative paragraphs, sections, and fine amounts (₹1,000, ₹10,000) remain reliably extractable. |
| **Supreme Court** | 12 | NativeClean (100%) | **Excellent.** High-precision digitally rendered court orders. Clean structuring with explicit operative directions, deposit conditions, and cost penalties. |
| **NCLT** (National Company Law Tribunal) | 10 | NativeClean (100%) | **Excellent text quality, severe scraper duplication.** Regular layout (Coram, Appearance of Counsel, Order). See Section 3 for the duplication anomaly. |
| **NCLAT** (Appellate Tribunal) | 8 | NativeClean (100%) | **Excellent.** Appellate orders disposing appeals, granting interim protection, or issuing directions to the Resolution Professional / Committee of Creditors. |
| **CESTAT** (Customs, Excise & Service Tax) | 2 | CrawlerArtifact (100%) | **Unusable as judicial orders.** 100% web crawler artifacts. See Section 4. |
| **Total** | **66** | | |

---

## 3. Scraper Duplication Anomaly in NCLT

### Finding
In the Coastal Projects NCLT corpus, there are **221 order files**, but only **15 unique orders** exist.
Specifically, **118 files are byte-for-byte identical copies of a single 3-page order sheet** (`TP 255/CTB/2019`, Order date: 2022-02-10).

### Root Cause
In corporate insolvency proceedings before the NCLT, numerous Interlocutory Applications (IAs) are filed (e.g. applications by the Resolution Professional, operational creditors, and financial creditors). When the upstream crawler scraped the case portal, it queried case history per IA listing and repeatedly downloaded the entire day's combined daily order sheet for every listed IA rather than isolating unique order documents.

### Ingestion & Pipeline Implications
1. **Sha256 / Content Hash De-duplication is Mandatory:** `LitigationOrderClassifier` and `LitigationOrderOutcomeQuery` hash chunk text (`EvidenceHash` and `PromptHash`). This prevents invoking LLM inference 118 times for identical order sheets.
2. **UI & Search Clutter Prevention:** Without de-duplication at query/retrieval time, top-K search and chat retrieval would be swamped by redundant identical chunks. The request-level deduping in `LitigationOrderOutcomeQuery` ensures each unique order document appears once.

---

## 4. Crawler Artifacts vs. Judicial Orders (CESTAT)

### Finding
The two CESTAT samples in the corpus (Sample 65 and Sample 66) represent crawler pipeline failures rather than court orders:
- **Sample 65:** The scraper captured and rendered an HTML table dump of the CESTAT web portal case status table rather than downloading the judicial order PDF.
- **Sample 66:** The scraper received an HTTP 404 response from the tribunal portal and converted the 404 error HTML page into a PDF document.

### Recommendation
The classification pipeline must handle crawler artifacts gracefully:
- Orders that contain portal navigation markup, HTTP error messages, or table dumps with zero judicial findings must be classified as `LitigationAiAnalysisItemStatus.InsufficientEvidence` (or rejected fail-closed) with `ExpectedConfidence = null` and empty outcomes, rather than forcing a speculative classification.

---

## 5. Taxonomy Validation & Negation Semantics

All 66 samples were evaluated against the 9 enum values of `LitigationOrderOutcome`:
1. `AdjournedNoSubstantiveOrder` (25 samples — 38%): By far the most common procedural order in Indian courts (re-listing, adjournment for counter-affidavit, awaiting service).
2. `DisposedSettled` (10 samples): Formal disposal of writ petitions, company appeals, or compromise decree.
3. `FinePenalty` (9 samples): Exact amounts recorded:
   - Routine costs: ₹500 (Sample 25), ₹1,000 (Samples 40, 41, 57, 58), ₹5,000 (Samples 23, 24), ₹10,000 (Samples 39, 42).
   - All 9 samples with fine amounts strictly correlate with the `FinePenalty` outcome.
4. `InterimRelief` (9 samples): Grant of interim protection, status quo orders, or direction to deposit partial amounts.
5. `StayGranted` (7 samples): Explicit stay of proceedings or recovery (Samples 26, 27, 28, 42, 54, 55, 56).
6. `Dismissal` (7 samples): Dismissal for default, non-prosecution, or dismissal on merits.
7. `Injunction` (6 samples): Temporary injunction restraining alienation of assets or encumbrance of properties.
8. `StayVacated` (4 samples): Explicit vacation of stay / receiver discharge (Samples 19, 20, 21, 22).
9. `PossessionOrder` (3 samples): Orders directing delivery of possession or appointing court receivers/commissioners.

### Strict Negation Enforcement
A critical requirement identified in Epic #195 is handling negation:
- Orders where an interim stay is **vacated** (e.g. Sample 19: receiver appointment vacated, petition disposed) must be classified as `StayVacated` and **never** `StayGranted`.
- In the 66-sample dataset, `StayGranted` and `StayVacated` are strictly mutually exclusive for the same relief across all samples.
- The unit test `LitigationOrderOutcomeFixtureTests.Fixture_StrictNegationCases_StayVacatedAndStayGranted_MutuallyExclusive` enforces this invariant.

---

## 6. Identified Taxonomy Gaps & Proposed Future Extensions

While the 9 existing taxonomy values cover core outcomes, analysis of real orders revealed 6 recurring legal events that currently have no dedicated enum value and are either collapsed into `ProceduralDirections` or forced into `InterimRelief`. 

When the next migration window opens (in Claude's migration lane), consider proposing these 6 extensions:

1. **`NoticeIssued`**:
   - *Frequency:* Extremely common (present in ~40% of preliminary High Court / Supreme Court orders).
   - *Description:* Formal issuance of notice to respondents/defendants (e.g., "Issue notice returnable in four weeks; dasti notice permitted").
   - *Current Workaround:* Swallowed under `ProceduralDirections` or `AdjournedNoSubstantiveOrder`.

2. **`CondonationOfDelayAllowed`**:
   - *Frequency:* Frequent in appellate and limitation applications (Section 5 Limitation Act).
   - *Description:* Court condones delay in filing appeals or applications, often subject to cost payment.
   - *Current Workaround:* Split between `FinePenalty` (if costs imposed) and `ProceduralDirections`.

3. **`ArbitratorAppointed`**:
   - *Frequency:* Prevalent in commercial and corporate disputes (Section 11 Arbitration & Conciliation Act 1996).
   - *Description:* High Court or Supreme Court appoints a sole arbitrator or arbitral tribunal to adjudicate disputes.
   - *Current Workaround:* DisposedSettled with descriptive text in AI summary.

4. **`BailOrSurety`**:
   - *Frequency:* Routine in criminal / Section 138 NI Act (cheque bounce) cases involving directors.
   - *Description:* Grant or cancellation of bail, anticipatory bail, or direction to furnish surety bonds.
   - *Current Workaround:* Uncomfortably labelled as `InterimRelief`.

5. **`InsolvencyResolution`**:
   - *Frequency:* Central to NCLT / IBC matters.
   - *Description:* Specific insolvency milestones: admission of Section 7/9/10 petition, moratorium declaration under Section 14, appointment of IRP/RP, or approval of resolution plan under Section 31.
   - *Current Workaround:* Split between `DisposedSettled` and `InterimRelief`.

6. **`WarrantIssued`**:
   - *Frequency:* Encountered when parties fail to appear in criminal/magistrate court proceedings.
   - *Description:* Issuance of bailable warrant (BW) or non-bailable warrant (NBW) to secure personal attendance.
   - *Current Workaround:* ProceduralDirections.

---

## 7. Conclusion

The committed fixture `litigation_order_outcomes_coastal_sample.json` and its companion test suite `LitigationOrderOutcomeFixtureTests` provide a rigorous, grounded baseline for evaluating automated order outcome classifiers. The current 9-outcome taxonomy is sound and covers all major dispositive and interim reliefs, with clear separation of fine amounts and strict negation boundaries.
