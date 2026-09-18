using System.Security.Cryptography;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Runs one <see cref="AutoFetchJob"/> end to end. Given only a CIN/LLPIN it:
/// <list type="number">
/// <item>checks the reference-tool session is alive (fails fast with an actionable message if not);</item>
/// <item>exports the corporate + charge workbooks and stores them as the request's ROC / charge documents;</item>
/// <item>runs <see cref="IngestionOrchestrator"/> and enqueues the rule-engine + AI analysis exactly as
/// the manual New-request flow does;</item>
/// <item>lists every filing PDF in the tool's reference-documents registry, downloads them in parallel
/// (one file at a time — the tool's own zip export caps at 50 MB), packages them into the nested-zip
/// layout the MCA filings pipeline unpacks, and enqueues that batch — OCR, Gemini extraction and
/// chunking/embedding then follow automatically.</item>
/// </list>
/// Every stage records a checkpoint on the job row, so a job re-run after a crash or a Retry skips the
/// stages whose output already exists and re-uses PDFs already staged on disk.</summary>
public sealed class AutoFetchJobService(
    AppDbContext db,
    ReferenceToolClient client,
    IOptions<ReferenceToolOptions> options,
    FileValidationService fileValidation,
    IngestionOrchestrator orchestrator,
    AnalysisQueue analysisQueue,
    FilingProcessingQueue filingQueue,
    IStorageReservationManager reservations,
    IWebHostEnvironment env,
    ILogger<AutoFetchJobService> logger)
{
    /// <summary>Fallback per-file size estimate used when the registry gives no declared size for a
    /// document or an attachment — only for sizing the up-front storage reservation and the plan-time
    /// aggregate-cap truncation; the live download-time cap (see <see cref="FetchFilingsAsync"/>) is what
    /// actually bounds real disk usage regardless of how good this estimate is.</summary>
    private const long FallbackPerFileEstimateBytes = 2_000_000L; // 2 MB
    private readonly ReferenceToolOptions _opts = options.Value;
    private static readonly TimeSpan ProgressFlushInterval = TimeSpan.FromSeconds(2);

    // ── Creation ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates (or, for a request that already has one, resets) the job row for a request and
    /// returns it. The caller enqueues it — separated so the controller can save the request first.</summary>
    public async Task<AutoFetchJob> CreateOrResetJobAsync(McaRequest request, bool includeFilings, int maxDocumentsPerSection, CancellationToken ct)
    {
        var cin = (request.Cin ?? request.Llpin ?? "").Trim().ToUpperInvariant();
        if (cin.Length == 0) throw new InvalidOperationException("A CIN or LLPIN is required to auto-fetch.");

        var job = await db.AutoFetchJobs.FirstOrDefaultAsync(j => j.RequestId == request.RequestId, ct);
        if (job is null)
        {
            job = new AutoFetchJob { RequestId = request.RequestId, CreatedUtc = DateTime.UtcNow };
            db.AutoFetchJobs.Add(job);
        }
        job.Cin = cin;
        job.Bid = ReferenceToolClient.ComputeBid(cin);
        job.IncludeFilings = includeFilings;
        job.MaxDocumentsPerSection = Math.Max(0, maxDocumentsPerSection);
        job.Status = AutoFetchJobStatus.Queued;
        job.ProgressPercent = 0;
        job.StatusMessage = "Queued.";
        job.FailureReason = null;
        job.CompletedUtc = null;
        await db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>Puts a Failed job back in the queue keeping its checkpoints — the caller enqueues it.</summary>
    public async Task<AutoFetchJob?> RequeueAsync(long requestId, CancellationToken ct)
    {
        var job = await db.AutoFetchJobs.FirstOrDefaultAsync(j => j.RequestId == requestId, ct);
        if (job is null || !job.IsTerminal) return job;
        job.Status = AutoFetchJobStatus.Queued;
        job.ProgressPercent = 0;
        job.StatusMessage = "Queued for retry.";
        job.FailureReason = null;
        job.CompletedUtc = null;
        await db.SaveChangesAsync(ct);
        return job;
    }

    // ── Processing ─────────────────────────────────────────────────────────────────────────────────

    public async Task ProcessAsync(long jobId, CancellationToken ct)
    {
        // Atomic claim — only one worker may move a job out of Queued.
        var claimed = await db.AutoFetchJobs
            .Where(j => j.AutoFetchJobId == jobId && j.Status == AutoFetchJobStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, AutoFetchJobStatus.CheckingSession)
                .SetProperty(j => j.StartedUtc, DateTime.UtcNow)
                .SetProperty(j => j.HeartbeatUtc, DateTime.UtcNow)
                .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1), ct);
        if (claimed == 0) return;

        var job = await db.AutoFetchJobs.FirstAsync(j => j.AutoFetchJobId == jobId, ct);
        var request = await db.Requests.FirstAsync(r => r.RequestId == job.RequestId, ct);
        var warnings = ReadWarnings(job);

        try
        {
            if (!_opts.IsConfigured)
                throw new ReferenceToolException("Auto-fetch is not configured: set ReferenceTool:BaseUrl and ReferenceTool:SessionCookie.");

            // 1. Session
            await SetStageAsync(job, AutoFetchJobStatus.CheckingSession, 2, "Checking the reference-tool session…", ct);
            var session = await client.CheckSessionAsync(ct);
            if (!session.IsValid)
                throw new ReferenceToolException($"The reference-tool session is not valid ({session.Detail}). Sign in to the tool in a browser, copy the fresh Cookie header into ReferenceTool:SessionCookie and retry.");
            var userId = !string.IsNullOrWhiteSpace(_opts.UserId) ? _opts.UserId.Trim() : session.UserId;

            // 2. Company name (best effort — the workbook's own "About the Company" sheet fills it in later anyway)
            if (NeedsCompanyName(request))
                await TryResolveCompanyNameAsync(request, job, ct);

            // 3. Workbooks → documents
            if (job.RocDocumentId is null)
            {
                await SetStageAsync(job, AutoFetchJobStatus.FetchingWorkbooks, 8, "Exporting the MCA / ROC workbook…", ct);
                job.RocDocumentId = (await FetchWorkbookAsync(request, job, ReferenceWorkbookKind.Corporate, ct)).DocumentId;
                await db.SaveChangesAsync(ct);
            }
            if (job.ChargeDocumentId is null && job.IngestionRunId is null)
            {
                await SetStageAsync(job, AutoFetchJobStatus.FetchingWorkbooks, 18, "Exporting the detailed charge workbook…", ct);
                try
                {
                    job.ChargeDocumentId = (await FetchWorkbookAsync(request, job, ReferenceWorkbookKind.Charge, ct)).DocumentId;
                    await db.SaveChangesAsync(ct);
                }
                catch (ReferenceToolException ex)
                {
                    // The charge annexure is an enrichment: ingestion runs ROC-only without it and flags
                    // ChargeReportMissing itself, so this is a warning, not a failure.
                    warnings.Add($"Detailed charge workbook not exported: {ex.Message}");
                    await SaveWarningsAsync(job, warnings, ct);
                }
            }

            // 4. Ingestion (+ analysis)
            if (job.IngestionRunId is null)
            {
                await SetStageAsync(job, AutoFetchJobStatus.Ingesting, 28, "Extracting workbook data…", ct);
                request.IsManualReviewRequired = false;
                request.ManualReviewReason = null;
                request.RequestStatus = RequestStatus.DocumentsUploaded;
                request.AnalysisStartedDate = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                var run = await orchestrator.RunAsync(request.RequestId, job.RocDocumentId!.Value, job.ChargeDocumentId, ct);
                if (run.Status == IngestionRunStatus.Failed)
                    throw new ReferenceToolException($"Workbook extraction failed: {run.FailureReason}");

                job.IngestionRunId = run.IngestionRunId;
                await db.SaveChangesAsync(ct);

                await db.Entry(request).ReloadAsync(ct); // the orchestrator updated status/flags on its own tracked copy
                if (NeedsCompanyName(request))
                {
                    var profileName = await db.CompanyProfiles
                        .Where(p => p.IngestionRunId == run.IngestionRunId)
                        .Select(p => p.CompanyName)
                        .FirstOrDefaultAsync(ct);
                    if (!string.IsNullOrWhiteSpace(profileName))
                    {
                        request.CompanyName = profileName;
                        await db.SaveChangesAsync(ct);
                    }
                }

                // Same block/don't-block line as the New-request flow: only an identity mismatch holds
                // analysis back, never a missing optional sheet.
                if (request.RequestStatus == RequestStatus.DataExtracted && !request.IsManualReviewRequired)
                    analysisQueue.Enqueue(request.RequestId);
                else if (request.IsManualReviewRequired)
                    warnings.Add($"Manual review required after extraction: {request.ManualReviewReason}");
                if (run.Status == IngestionRunStatus.CompletedWithWarnings)
                    warnings.Add($"Workbook extraction completed with {run.WarningsCount} warning(s) — see the Documents tab.");
                await SaveWarningsAsync(job, warnings, ct);
            }

            // 5. Filings
            if (job.IncludeFilings && job.FilingsDocumentId is null)
            {
                if (string.IsNullOrWhiteSpace(userId))
                    throw new ReferenceToolException("The reference tool's user id could not be determined from the session — set ReferenceTool:UserId.");
                await FetchFilingsAsync(request, job, userId, warnings, ct);
            }

            job.Status = warnings.Count > 0 ? AutoFetchJobStatus.CompletedWithWarnings : AutoFetchJobStatus.Completed;
            job.ProgressPercent = 100;
            job.StatusMessage = job.IncludeFilings
                ? "Done — workbook data extracted, analysis queued, filings handed to the document pipeline."
                : "Done — workbook data extracted and analysis queued.";
            job.CompletedUtc = DateTime.UtcNow;
            job.HeartbeatUtc = DateTime.UtcNow;
            job.WarningsJson = JsonSerializer.Serialize(warnings);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown — the worker's recovery sweep re-queues it
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-fetch job {JobId} (request {RequestId}) failed", job.AutoFetchJobId, job.RequestId);
            db.ChangeTracker.Clear();
            var failed = await db.AutoFetchJobs.FirstAsync(j => j.AutoFetchJobId == jobId, CancellationToken.None);
            failed.Status = AutoFetchJobStatus.Failed;
            failed.FailureReason = ex is ReferenceToolException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            failed.StatusMessage = "Failed.";
            failed.CompletedUtc = DateTime.UtcNow;
            failed.HeartbeatUtc = DateTime.UtcNow;
            failed.WarningsJson = JsonSerializer.Serialize(warnings);
            await db.SaveChangesAsync(CancellationToken.None);

            // Before ingestion ever ran there is nothing on the request to look at; say why on the request too.
            if (failed.IngestionRunId is null)
            {
                await db.Requests.Where(r => r.RequestId == failed.RequestId && r.LatestCompletedIngestionRunId == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.RequestStatus, RequestStatus.ExtractionFailed)
                        .SetProperty(r => r.FailureReason, failed.FailureReason), CancellationToken.None);
            }
        }
    }

    // ── Stages ─────────────────────────────────────────────────────────────────────────────────────

    private static bool NeedsCompanyName(McaRequest request) =>
        string.IsNullOrWhiteSpace(request.CompanyName)
        || string.Equals(request.CompanyName.Trim(), request.Cin, StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.CompanyName.Trim(), request.Llpin, StringComparison.OrdinalIgnoreCase);

    private async Task TryResolveCompanyNameAsync(McaRequest request, AutoFetchJob job, CancellationToken ct)
    {
        try
        {
            var hits = await client.SearchCompaniesAsync(job.Cin, 5, ct);
            var match = hits.FirstOrDefault(h => string.Equals(h.Cin, job.Cin, StringComparison.OrdinalIgnoreCase));
            if (match is null) return;
            request.CompanyName = match.LegalName;
            if (!string.IsNullOrWhiteSpace(match.Bid) && !string.Equals(match.Bid, job.Bid, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Reference tool bid {SearchBid} for {Cin} differs from sha256(CIN) {ComputedBid}; using the tool's", match.Bid, job.Cin, job.Bid);
                job.Bid = match.Bid;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Company-name lookup for {Cin} failed; continuing", job.Cin);
        }
    }

    private async Task<RequestDocument> FetchWorkbookAsync(McaRequest request, AutoFetchJob job, ReferenceWorkbookKind kind, CancellationToken ct)
    {
        var stagingDir = StagingDir(job);
        Directory.CreateDirectory(stagingDir);
        var stem = kind == ReferenceWorkbookKind.Charge ? $"{job.Cin}-charge" : job.Cin;
        var downloaded = await client.DownloadWorkbookAsync(job.Cin, job.Bid, kind, Path.Combine(stagingDir, stem), ct);

        await using (var stream = File.OpenRead(downloaded))
        {
            var check = fileValidation.ValidateUpload(Path.GetFileName(downloaded), new FileInfo(downloaded).Length, stream);
            if (!check.IsValid)
                throw new ReferenceToolException($"The exported {(kind == ReferenceWorkbookKind.Charge ? "charge" : "MCA / ROC")} workbook is not usable: {check.Error}");
        }

        var documentType = kind == ReferenceWorkbookKind.Charge ? DocumentType.ChargeReport : DocumentType.McaRocReport;
        var document = await StoreDocumentAsync(request.RequestId, downloaded, Path.GetFileName(downloaded), documentType, validateAsExcel: true, ct);
        if (document.UploadStatus == DocumentUploadStatus.ValidationFailed)
            throw new ReferenceToolException($"The exported workbook could not be opened: {document.QuarantineReason}");
        return document;
    }

    private async Task FetchFilingsAsync(McaRequest request, AutoFetchJob job, string userId, List<string> warnings, CancellationToken ct)
    {
        // Registry — cached in the staging folder so a resumed job packages exactly the same set.
        var stagingDir = StagingDir(job);
        Directory.CreateDirectory(stagingDir);
        var registryPath = Path.Combine(stagingDir, "registry.json");

        await SetStageAsync(job, AutoFetchJobStatus.FetchingRegistry, 36, "Listing the filing documents…", ct);
        ReferenceDocumentRegistry registry;
        if (File.Exists(registryPath))
        {
            registry = JsonSerializer.Deserialize<ReferenceDocumentRegistry>(await File.ReadAllTextAsync(registryPath, ct))
                ?? await client.GetReferenceDocumentsAsync(job.Bid, ct);
        }
        else
        {
            registry = await client.GetReferenceDocumentsAsync(job.Bid, ct);
            await File.WriteAllTextAsync(registryPath, JsonSerializer.Serialize(registry), ct);
        }

        job.RegistryTotalCount = registry.TotalCount;
        job.RegistryListedCount = registry.ListedCount;
        if (registry.ListedCount < registry.TotalCount)
            warnings.Add($"The reference tool reports {registry.TotalCount:N0} filing documents but listed {registry.ListedCount:N0}; only the listed ones were fetched.");

        var aggregateCap = _opts.MaxAggregateDownloadBytes;
        var (plan, estimatedBytes) = BuildDownloadPlan(registry, job.MaxDocumentsPerSection, aggregateCap, stagingDir, warnings);
        job.FilesTotal = plan.Sum(f => f.Files.Count);
        job.FilesDownloaded = plan.Sum(f => f.Files.Count(x => IsStaged(x.LocalPath)));
        job.FilesFailed = 0;
        await SaveWarningsAsync(job, warnings, ct);

        if (plan.Count == 0)
        {
            warnings.Add("The reference tool listed no filing documents for this company; no filings archive was created.");
            await SaveWarningsAsync(job, warnings, ct);
            return;
        }

        // Reserve disk headroom before writing a single byte: staged originals plus the packaged copy
        // that briefly coexists with them (see the cleanup at the end of this method) — both draw from the
        // same volume-wide ledger the large-archive-upload feature uses, so the two features never
        // independently believe the same free space is available to both. Sized from the plan's own
        // estimate (capped at the configured ceiling) plus one MaxResponseBytes margin — the most the
        // aggregate download budget below can ever overshoot by, now that admission against it is atomic
        // (see AggregateDownloadBudget) — so a handful of PDFs for a small company doesn't have to fail
        // because a machine lacks 40 GB free for nothing it will actually use, while still covering the
        // real worst case rather than only the happy-path estimate.
        var reserveBytes = 2 * Math.Max(Math.Min(estimatedBytes, aggregateCap) + _opts.MaxResponseBytes, 50 * 1024 * 1024L);
        var reservation = await reservations.TryReserveAsync(
            "AutoFetchJob", job.AutoFetchJobId.ToString(), stagingDir, reserveBytes, _opts.StorageReservationLifetime, ct);
        if (!reservation.Success)
            throw new ReferenceToolException($"Not enough disk space to fetch this company's filings: {reservation.Error}");

        try
        {
            // Parallel download with a bounded degree of concurrency; progress flushed on a timer from this
            // (single) DbContext-owning flow — the download tasks never touch the DbContext.
            await SetStageAsync(job, AutoFetchJobStatus.DownloadingFilings, 40, $"Downloading filings 0 / {job.FilesTotal:N0}…", ct);
            var allFiles = plan.SelectMany(f => f.Files.Select(x => (Filing: f, File: x))).ToList();
            // A resumed job's already-staged bytes must count toward the aggregate cap from the start —
            // otherwise a job retried enough times could accumulate past the cap one resume at a time.
            var initialBytes = allFiles.Where(t => IsStaged(t.File.LocalPath)).Sum(t => new FileInfo(t.File.LocalPath).Length);
            var pending = allFiles.Where(t => !IsStaged(t.File.LocalPath)).ToList();
            var downloadedCount = job.FilesDownloaded;
            var failedCount = 0;
            var cappedCount = 0;
            long bytes = initialBytes; // for progress display only — real bytes actually on disk, never a reservation
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

            // Enforces the aggregate cap atomically: a worker claims MaxResponseBytes of budget with a
            // single compare-and-swap BEFORE it starts downloading, not a separate check-then-add after —
            // the earlier version of this method let several concurrent workers all observe "under cap"
            // in the same window before any of them had added anything, so the job could overshoot by up
            // to DownloadConcurrency × MaxResponseBytes instead of one file's worth. TryReserve makes that
            // race structurally impossible: only one caller can ever be the reservation that crosses the
            // threshold, so real disk usage is now bounded by aggregateCap + MaxResponseBytes regardless
            // of how many workers race the check (see AggregateDownloadBudget's own remarks).
            var budget = new AggregateDownloadBudget(aggregateCap);
            budget.SeedKnownUsage(initialBytes);

            var downloadTask = Parallel.ForEachAsync(pending,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _opts.DownloadConcurrency), CancellationToken = ct },
                async (item, token) =>
                {
                    var (filing, (entryName, localPath, awsPath, did)) = item;
                    if (!budget.TryReserve(_opts.MaxResponseBytes))
                    {
                        Interlocked.Increment(ref cappedCount);
                        return;
                    }

                    var ok = await DownloadWithRetryAsync(job.Bid, userId, awsPath, did, localPath, token);
                    if (ok)
                    {
                        var actualBytes = new FileInfo(localPath).Length;
                        budget.Commit(_opts.MaxResponseBytes, actualBytes);
                        Interlocked.Increment(ref downloadedCount);
                        Interlocked.Add(ref bytes, actualBytes);
                    }
                    else
                    {
                        budget.Release(_opts.MaxResponseBytes);
                        Interlocked.Increment(ref failedCount);
                        failures.Add($"{filing.SectionFolder} / {filing.DocId} / {entryName}");
                    }
                });

            while (!downloadTask.IsCompleted)
            {
                await Task.WhenAny(downloadTask, Task.Delay(ProgressFlushInterval, ct));
                await FlushDownloadProgressAsync(job, Volatile.Read(ref downloadedCount), Volatile.Read(ref failedCount), Interlocked.Read(ref bytes), ct);
            }
            await downloadTask; // surfaces cancellation / unexpected exceptions
            await FlushDownloadProgressAsync(job, downloadedCount, failedCount, Interlocked.Read(ref bytes), ct);

            if (failedCount > 0)
            {
                var sample = failures.Take(10).ToList();
                warnings.Add($"{failedCount:N0} of {job.FilesTotal:N0} filing PDFs could not be downloaded after {_opts.DownloadAttempts} attempts each " +
                             $"(e.g. {string.Join("; ", sample)}{(failures.Count > sample.Count ? "; …" : "")}).");
            }
            if (cappedCount > 0)
                warnings.Add($"Stopped after reaching the {aggregateCap / (1024 * 1024 * 1024.0):N1} GB aggregate download limit; {cappedCount:N0} planned file(s) were not attempted.");
            if (downloadedCount == 0)
                throw new ReferenceToolException("None of the filing PDFs could be downloaded — the session cookie has probably expired, or the reference documents are not unlocked for this company.");

            // Package → RequestDocument → McaFilingBatch → unpack queue
            await SetStageAsync(job, AutoFetchJobStatus.Packaging, 88, "Packaging the filings archive…", ct);
            var outerZipPath = Path.Combine(stagingDir, $"{job.Cin}_filings.zip");
            AutoFetchArchiveBuilder.Build(outerZipPath, request.CompanyName, job.Cin, plan.Select(p => p.ToArchiveFiling()), ct);

            var safety = ArchiveSafetyValidator.ValidateOuterArchive(outerZipPath, ArchiveSafetyLimits.Default);
            if (!safety.IsValid)
                throw new ReferenceToolException($"The packaged filings archive failed the safety check: {safety.Error}");

            var filingsDocument = await StoreDocumentAsync(request.RequestId, outerZipPath, $"{job.Cin}_MCA_Filings.zip", DocumentType.McaFilingsArchive, validateAsExcel: false, ct);
            var batch = new McaFilingBatch
            {
                RequestId = request.RequestId,
                SourceDocumentId = filingsDocument.DocumentId,
                Status = FilingBatchStatus.Uploaded,
                StartedDate = DateTime.UtcNow
            };
            db.McaFilingBatches.Add(batch);
            job.FilingsDocumentId = filingsDocument.DocumentId;
            await db.SaveChangesAsync(ct);
            job.FilingBatchId = batch.BatchId;
            job.ProgressPercent = 96;
            job.StatusMessage = "Filings archive queued for OCR, extraction and indexing.";
            await SaveWarningsAsync(job, warnings, ct);
            filingQueue.Enqueue(new UnpackBatchWorkItem(batch.BatchId));

            // The archive is stored under App_Data/Uploads now; the staged PDFs are no longer needed.
            try { Directory.Delete(stagingDir, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not delete auto-fetch staging folder {Dir}", stagingDir); }
        }
        finally
        {
            await reservations.ReleaseReservationsAsync("AutoFetchJob", job.AutoFetchJobId.ToString(), CancellationToken.None);
        }
    }

    /// <summary>Turns the registry into the list of nested zips to build, with the local staging path
    /// of every file. Documents that appear in more than one section are packaged once (first section
    /// wins). The plan is truncated at whichever of two caps is hit first: the filings pipeline's
    /// batch-wide PDF count (going past it would only make the unpack step reject the whole archive), or
    /// <paramref name="aggregateByteCap"/> (estimated from the registry's own declared sizes, falling back
    /// to <see cref="FallbackPerFileEstimateBytes"/> per file when a size wasn't declared) — this estimate
    /// only decides how much is planned and how large a storage reservation to ask for; the live download
    /// loop in <see cref="FetchFilingsAsync"/> enforces the same cap against real downloaded bytes.</summary>
    private static (List<PlannedFiling> Plan, long EstimatedBytes) BuildDownloadPlan(
        ReferenceDocumentRegistry registry, int maxDocumentsPerSection, long aggregateByteCap, string stagingDir, List<string> warnings)
    {
        var plan = new List<PlannedFiling>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        long estimatedBytes = 0;
        var pdfCap = ArchiveSafetyLimits.Default.MaxPdfCount;
        var truncatedByCount = false;
        var truncatedByBytes = false;

        foreach (var section in registry.Sections)
        {
            var docs = maxDocumentsPerSection > 0 ? section.Documents.Take(maxDocumentsPerSection) : section.Documents;
            foreach (var doc in docs)
            {
                if (!seen.Add(doc.DocId)) continue;
                var files = new List<PlannedFile>(1 + doc.Attachments.Count);
                var docDir = Path.Combine(stagingDir, AutoFetchArchiveBuilder.SafeDocFolder(doc.DocId));
                files.Add(new PlannedFile(AutoFetchArchiveBuilder.SanitizeEntryName(doc.Name), Path.Combine(docDir, "main.pdf"), doc.AwsPath, doc.DocId));
                var attIndex = 0;
                foreach (var att in doc.Attachments)
                {
                    attIndex++;
                    files.Add(new PlannedFile(AutoFetchArchiveBuilder.SanitizeEntryName(att.Name), Path.Combine(docDir, $"att{attIndex}.pdf"), att.AwsPath,
                        Path.GetFileNameWithoutExtension(att.Name)));
                }

                if (fileCount + files.Count > pdfCap) { truncatedByCount = true; break; }

                // The registry's declared size (when present) covers the whole filing entry, not each
                // attachment individually — split it evenly rather than attribute it all to the main PDF.
                var docEstimate = doc.SizeKb.HasValue && doc.SizeKb.Value > 0
                    ? (long)(doc.SizeKb.Value * 1024)
                    : FallbackPerFileEstimateBytes * files.Count;
                if (estimatedBytes + docEstimate > aggregateByteCap) { truncatedByBytes = true; break; }

                fileCount += files.Count;
                estimatedBytes += docEstimate;
                plan.Add(new PlannedFiling(section.FolderName, doc.DocId, files));
            }
            if (truncatedByCount || truncatedByBytes) break;
        }

        if (truncatedByCount)
            warnings.Add($"The filing list was cut at the pipeline's {pdfCap:N0}-PDF batch limit; later sections/documents were not fetched. Use a per-section cap to choose what to include.");
        if (truncatedByBytes)
            warnings.Add($"The filing list was cut at the {aggregateByteCap / (1024 * 1024 * 1024.0):N1} GB aggregate download limit (estimated from the registry's declared sizes); later sections/documents were not fetched.");
        return (plan, estimatedBytes);
    }

    private async Task<bool> DownloadWithRetryAsync(string bid, string userId, string awsPath, string did, string localPath, CancellationToken ct)
    {
        var attempts = Math.Max(1, _opts.DownloadAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await client.DownloadPdfAsync(bid, userId, awsPath, did, localPath, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or IOException or TaskCanceledException)
            {
                if (attempt == attempts)
                {
                    logger.LogWarning(ex, "Giving up on {Key} after {Attempts} attempts", awsPath, attempts);
                    return false;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
        return false;
    }

    private static bool IsStaged(string localPath)
    {
        try { return File.Exists(localPath) && new FileInfo(localPath).Length > 100; }
        catch { return false; }
    }

    // ── Persistence helpers ────────────────────────────────────────────────────────────────────────

    private string StagingDir(AutoFetchJob job) =>
        Path.Combine(env.ContentRootPath, "App_Data", "Requests", job.RequestId.ToString(), "auto-fetch", job.AutoFetchJobId.ToString());

    /// <summary>Same on-disk layout and RequestDocument shape as RequestsController.SaveDocumentAsync,
    /// but from a file already on disk (moved, not copied — the staging copy is not needed afterwards).</summary>
    private async Task<RequestDocument> StoreDocumentAsync(long requestId, string sourcePath, string originalFileName, DocumentType documentType, bool validateAsExcel, CancellationToken ct)
    {
        var document = new RequestDocument
        {
            RequestId = requestId,
            DocumentType = documentType,
            OriginalFileName = originalFileName,
            FileSize = new FileInfo(sourcePath).Length,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow,
            UploadedBy = "auto-fetch"
        };
        db.RequestDocuments.Add(document);
        await db.SaveChangesAsync(ct); // need DocumentId for the internal filename

        var ext = Path.GetExtension(originalFileName);
        var storedFileName = $"{document.DocumentId}{ext}";
        var uploadsDir = Path.Combine(env.ContentRootPath, "App_Data", "Uploads", requestId.ToString(), "original");
        Directory.CreateDirectory(uploadsDir);
        var fullPath = Path.Combine(uploadsDir, storedFileName);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        File.Move(sourcePath, fullPath);

        await using (var hashStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            document.FileHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ct));

        document.StoredFileName = storedFileName;
        document.StoragePath = fullPath;
        if (validateAsExcel)
        {
            var openCheck = fileValidation.ValidateOpens(fullPath);
            if (!openCheck.IsValid)
            {
                document.UploadStatus = DocumentUploadStatus.ValidationFailed;
                document.QuarantineReason = openCheck.Error;
            }
        }
        await db.SaveChangesAsync(ct);
        return document;
    }

    private async Task SetStageAsync(AutoFetchJob job, AutoFetchJobStatus status, int percent, string message, CancellationToken ct)
    {
        job.Status = status;
        job.ProgressPercent = percent;
        job.StatusMessage = message;
        job.HeartbeatUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task FlushDownloadProgressAsync(AutoFetchJob job, int downloaded, int failed, long bytes, CancellationToken ct)
    {
        job.FilesDownloaded = downloaded;
        job.FilesFailed = failed;
        job.BytesDownloaded = bytes;
        var done = downloaded + failed;
        var fraction = job.FilesTotal == 0 ? 1 : Math.Min(1.0, (double)done / job.FilesTotal);
        job.ProgressPercent = 40 + (int)Math.Round(fraction * 46);
        job.StatusMessage = $"Downloading filings {done:N0} / {job.FilesTotal:N0} ({bytes / (1024.0 * 1024.0):N0} MB)…";
        job.HeartbeatUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static List<string> ReadWarnings(AutoFetchJob job)
    {
        try { return JsonSerializer.Deserialize<List<string>>(job.WarningsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task SaveWarningsAsync(AutoFetchJob job, List<string> warnings, CancellationToken ct)
    {
        job.WarningsJson = JsonSerializer.Serialize(warnings.Distinct().ToList());
        job.HeartbeatUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private sealed record PlannedFile(string EntryName, string LocalPath, string AwsPath, string Did);

    private sealed record PlannedFiling(string SectionFolder, string DocId, List<PlannedFile> Files)
    {
        public ArchiveFiling ToArchiveFiling() =>
            new(SectionFolder, DocId, Files.Select(f => (f.EntryName, f.LocalPath)).ToList());
    }
}
