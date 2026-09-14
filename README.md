# MCA ROC Analysis Portal

ASP.NET MVC + EF Core + SQL Server portal for MCA/ROC company analysis — ingests ROC/charge workbooks and
MCA filing archives, runs deterministic + AI-assisted analysis, and produces a client-facing due-diligence
dossier. Also generates standalone pre-login reports (no document upload) from the InstaFinancials API.

This is the first setup doc for the repo — if you're the dev team receiving this project, start here.

## Prerequisites

- **.NET SDK 10.0** (see `TargetFramework` in `MCAROC.Portal/MCAROC_Analysis/MCAROC_Analysis.csproj`)
- **SQL Server** — SQLEXPRESS works for local dev (`.\SQLEXPRESS`, Windows-integrated auth by default)
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

| Key | Required? | Default | What it's for |
|---|---|---|---|
| `ConnectionStrings:Default` | **Yes**, in every environment except local dev (already set in `appsettings.Development.json`) | none | The SQL Server connection string |
| `GoogleCloud:ProjectId` | **Yes**, once any AI-backed feature is actually used (chat, extraction, analysis, #164) | none — throws at first use if missing | Vertex AI project |
| `GoogleCloud:CredentialsPath` | **Yes**, same trigger as above | none — throws at first use if missing | Path to the GCP service-account key file |
| `GoogleCloud:Location` | No | `us-central1` | Vertex AI region |
| `InstaFinancials:ApiKey` | **Yes**, only for pre-login reports | none — throws when a report is actually requested | InstaFinancials API auth |
| `InstaFinancials:BaseUrl` | No | already set to the real endpoint | InstaFinancials API base URL |
| `McaFilings:TesseractExePath` | No | `C:\Program Files\Tesseract-OCR\tesseract.exe` | OCR fallback for scanned PDF pages |
| `CalculationAssurance:Mode` | No | `Off` (fully inert) | #164 calculation assurance — see `docs/calculation-assurance-runbook.md` before ever changing this |
| `InternalAuth:ReviewerUsername`/`ReviewerPasswordHash`/`ReviewerDisplayName` | No, until you want anyone to be able to sign in to `/internal/calc-audit` | empty — nobody can sign in | #164 reviewer login — see the runbook above for how to generate the password hash |
| `LargeArchiveUpload:Enabled` | No | `false` | Resumable >2GB MCA filing archive upload |

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
