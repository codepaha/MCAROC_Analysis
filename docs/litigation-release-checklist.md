# Litigation release checklist (LIT-08)

Use this gate after LIT-01 through LIT-07 are merged, before enabling a BPR configuration in any environment.
It is intentionally a release checklist, not a way to bypass the normal migration, authentication, or
deployment controls.

## Automated gate

Run the focused litigation suite and require a clean result:

```powershell
dotnet test MCAROC.Portal/MCAROC_Analysis.Tests/MCAROC_Analysis.Tests.csproj --filter "FullyQualifiedName~Litigation"
```

`LitigationEndToEndAcceptanceTests` is the cross-boundary acceptance fixture. It uses only an in-process
HTTP handler and RFC 5737 address; it never calls BPR or needs a credential. It proves a representative
report through job completion, snapshot creation, CNR-first/CSP-not-a-key merge, all surfaced orders,
expired-order re-admission on a rerun, and the reconciled PDF/CSV model. The supporting focused suites cover
the negative and scale paths that a single happy-path fixture should not obscure:

| Gate | Focused coverage |
|---|---|
| Timeout, retry, vendor errors, ambiguous registration | `BprLitigationClientTests`, `LitigationSearchJobServiceTests` |
| All-order import, CNR/CSP identity, crash recovery and refetch | `LitigationCasePersistenceServiceTests`, `LitigationOrderDocumentServiceTests` |
| Request isolation, evidence-only Gemini output and client-visible failure state | `LitigationOrderChunkingTests`, `LitigationAnalysisPromptBuilderTests`, `LitigationTabAndControllerTests` |
| PDF/CSV reconciliation, no vendor-URL disclosure and large snapshots | `LitigationReportWireTests`, `LitigationReportArtifactsTests` |

Do not treat a skipped, pending, or unavailable SQL-backed suite as a pass. Resolve the test database
connection problem or obtain the equivalent hosted CI evidence for the exact release commit.

## Live-safe smoke

1. Apply the deployment's normal EF migration process and confirm all litigation workers start. Do not add a
   CRA worker for this feature.
2. Configure `BprLitigation:BaseUrl`, `Id`, and `SecretKey` through the deployment secret store/user secrets.
   Do not copy the Postman JWT or any vendor credential into source, logs, tickets, or this checklist.
3. As an `InternalReviewer`, use one disposable or expressly approved request. Start one litigation search,
   then poll `GET /Requests/{id}/Litigation/Status` until it is terminal. Confirm the status endpoint is
   `no-store`, states the import state, and leaves an error/failure reason visible if BPR fails.
4. For a completed search, confirm the snapshot reaches `Completed`, its case count matches the court grid,
   every returned order has a durable document lifecycle row, and the PDF and CSV reconcile to those totals.
5. Download one retained order and the ZIP. Verify both are request-scoped, authenticated, attachment-only,
   `no-store`, and do not reveal a vendor URL. Try the same document id under a different request and require
   a 404.
6. Start the evidence-grounded analysis only after import completes. Confirm every displayed analysis has its
   persisted case/order evidence, and pending/failed analysis remains visibly pending/failed rather than
   producing a conclusion.
7. Retain the release evidence: exact commit, migration result, focused/hosted test links, smoke request id
   (not vendor credentials), terminal status, reconciliation counts, and any failure capture.

## Stop conditions

Do not enable the integration when BPR is unconfigured, an import is incomplete, a reconciliation mismatch
exists, a request-isolation check fails, or a failure state is hidden. Escalate vendor contract changes,
credential rotation, report-format changes, or an unresolved registration attempt; the service deliberately
fails closed rather than issuing a possibly duplicate vendor search.
