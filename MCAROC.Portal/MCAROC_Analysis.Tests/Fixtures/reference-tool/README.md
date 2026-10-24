# Live-call fixtures (#514)

Recorded calls to the reference tool (`reference-tool/`) and the litigation data service (`litigation-api/`), replayed by `FixtureReferenceTool`
in `ReferenceToolReplayTests`: the client is tested against what the tool really answered, not against replies written from our assumptions. They
are committed, unlike the workbooks: they hold public company data only, and
`IntegrationCaptureTests.Committed_live_fixtures_hold_nothing_that_signs_us_in_or_names_a_real_host` fails CI if any file holds a token, session
cookie, e-mail address, real host, IP address or encoding artifact.

## Layout
One folder per scenario (`<what it shows>-<yyyymmdd>`), one file per call in the order made: `NN-<action>.json`.

| Field | Meaning |
|---|---|
| `source` | where the recording came from (required). A call not recorded live says so here |
| `url` | path on the placeholder host (`reference.test` / `litigation.test`) |
| `action`, `payload` | the signed payload, decoded, with sign-in and account fields replaced by `<redacted>` (a numeric id by `0`) |
| `status`, `contentType`, `contentLength` | as received |
| `body` | the reply (JSON or text). `null` for a document (PDF, workbook, archive): only its type and size are recorded |
| `after` | optional: replay this reply only once that action has been called (the asset status after a paid unlock) |

Replay matches calls by action. Each action's replies are given in file order, and the last one repeats. The company identifier (`bid`) isn't
checked during replay, so tests can use their own identifiers. `Each_recording_was_made_for_the_company_its_identifier_hashes_to` checks each
scenario's own.

## Adding a scenario
- **From a live run:** in Development only, set `IntegrationCapture:Directory` (user-secrets) to an empty folder outside the repository and run the
  E2E profile. Every call is written there, already redacted. Then:
  `dotnet run --project Tools/FixturePromote -- --in=<that folder> --out=MCAROC_Analysis.Tests/Fixtures/reference-tool/<scenario> --source="<what and when>"`
- **From a browser trace** (a JSON list of `{url, method, status, action, req_payload, response}`, or an object of named calls): the same command
  with `--in=<trace file>`. Only the calls our client makes are kept (`--actions=` narrows further), redacted the same way. Nothing is written if a
  file would still leak.
- **Always read every file before committing.** The pattern check can't recognise an account id under an unfamiliar field name. The 2026-09-22
  traces carried `created_by` and `srcTeamId`, which are now on the redactor's list.

## Scenarios
- `unlocked-stale-coastal-20260922`: COASTAL PROJECTS LIMITED, unlocked, data a month old. Real `getAssetTeams`, `getDataStatus` (with `downloaded`
  and `download_status`), status poll, companies MCA check, and the `requestProbeDataUpdate` reply.
- `locked-unlock-tcs-20260922`: TATA CONSULTANCY SERVICES LIMITED, locked, unlocked for one credit. Real asset status before and after (`after: addAsset`),
  the pre-unlock MCA check and the `addAsset` reply. The preview's shape comes from the contract doc, because the trace kept no copy.
- `never-probed-llp-20261016`: NATURE EARTH KHARKHODA LLP, never probed, during an MCA ALERT. Real ALERT notice, the update action's 404, the
  `requestProbeData` reply and the status poll (REQUESTED → NO PENDING REQUEST). Its asset status and empty data status weren't recorded and are
  marked so; replace them with the next live capture.
