# Issue #190: reviewer initiated company re-check

The owner activated this issue on 23 September 2026 and selected **automatic retrieval of fresh data**. A re-check targets the existing request ID. It must never create a second request for the same review.

## Product behavior

1. Show the latest completed ingestion run's **workbook source snapshot date** and its age on the request list and detail page. If the workbook did not contain a parsable date, say that the source date is unknown. `McaRequest.UpdatedDate`, the last ingestion time, and the reference-tool download time are not evidence of source freshness.
2. Show `Re-check this company` only to an authenticated `InternalReviewer` on an existing request with a canonical CIN or LLPIN. Use a CSRF-protected POST. Keep the action and its status scoped to that request. An analyst assignment must not grant the ability to trigger the reference-tool session.
3. Check the reference tool's company lifecycle before any workbook export. An unlocked company with a verified snapshot at most 24 hours old can reuse that snapshot; an older snapshot starts or joins the one durable company refresh from #229. Wait for provider readiness before retrieving workbooks. A locked or expired company waits for #266's approval-gated unlock; this action never spends a credit by itself.
4. Once the provider reports a ready snapshot, start a **new** auto-fetch attempt for the same request. Clear the previous `AutoFetchJob`'s workbook, ingestion and filing checkpoints and progress counters in an atomic admission step, while leaving old documents, ingestion runs, analysis runs, and filing batches intact. A retry of this new attempt retains only its own checkpoints. Concurrent clicks join the active attempt; neither a duplicate provider refresh nor a duplicate ingestion run is allowed.
5. Feed the newly fetched workbooks into the existing `IngestionOrchestrator` and analysis queue. The existing run lineage and calculation assurance process remain the authority. Show the previous result until the new run completes, with an explicit re-check-in-progress state. If fetching or validation fails, keep the previous completed run and show the failure.

## Implementation

#229 merged as `1058b51` and provides the durable company refresh gate. The reviewer POST uses a separate fresh-attempt admission method; ordinary Retry continues to preserve checkpoints. The re-check resets every workbook, ingestion, and filing checkpoint, plus counters and progress, on the existing job row. Manual-upload requests get their first auto-fetch job. It fetches workbooks only, so the existing filing batch is retained and RAG documents are not re-embedded. A provider snapshot within 24 hours may be reused, but the workbooks are exported again for the new attempt. The action requires the reference-tool freshness gate to be enabled. #266 still supplies the locked-company approval path.

## Acceptance checks for the action

- An existing unlocked request yields a later `IngestionRun` and matching `AnalysisRun`, with freshly downloaded source documents and unchanged request ID.
- Two POSTs and two workers cannot create two active fresh attempts for the request; two requests for the same company join one provider refresh.
- Restart during provider polling or workbook processing resumes without losing the action or reusing a previous attempt's checkpoints.
- Locked/expired, provider timeout, source identity mismatch, and failed workbook export leave the old completed result available and report the precise reason.
- Anonymous and analyst callers cannot start the action. A locked company cannot consume a credit without #266's approval path.
