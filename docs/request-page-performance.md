# Request workspace performance and usability

The request page now delegates workbook, filing-summary, and litigation loading to
`Services/RequestDetails`. It still renders all sections in one response. Broad tab
lazy loading is deliberately deferred until representative live timings justify it.

## Reduced work

- Initial filing summaries use grouped SQL projections instead of loading every
  filing, PDF entity, and extraction JSON. Chat retains an authoritative document-ID
  set for citation validation. Full filing details use the existing paged endpoint.
- The timeline uses the already-loaded workbook graph, preserving its ingestion
  selection and avoiding a second set of queries.
- Read-only domain queries use no tracking and propagate request cancellation.
- Litigation property matching reuses the selected profile, establishments, and
  charges instead of loading historical workbook generations by request ID.
- `Server-Timing: request-model;dur=...` and an information log expose model-loading
  duration. This excludes Razor rendering, network transfer, and browser rendering.

## Consistency

An optimistic publication fence reads the ingestion, analysis ID/status, filing
batch, litigation snapshot/search hash, and litigation analysis ID/status before
and after assembly. If publication changes during assembly, the page returns HTTP
409 with a reload action instead of rendering mixed generations. This is a
publication check, not a database snapshot transaction: processing progress and
mutable case metadata may still advance within the same generation.

Document browsing carries the selected batch ID through filter and pagination
links. SQL verifies the batch belongs to the requested request. Calls without a
batch ID preserve the existing latest-authoritative-batch behavior. Analyst route
restrictions remain unchanged.

## Measurement

Baseline: `2b3f22f` Details action. Synthetic fixture: one analyzed company using
the existing golden-master seed plus 1,000 filings/PDFs, no live litigation
snapshot, no providers, no background workers. Four iterations per implementation,
with a fresh DbContext each time and a shared dossier cache. The initial iteration
includes cold compilation/cache effects; warm medians use the remaining three.
Both actions were rendered through the same Razor test harness and updated view,
so these results isolate the loading refactor, not a before/after visual comparison.

| Measurement | Baseline | Refactored |
|---|---:|---:|
| Warm SQL reads | 72 | 60 |
| Tracked entities | 2,039 | 1 |
| Warm median model loading | 2,740 ms | 1,143 ms |
| Warm median model + Razor rendering | 3,093 ms | 1,447 ms |
| First iteration model + Razor | 13,475 ms | 14,883 ms |
| Rendered HTML | 289,604 bytes | 289,604 bytes |

These are local diagnostic measurements, not a production SLA. SQL Express
reported `RESOURCE_SEMAPHORE` waits during the first unrestricted attempt. The
completed comparison used identical, **test-only** `MAX_GRANT_PERCENT = 0,
MAXDOP 1` hints on SELECTs. No server configuration or production query hints were
changed. Cold startup and HTML payload were not improved. Measure a populated
litigation request and normal deployed hardware before choosing further deferral.

A final repeat after status-copy changes retained 60 warm reads / 1 tracked entity;
warm total times ranged from 468 to 1,314 ms (median 990 ms). That variability is
another reason to treat timings as diagnostic, while query/entity counts are stable.

To reproduce in PowerShell from this worktree, explicitly select a disposable
local test database, then run:

```powershell
$env:MCAROC_TEST_CONNECTION = 'Server=.\SQLEXPRESS;Database=MCAROC_RequestPage_Perf_20261004;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False'
python scripts/prepare-request-page-baseline.py 2b3f22f
$env:MCAROC_PROBE_LOW_MEMORY = '1' # optional; use the SAME setting in both runs
$env:MCAROC_PAGE_PROBE = "$PWD/tmp/page-before"
dotnet test MCAROC.Portal/MCAROC_Analysis.Tests --filter FullyQualifiedName~RequestPagePerformanceTests -p:RequestPageBaseline=true
$env:MCAROC_PAGE_PROBE = "$PWD/tmp/page-after"
dotnet test MCAROC.Portal/MCAROC_Analysis.Tests --filter FullyQualifiedName~RequestPagePerformanceTests
Remove-Item Env:MCAROC_PAGE_PROBE, Env:MCAROC_PROBE_LOW_MEMORY
```

Each run adds a synthetic request; it does not delete existing data. The probe
reports skipped when not opted in, and never asserts a machine-dependent timing
threshold. Outputs contain timing JSON and rendered synthetic HTML.

## Usability and validation

- Assessment label and readable status text; Company/LLP identifier labels.
- Clear report action, request breadcrumb, and expandable missing-sheet disclosure.
- Explicit tab/panel labels, keyboard navigation, visible focus, and independently
  scrolling tabs. On smaller screens the tall company header no longer stays fixed
  over the reading area.
- Document fetches announce progress, prevent overlapping requests, reject error
  and login redirect responses, preserve current content on failure, and support retry.
- No new paid actions, schema migrations, or permissions.

Focused SQL/Razor suite: 72 passed, covering publication races, batch ownership,
timeline parity, golden-master metrics, litigation filters and snapshots, citation
rendering, and analyst boundaries. Navigation JavaScript: 19 passed, including
fetch failure, redirect, overlap, and batch-URL tests. Browser review uses rendered
synthetic Razor HTML plus the application assets; it does not establish live portal
authentication, provider readiness, or end-to-end report delivery.
Final header render checks and the performance probe also passed (4 checks). Desktop
and mobile browser inspection found no document-wide horizontal overflow; keyboard
ArrowRight activated the next tab. Screenshots are under `tmp/page-preview/`.
