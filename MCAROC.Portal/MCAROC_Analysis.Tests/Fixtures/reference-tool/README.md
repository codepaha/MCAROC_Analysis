# Live-call fixtures (#514)

Recorded calls to the reference tool (`reference-tool/`) and the litigation data service (`litigation-api/`). They are committed, unlike the
workbooks: they hold public company data only, and `IntegrationCaptureTests.Committed_live_fixtures_hold_nothing_that_signs_us_in_or_names_a_real_host`
fails CI if any file holds a token, session cookie, e-mail address, real host or IP address.

## Layout
One folder per scenario (`<what it shows>-<yyyymmdd>`), one file per call in the order made: `NN-<action>.json`.

| Field | Meaning |
|---|---|
| `url` | path on the placeholder host (`reference.test` / `litigation.test`) |
| `action`, `payload` | the signed payload, decoded, with sign-in and account fields replaced by `<redacted>` |
| `status`, `contentType`, `contentLength` | as received |
| `body` | the reply (JSON or text). `null` for a document (PDF, workbook, archive): only its type and size are recorded |

## Capturing a live run
1. In Development only, set `IntegrationCapture:Directory` (user-secrets) to an empty folder outside the repository.
2. Run the E2E profile. Every call is written there, already redacted (`CaptureHandler`, `CaptureRedactor`).
3. Read the files, copy the ones that show the scenario into a new folder here, and run the tests.

## Scenarios
- `never-probed-llp-20261016`: an LLP the tool had never probed, during an MCA ALERT. The update action returns 404, the first probe is `requestProbeData`,
  and the status poll goes REQUESTED → NO PENDING REQUEST. Transcribed from the 2026-10-16 live capture.
