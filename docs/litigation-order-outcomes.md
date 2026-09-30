# Litigation order-outcome classification

Epic #195, "Order Outcome Classification" deliverable. Classifies what each court order **grants or decides**,
so the chatbot can answer questions like "pull every order with a fine / a stay / a possession order"
**completely**, from a structured query instead of top-K retrieval.

## Taxonomy (`LitigationOrderOutcome`)

| Outcome | Meaning |
|---|---|
| `FinePenalty` | Imposes a fine, penalty or costs. `FineAmount` (₹) recorded only when the order states it. |
| `StayGranted` | Grants or continues a stay. |
| `StayVacated` | Vacates, lifts, sets aside or refuses a stay. Never labelled `StayGranted`. |
| `PossessionOrder` | Directs delivery or taking of possession of property. |
| `Injunction` | Grants an injunction (other than a stay). |
| `Dismissal` | Dismisses the case, petition or application. |
| `DisposedSettled` | Disposes of the matter as settled, withdrawn or otherwise finally disposed. |
| `InterimRelief` | Grants other interim relief. |
| `AdjournedNoSubstantiveOrder` | Only adjourns or lists the matter. |

An order can have several outcomes. The list follows the epic; it should be re-checked against a sample of real
Coastal orders once the NCLT corpus fix (#189) lands. Outcomes are stored by name, so extending the enum later
does not disturb existing rows.

## When it runs, and what it costs

Classification runs **inside a litigation AI analysis run** (auto or manual), under that run's existing paid-call
admission. There is no separate spend path or cap.

- One Gemini call per order document with retained, chunked text (`LitigationOrderClassifier`, up to 24 excerpts
  per order; a longer order is marked `EvidenceTruncated`).
- An order whose evidence and prompt hashes match an earlier classification is **carried forward without a call**,
  the same reuse rule the per-case analysis uses.
- Each result is persisted as soon as it exists, so an interrupted run never loses paid work.
- `NeedsAnalysisAsync` treats an unclassified order as outstanding work. Requests analysed before this feature
  therefore show analysis as available again; a run then only pays for their orders, because cases are reused.
- Cross-client report reuse (#291) copies classifications with the analysis.

## Fail-closed validation

The model's JSON is rejected (row persisted as `Failed`, raw response kept for audit) unless:

- status is `Completed` or `InsufficientEvidence`; `InsufficientEvidence` carries no outcomes;
- every outcome type is a taxonomy **name** (a numeric string like `"3"` is rejected), listed once;
- every outcome cites at least one `(pageNumber, chunkIndex)` that was actually in the prompt;
- `fineAmount` appears only on `FinePenalty` and is positive; confidence is `High|Medium|Low`.

A failed classification never replaces an earlier good one for the same order.

## Querying and chat

`LitigationOrderOutcomeQuery.FindAsync(requestId, outcomes)` returns every order whose current classification
has any of the outcomes, with case, court, date and fine amount, plus **coverage**: orders with retained text vs
orders classified.

In chat, `QuestionHintExtractor` recognises outcome questions. Unambiguous words such as "stay", "injunction",
"dismissed" and "adjourned" count on their own. Ordinary words such as "fine", "penalty", "possession" and
"settled" count only with a litigation cue like "order", "case", "court", "NCLT" or "petition". "Stay vacated",
"lifted" or "set aside" maps to `StayVacated`. For such questions, `RetrievalContextBuilder` adds:

- one **`O` source** per matching order (up to 60), citing the classification and linking to the order PDF;
- a **coverage note** (always, even with zero matches), which says so explicitly when the list is not exhaustive.

Chat citations to litigation orders (`O` and the existing `L` tags) now link to
`/Requests/{id}/Litigation/Orders/{documentId}/download`, but only for this request's currently retained PDFs.
