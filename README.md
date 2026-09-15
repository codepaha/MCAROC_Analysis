# MCA ROC Analysis Portal

ASP.NET MVC + EF Core + SQL Server portal for MCA/ROC company analysis — ingests ROC/charge workbooks and
MCA filing archives, runs deterministic + AI-assisted analysis, and produces a client-facing due-diligence
dossier. Also generates standalone pre-login reports (no document upload) from the InstaFinancials API.

This is the first setup doc for the repo — if you're the dev team receiving this project, start here.

## Prerequisites

- **.NET SDK 10.0** (see `TargetFramework` in `MCAROC.Portal/MCAROC_Analysis/MCAROC_Analysis.csproj`)
- **SQL Server** — SQLEXPRESS works for local dev (`.\SQLEXPRESS`, Windows-integrated auth by default).
  SQL Server Express creates new databases with `AUTO_CLOSE ON`, which closes the database (and pays a full
  recovery cost on the next query) every time the connection pool drains to zero — this can stall a portal
  request long enough to hit the default 30s SQL command timeout, surfacing as a generic
  `TaskCanceledException` unrelated to the report's size. After the app/tests have created
  `MCAROC_Analysis`/`MCAROC_Analysis_Test` on first run, disable it once per machine:
  `sqlcmd -S .\SQLEXPRESS -Q "ALTER DATABASE MCAROC_Analysis SET AUTO_CLOSE OFF; ALTER DATABASE MCAROC_Analysis_Test SET AUTO_CLOSE OFF;"`
- **Tesseract OCR** installed locally — the MCA filings pipeline OCRs any PDF page without usable native
  text (`McaFilings:TesseractExePath`, default `C:\Program Files\Tesseract-OCR\tesseract.exe`)
- A **Google Cloud project** with Vertex AI enabled, and a service-account JSON key file with access to it
  (used for Gemini calls across chat, document extraction, cross-section analysis, and #164's calculation
  assurance — same project/credentials for all of them)
- An **InstaFinancials API key**, only if you need pre-login reports to actually fetch real company data

## Configuring secrets — where they go

**Nothing sensitive is ever committed to `appsettings.json` or `appsettings.Development.json`.** Both
files are tracked in git and are safe to read, but every real credential below is deliberately left empty
(or absent) in them — the app is designed to fail closed (an empty key, an unconfigured feature that just
does nothing) rather than accidentally ship with a stand-in value.

### Local development — `dotnet user-secrets`

Run these from `MCAROC.Portal/MCAROC_Analysis/` (the project already has a `UserSecretsId` set in its
`.csproj`, so no extra setup is needed):

```
dotnet user-secrets set "GoogleCloud:ProjectId" "your-gcp-project-id"
dotnet user-secrets set "GoogleCloud:CredentialsPath" "C:\path\to\your-service-account-key.json"
dotnet user-secrets set "InstaFinancials:ApiKey" "your-instafinancials-api-key"
```

Check what's currently set with `dotnet user-secrets list`. Secrets live outside the repo entirely (in
your Windows profile, not the project folder), so there's no risk of committing them by accident.

### A shared / deployed environment (staging, the dev team's own server, CI, etc.)

`user-secrets` is a **local-machine-only** mechanism — it does not transfer with the repo and isn't
available outside `dotnet run`/Visual Studio. For any environment other than your own machine, set the
same keys as **environment variables**, using ASP.NET Core's standard `Section__Key` naming (double
underscore in place of the JSON `:`):

```
GoogleCloud__ProjectId=your-gcp-project-id
GoogleCloud__CredentialsPath=/path/to/service-account-key.json
InstaFinancials__ApiKey=your-instafinancials-api-key
ConnectionStrings__Default=Server=...;Database=...;...
InternalAuth__ReviewerUsername=...
InternalAuth__ReviewerPasswordHash=...
InternalAuth__ReviewerDisplayName=...
```

On Azure App Service (or similar), set these as Application Settings in the portal/CLI — same
`Section__Key` naming, and changing one there triggers an app recycle automatically. Wherever you host it,
after setting or rotating any of these, **verify with a real request** (a login attempt, a report
generation) rather than assuming the new value is live — most config providers here are read once at
process startup, not watched for changes.

## Configuration reference

Every key the app actually reads, grouped by section — not a curated subset. Sizes are converted to
human-readable units alongside the raw byte value actually in `appsettings.json`, so you can sanity-check
either.

### Database

| Key | Required? | Default | What it's for |
|---|---|---|---|
| `ConnectionStrings:Default` | **Yes**, in every environment except local dev (already set in `appsettings.Development.json`) | none | The SQL Server connection string |

### Google Cloud / Vertex AI

| Key | Required? | Default | What it's for |
|---|---|---|---|
| `GoogleCloud:ProjectId` | **Yes**, once any AI-backed feature is actually used (chat, extraction, analysis, #164) | none — throws at first use if missing | Vertex AI project |
| `GoogleCloud:CredentialsPath` | **Yes**, same trigger as above | none — throws at first use if missing | Path to the GCP service-account key file |
| `GoogleCloud:Location` | No | `us-central1` | Vertex AI region |

### InstaFinancials (pre-login reports)

| Key | Required? | Default | What it's for |
|---|---|---|---|
| `InstaFinancials:ApiKey` | **Yes**, only for pre-login reports | none — throws when a report is actually requested | InstaFinancials API auth |
| `InstaFinancials:BaseUrl` | No | already set to the real endpoint | InstaFinancials API base URL |
| `InstaFinancials:DaysToIgnore` | No | `888` | How many days old a company's InstaFinancials record can be before it's treated as stale and re-fetched |

### MCA filings (OCR)

| Key | Required? | Default | What it's for |
|---|---|---|---|
| `McaFilings:TesseractExePath` | No | `C:\Program Files\Tesseract-OCR\tesseract.exe` | OCR fallback for scanned PDF pages |
| `McaFilings:MinCharsPerPageForNativeText` | No | `80` | Below this many extracted characters, a PDF page is treated as scanned/unreadable and sent to OCR instead of trusted as native text |

### #164 Calculation assurance

See `docs/calculation-assurance-runbook.md` before changing any of these in a real environment — flipping
`Mode` has real consequences (it can start holding report downloads).

| Key | Required? | Default | What it's for |
|---|---|---|---|
| `CalculationAssurance:Mode` | No | `Off` (fully inert) | `Off` / `ObserveOnly` / `Enforced` — see the runbook |
| `CalculationAssurance:AiAuditEnabled` | No | `true` | Whether the AI second-line review worker runs at all (independent of `Mode`, as long as `Mode` isn't `Off`) |
| `CalculationAssurance:AiAuditMaxAttempts` | No | `3` | Retries before an AI audit run is marked `SkippedAiUnavailable` |
| `CalculationAssurance:AiAuditTimeoutSeconds` | No | `60` | Hard timeout on a single Vertex AI call for this feature — the worker's lease duration is derived from this, so raising it also raises the lease automatically |
| `CalculationAssurance:AiAuditMaxLedgerRowsPerCall` | No | `150` | Caps how many ledger rows are sent to the model in one prompt |
| `InternalAuth:ReviewerUsername` / `ReviewerPasswordHash` / `ReviewerDisplayName` | No, until you want anyone to be able to sign in to `/internal/calc-audit` | all empty — nobody can sign in | Reviewer login — see the runbook for how to generate the password hash |

### Large archive upload (#169)

| Key | Required? | Default | What it's for |
|---|---|---|---|
| `LargeArchiveUpload:Enabled` | No | `false` | Master switch — resumable >2GB MCA filing archive upload |
| `LargeArchiveUpload:MaxArchiveSizeBytes` | No | `5368709120` (5 GiB) | Largest accepted uploaded archive |
| `LargeArchiveUpload:ChunkSizeBytes` | No | `67108864` (64 MiB) | Upload chunk size |
| `LargeArchiveUpload:MaxConcurrentUploads` | No | `1` | Global concurrent-upload limit |
| `LargeArchiveUpload:MaxConcurrentUnpacks` | No | `1` | Global concurrent-unpack limit |
| `LargeArchiveUpload:MaxUncompressedSizeBytes` | No | `21474836480` (20 GiB) | Largest accepted uncompressed archive contents |
| `LargeArchiveUpload:MaxPdfCount` | No | `10000` | Largest accepted number of PDFs in one archive |
| `LargeArchiveUpload:MaxIndividualPdfSizeBytes` | No | `250000000` (~238 MiB) | Largest accepted single PDF within an archive |
| `LargeArchiveUpload:MinFreeDiskHeadroomBytes` | No | `10737418240` (10 GiB) | An upload is refused if less than this much disk space is free — check this against your actual disk before enabling on a real machine |

## Running locally

```
cd MCAROC.Portal/MCAROC_Analysis
dotnet ef database update   # applies any pending migrations
dotnet run
```

## Running tests

```
cd MCAROC.Portal
dotnet test
```

Requires a local `.\SQLEXPRESS` instance — the test suite runs against a real SQL Server database
(`MCAROC_Analysis_Test`, created automatically on first run), not an in-memory provider.

## Further reading

- `docs/calculation-assurance-runbook.md` — rollout procedure, reviewer-credential setup, and what to
  check before enabling #164's calculation assurance / delivery gate in a real environment.
- `docs/document-data-linking-plan.md` — design for the (in-progress, not yet wired into the live app)
  document-to-data linking feature.
- `AGENT_CHANNEL.md` — the running coordination log between everyone (human or agent) who has worked on
  this repo; useful for "why does this exist" context on recent changes.
