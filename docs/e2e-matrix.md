# Post-login E2E matrix (#514)

A round of live end-to-end runs, one per company profile, each run with **no manual step** except the documented gates (approving an unlock). Post-login
E2E is "done" only when every profile has passed and its outputs have been reviewed. Three similar companies are not a test: on 2026-10-16 an LLP
failed at five points that the 10-13 runs and the Coastal demo never reached.

## Before the round

1. **Settings** (user-secrets on the machine that runs the portal):

   | Setting | Value for the round | Why |
   |---|---|---|
   | `IntegrationCapture:Directory` | an empty folder outside the repository, e.g. `E:\e2e-captures\<yyyymmdd>` | every reference-tool and litigation-API call is saved, redacted, for fixtures. **Only when the portal runs in Development** (`ASPNETCORE_ENVIRONMENT=Development`, as `dotnet run` does by default); in any other environment the setting is ignored |
   | `Pipeline:Policy:LitigationSearch`, `:LitigationAnalysis`, `:CourtUpdate` | `false` while the litigation service is down; `true` for profiles 6 and 7 | each run copies the policy when it is created, so set this **before** creating the requests |
   | `LitigationKeywords:UseGeneratedKeywords` | `true` | profiles 6 and 7 check the generated keyword list |

   Restart the portal after changing them.
2. **Pre-flight:** open `/Preflight` (sidebar → Pre-flight). Fix any failure first. The run is kept in the audit log, and each request's run
   record shows the last pre-flight made before it was created. Every row must pass, with these exceptions:
   - the litigation rows (litigation API, order-document host, court update), while litigation is off;
   - the MCA status rows may show **Warning: ALERT** (the peak-filing notice). Runs proceed through it, and it is what profile 8 needs. A **Fail**
     (MCA maintenance) still blocks the round: refreshes and first probes wait until it clears.
3. **Capture check:** the pre-flight itself calls the reference tool, so the capture folder must now hold files such as
   `…-reference-tool-getUpgradeStatusForCompanies.json`. If it is empty, capture isn't on (wrong environment, or the portal wasn't restarted):
   fix that before creating any request, or the round's calls won't be kept.
4. **Companies:** the list proposed for each profile (posted on #514) is approved, including which ones may spend a credit (at most 2 for the round).

## The profiles

| # | Profile | What it exercises | Passes when | Also covered offline by |
|---|---|---|---|---|
| 1 | Company, already probed, data under 24 hours old | ready path | exported without a refresh; MCA status not asked | `ReferenceToolReplayTests` profile 1 |
| 2 | Company, probed, data stale | `requestProbeDataUpdate` → wait → export | exactly one update request; the export follows (now, or when the refresh lands, as configured) | replay profile 2 |
| 3 | Never-probed company or LLP | `requestProbeData`; waits for the first probe | first-probe request (never the update); job waits, does not export empty data; exports once the probe lands | replay profiles 3+4+8 |
| 4 | LLP (LLPIN) | LLP MCA status, LLP sheets and parsers | LLP status asked (not companies); every LLP tab populated | replay profiles 3+4+8 |
| 5 | Locked company (1 credit) | unlock gate, approval pop-up and request-page button | job waits for approval with nothing spent; after approval exactly one credit; then the normal path | replay profile 5 |
| 6 | Renamed company (former names in MCA data) | parallel litigation search, former-name top-up | **on hold until the litigation service is back**: former names searched; no stage waits for good | `PipelineDeciderTests` |
| 7 | Name with a real acronym (HDFC-style) | spaced and dotted acronym keywords, no made-up initials | **on hold**: keyword list as `RegistryNameKeywordTests` expects | `RegistryNameKeywordTests` |
| 8 | During an MCA ALERT | ALERT proceeds, maintenance waits | run proceeds with the ALERT warning (run only if an ALERT is live) | replay profiles 3+4+8 and 8 |
| 9 | Name-only intake (no CIN) | identity resolution → auto-fetch | identity resolved (or one documented ambiguity choice), then the normal path | `PipelineResolveStageTests` |
| 10 | Large company (many filings and charges) | throughput; registry paging past 100 rows | **waits for #515**: registry lists every document (`listed == total`) | after #515 |

Run order: 1, 2, 9, 4, 3, 8 (only if an ALERT is live), 5, then 6, 7 and 10 when unblocked.

## For each run

1. Create the request as a client would (identifier, or name only for profile 9). Touch nothing else, apart from approving an unlock for profile 5.
2. Watch the request page; leave it alone. A stage that needs a person, other than the unlock approval, is a **failure of the run**: note it.
3. When the pipeline is done, open **Actions → Run record** on the request and check:
   - the pre-flight shown is this round's;
   - each stage's outcome and reason;
   - the fetch line (registry listed against total, failed downloads, warnings);
   - for profiles 6 and 7, the planned keyword list.
4. Download the dossier from the request page, **open it and read it**: every section present, figures plausible, property particulars shown.
5. Download the run record (**Download as Markdown**) into `artifacts/e2e-<yyyymmdd>/run-record-<request>.md`, tick "Opened and reviewed", and add notes.
6. Fill in the row below.

## After the round

- **Promote captures to fixtures:** for each call sequence worth keeping (anything the fixtures don't already show):
  `dotnet run --project MCAROC.Portal/Tools/FixturePromote -- --in=<capture folder> --out=MCAROC.Portal/MCAROC_Analysis.Tests/Fixtures/reference-tool/<scenario>-<yyyymmdd> --source="<company, profile, date>" [--actions=a,b]`.
  Then **read every file**, because the leak check cannot recognise an account id under a field name it does not know. Run `ReferenceToolReplayTests` and
  `IntegrationCaptureTests`, and open a PR. Replies marked "Not recorded" in an existing scenario's `source` should be replaced first.
- Clear `IntegrationCapture:Directory` when the round is over, and set the litigation policy back if you changed it.
- Post the filled-in table and the run records on #514. Each failure gets its own issue.

## Results

Round: _date_ · pre-flight: _result_ · run by: _name_

| # | Company | Identifier | Request | Outcome | Manual steps | Credits | Dossier reviewed | Notes / issues |
|---|---|---|---|---|---|---|---|---|
| 1 | | | | | | | | |
| 2 | | | | | | | | |
| 3 | | | | | | | | |
| 4 | | | | | | | | |
| 5 | | | | | | | | |
| 6 | | | | | | | | on hold (litigation service) |
| 7 | | | | | | | | on hold (litigation service) |
| 8 | | | | | | | | only if an ALERT is live |
| 9 | | | | | | | | |
| 10 | | | | | | | | waits for #515 |

## Known gaps going in

- Profiles 6 and 7 can't run until the litigation service answers again (refusing connections since about 2026-10-16). The pre-flight's
  litigation row shows when it is back.
- Profile 10 needs the registry paging fix from #515.
- The two follow-ups from #514 are not fixed yet: a job's attempt count also counts unlock-gate waits and restarts, and the approve-unlock box needs a
  page reload to appear. Note them if they show up, but they don't fail a run on their own.
