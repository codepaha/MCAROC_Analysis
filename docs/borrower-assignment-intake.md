# Automated borrower assignment intake

At `/pre-login-reports`, upload a request document or paste request/email text and choose **Create assignment automatically**. The application records the assignment immediately; the existing durable worker recognizes it in the background. The assignment page displays requesting-branch details, borrower identity, addresses and directors/partners/trustees/proprietors, and exposes the exact three-section extraction JSON contract.

Company/LLP request intake now resolves identity against the local MCA master automatically in the background. An OCR-extracted CIN/LLPIN must exist in the master and match the recognized legal name and entity type. Without an identifier, the existing name resolver retrieves and ranks master records; only a unique, exact normalized name clearing the strict 0.99 score and 0.10 runner-up margin is auto-selected. A confirmed identity proceeds into the existing company-details fetch and report worker. An ambiguous, missing, mismatched or stale-master identity stays on the created assignment with ranked candidates for explicit selection. The resolution reason, candidates, score and chosen identifier persist in the assignment data. This intake policy does not switch on global post-login auto-selection.

Typing a borrower name in the assignment review page or existing manual form also searches the local MCA master database after three characters and shows registered names alongside CINs or LLPINs. Select the correct row to fill both fields; the server verifies the selected name and identifier again before requesting details. Foreign-company FCRNs are shown for reference but the current report-data service still requires a supported identifier. No external lookup or paid call is made while typing.

Supported uploads are PDF (maximum 10 pages), PNG/JPG screenshots, Word `.doc` (Word 97–2003) and `.docx`, `.eml` emails (body plus supported base64 request attachments), and `.txt`. Uploads are limited to 10 MB, images to 25 megapixels, and combined extracted/pasted text to 100,000 characters. Pasted screenshots are accepted by the upload form. Original requests stay under `App_Data/BorrowerAssignments`, outside the web root; source downloads and extracted JSON use the existing application authentication and batch-scoped access contract.

## Recognition configuration

`BorrowerAssignments:Model` defaults to `gemini-3.1-flash-lite`. The other allowed values are `gemma-4-31b-it` and `gemma-4-26b-a4b-it`. Set `BorrowerAssignments:ApiKey` through user secrets or the production secret store for the Gemini API, including either Gemma model. Without an API key, Gemini uses the existing `GoogleCloud:ProjectId` and `GoogleCloud:CredentialsPath` with `BorrowerAssignments:Location` (default `global`). No credentials are committed.

Screenshots are sent as images. PDFs use the existing native-text/OCR extractor and are also supplied to Gemini as original PDF content; Gemma receives rendered PDF pages. Other formats supply extracted text. Scanned PDFs require the existing `McaFilings:TesseractExePath`. Recognition has a 90-second model-call timeout, with the report worker's existing maximum of three attempts and increasing retry delay. An atomic job claim prevents duplicate queue deliveries from starting overlapping recognition.

The prompt treats request content as data, excludes vendor/routing details, preserves nulls and branch-code leading zeroes, and distinguishes PAN, CIN, GSTIN and eight-digit DIN by shape. The server rejects malformed/extra-key JSON and invalid identifiers. A printed list of company-type choices is not a selection. A nine-digit DIN is left null; it is never truncated to eight digits.

## Scope and missing information

Limited/Private Limited companies, LLPs and foreign companies retain MCA scope. Every other entity type—including partnership, proprietorship, trust, society, association, individual, HUF and arbitrary custom types—has litigation-only scope and never enters the MCA fetch path. An unrecognized borrower/type or missing MCA identifier leaves a created assignment awaiting details.

The supplied JSON contract deliberately has no LLPIN/foreign registry field. An LLPIN visible in extracted text can be retained separately as an MCA identifier without populating `cin`, and is checked against the master. When no supported identifier is available, the assignment remains created and can be completed from its assignment page. Foreign company registration numbers are not passed to an unsupported vendor endpoint: the existing report-data client accepts its current CIN/LLPIN formats only. Adding a foreign registry lookup requires a separately verified provider contract.

Litigation-only assignments await litigation results; they do not produce an implicit zero-case report from an uploaded request. Attach the existing litigation-tracker `.xlsx/.xls/.csv` export, or explicitly confirm that a completed search returned no cases, to queue the SBI report. The generated report contains borrower identity and litigation sections, omitting ROC profile, capital and MCA charge sections. Report edit/rerun retains its fixed litigation-only identity and source/extraction metadata. Intake does not automatically start a paid litigation search.

No database migration is required: pending intake and recognized metadata use `PreLoginReportJob.DataJson`; `AwaitingReview` already exists. Restart recovery, progress, history, failure capture and rerun use the existing worker and queue.

## Verification

Focused tests cover strict JSON shape/nulls, branch/PAN/DIN extraction, vendor exclusion, entity routing, upload bounds, email decoding, application authentication/antiforgery, litigation-only document sections, SQL-backed durable creation/completion/rerun and recognition failure recovery. Run the relevant `BorrowerAssignmentTests`, `PreLoginReportJobServiceTests`, `InstaFinancialsClientTests`, `PreLoginReportsControllerTests` and `PreLoginReportsMineRenderingTests` classes. Local SQL integration tests use the repository's test database configuration.
