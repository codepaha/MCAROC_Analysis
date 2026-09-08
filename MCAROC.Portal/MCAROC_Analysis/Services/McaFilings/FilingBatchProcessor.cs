using System.IO.Compression;
using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>Drives the MCA Filings pipeline: archive-safe unpacking (outer zip -> per-filing nested zips
/// -> PDFs), per-document classification + text extraction, and per-filing Gemini extraction. Each method
/// is one unit of background work, dispatched by FilingProcessingWorker via FilingProcessingQueue.</summary>
public class FilingBatchProcessor(
    AppDbContext db,
    string contentRootPath,
    PdfTextExtractor pdfTextExtractor,
    VertexAiExtractionService vertexAiService,
    FilingProcessingQueue queue,
    ILogger<FilingBatchProcessor> logger)
{
    private static readonly ArchiveSafetyLimits Limits = ArchiveSafetyLimits.Default;
    private const int MaxRetryCount = 3;
    private static readonly FilingCategory[] AiEligibleCategories =
        [FilingCategory.Charge, FilingCategory.Compliance, FilingCategory.Constitutional];

    public async Task UnpackBatchAsync(long batchId, CancellationToken ct)
    {
        var batch = await db.McaFilingBatches.Include(b => b.Request).FirstAsync(b => b.BatchId == batchId, ct);
        var outerZipDoc = await db.RequestDocuments.FirstAsync(d => d.DocumentId == batch.SourceDocumentId, ct);

        try
        {
            batch.Status = FilingBatchStatus.Unpacking;
            await db.SaveChangesAsync(ct);

            var outerSafety = ArchiveSafetyValidator.ValidateOuterArchive(outerZipDoc.StoragePath, Limits);
            if (!outerSafety.IsValid)
                throw new InvalidOperationException($"Archive safety check failed: {outerSafety.Error}");

            var tempDir = FilingStoragePaths.BatchTempDir(contentRootPath, batch.RequestId, batchId);
            Directory.CreateDirectory(tempDir);

            // One tracker shared across every nested zip in this batch, so the uncompressed-size and
            // PDF-count limits are enforced cumulatively — many individually-small nested zips could
            // otherwise combine to exhaust disk without any single one tripping a per-archive check.
            var cumulativeStats = new CumulativeArchiveStats();

            using (var outerArchive = ZipFile.OpenRead(outerZipDoc.StoragePath))
            {
                var nestedZipEntries = outerArchive.Entries.Where(e => e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var nestedEntry in nestedZipEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    await IndexNestedZipAsync(batch, nestedEntry, tempDir, cumulativeStats, ct);
                }
            }

            batch.Status = FilingBatchStatus.Processing;
            await db.SaveChangesAsync(ct);

            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }

            // Only Discovered (not yet started) — on a retry after a crash mid-unpack, documents from
            // nested zips indexed before the crash may already be past this stage; re-enqueueing those
            // too would just waste OCR/CPU time re-doing finished work, not cause incorrect data.
            var documentIds = await db.McaFilingDocuments
                .Where(d => d.BatchId == batchId && d.DuplicateOfDocumentId == null
                    && d.ProcessingStatus == FilingDocumentProcessingStatus.Discovered)
                .Select(d => d.FilingDocumentId)
                .ToListAsync(ct);
            foreach (var id in documentIds)
                queue.Enqueue(new ProcessDocumentWorkItem(id));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to unpack MCA filings batch {BatchId}", batchId);
            batch.Status = FilingBatchStatus.Failed;
            batch.FailureReason = ex.Message;
            batch.CompletedDate = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task IndexNestedZipAsync(McaFilingBatch batch, ZipArchiveEntry nestedEntry, string batchTempDir, CumulativeArchiveStats cumulativeStats, CancellationToken ct)
    {
        var nestedZipFileName = Path.GetFileName(nestedEntry.FullName);
        var outerCategoryFolder = Path.GetFileName(Path.GetDirectoryName(nestedEntry.FullName.Replace('\\', '/'))) ?? "";

        // Idempotency: a re-run of UnpackBatchAsync (after a crash mid-unpack, via RecoverStaleWorkAsync)
        // must not re-index a nested zip it already processed, or every retry would duplicate that filing.
        var alreadyIndexed = await db.McaFilings.AnyAsync(
            f => f.BatchId == batch.BatchId && f.NestedZipName == nestedZipFileName, ct);
        if (alreadyIndexed)
            return;

        var tempNestedZipPath = Path.Combine(batchTempDir, $"nested-{Guid.NewGuid():N}.zip");
        await using (var entryStream = nestedEntry.Open())
        await using (var fileStream = File.Create(tempNestedZipPath))
        {
            await entryStream.CopyToAsync(fileStream, ct);
        }

        try
        {
            var nestedSafety = ArchiveSafetyValidator.ValidateNestedArchive(tempNestedZipPath, Limits, currentDepth: 2, cumulativeStats);

            var identity = FilingIdentityParser.Parse(Path.GetFileNameWithoutExtension(nestedZipFileName));
            var identityMatchesRequest = identity.Cin is null || batch.Request!.Cin is null
                || string.Equals(identity.Cin, batch.Request.Cin, StringComparison.OrdinalIgnoreCase);

            var filing = new McaFiling
            {
                BatchId = batch.BatchId,
                RequestId = batch.RequestId,
                Srn = identity.Srn ?? nestedZipFileName,
                ParsedCompanyName = identity.CompanyName,
                ParsedCin = identity.Cin,
                IdentityMatchesRequest = identityMatchesRequest,
                OuterCategoryFolder = outerCategoryFolder,
                NestedZipName = nestedZipFileName,
                ManualReviewRequired = !nestedSafety.IsValid || !identityMatchesRequest,
                ManualReviewReason = !nestedSafety.IsValid
                    ? $"Archive safety check failed: {nestedSafety.Error}"
                    : !identityMatchesRequest
                        ? $"Nested archive CIN '{identity.Cin}' does not match the request's CIN '{batch.Request!.Cin}'."
                        : null
            };
            db.McaFilings.Add(filing);
            await db.SaveChangesAsync(ct); // need FilingId

            if (nestedSafety.IsValid)
                await ExtractPdfsFromNestedZipAsync(batch, filing, tempNestedZipPath, ct);
        }
        finally
        {
            try { File.Delete(tempNestedZipPath); } catch { /* best effort */ }
        }
    }

    private async Task ExtractPdfsFromNestedZipAsync(McaFilingBatch batch, McaFiling filing, string nestedZipPath, CancellationToken ct)
    {
        var documentsDir = FilingStoragePaths.DocumentsDir(contentRootPath, batch.RequestId, batch.BatchId, filing.FilingId);
        Directory.CreateDirectory(documentsDir);

        // Only non-duplicate rows: a duplicate row keeps the same FileHash as its canonical, so including
        // them here would mean building a Hash -> Id dictionary from multiple rows sharing one key.
        var hashesSeenInBatch = await db.McaFilingDocuments
            .Where(d => d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null)
            .Select(d => new { d.FileHash, d.FilingDocumentId })
            .ToDictionaryAsync(x => x.FileHash, x => x.FilingDocumentId, ct);

        using var nestedArchive = ZipFile.OpenRead(nestedZipPath);
        foreach (var pdfEntry in nestedArchive.Entries.Where(e => e.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (!ArchiveSafetyValidator.IsPathSafe(pdfEntry.FullName))
            {
                logger.LogWarning("Skipped unsafe entry path '{Path}' in {Zip}", pdfEntry.FullName, nestedZipPath);
                continue;
            }

            var sourceFolder = Path.GetFileName(Path.GetDirectoryName(pdfEntry.FullName.Replace('\\', '/'))) ?? "";
            var docId = Guid.NewGuid().ToString("N");
            var storagePath = ArchiveSafetyValidator.ResolveSafeExtractionPath(documentsDir, $"{docId}.pdf");

            string hash;
            await using (var entryStream = pdfEntry.Open())
            await using (var fileStream = File.Create(storagePath))
            {
                using var sha256 = SHA256.Create();
                await using var hashingStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write);
                await entryStream.CopyToAsync(hashingStream, ct);
                await hashingStream.FlushFinalBlockAsync(ct);
                hash = Convert.ToHexString(sha256.Hash!);
            }

            var document = new McaFilingDocument
            {
                FilingId = filing.FilingId,
                BatchId = batch.BatchId,
                RequestId = batch.RequestId,
                OriginalFileName = Path.GetFileName(pdfEntry.FullName),
                SourceFolder = sourceFolder,
                StoragePath = storagePath,
                FileHash = hash,
                ProcessingStatus = FilingDocumentProcessingStatus.Discovered,
                UpdatedAt = DateTime.UtcNow
            };

            if (hashesSeenInBatch.TryGetValue(hash, out var canonicalId))
            {
                document.DuplicateOfDocumentId = canonicalId;
                document.ProcessingStatus = FilingDocumentProcessingStatus.Skipped;
                try { File.Delete(storagePath); } catch { /* keep only the canonical copy on disk */ }
                document.StoragePath = string.Empty;
            }
            else
            {
                hashesSeenInBatch[hash] = 0; // placeholder until saved; real id set right after
            }

            db.McaFilingDocuments.Add(document);
            await db.SaveChangesAsync(ct);
            if (document.DuplicateOfDocumentId is null)
                hashesSeenInBatch[hash] = document.FilingDocumentId;
        }
    }

    public async Task ProcessDocumentAsync(long filingDocumentId, CancellationToken ct)
    {
        // Atomic claim: a single UPDATE ... WHERE ProcessingStatus = 'Discovered' is what actually
        // prevents two overlapping enqueues of the same document (recovery + a retry, or two near-
        // simultaneous triggers) from both doing the work. SQL Server serializes concurrent UPDATEs
        // against the same row via row locking and re-evaluates the WHERE predicate on the blocked
        // transaction once the first commits, so only one caller ever sees rowsClaimed > 0.
        var rowsClaimed = await db.McaFilingDocuments
            .Where(d => d.FilingDocumentId == filingDocumentId && d.ProcessingStatus == FilingDocumentProcessingStatus.Discovered)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ProcessingStatus, FilingDocumentProcessingStatus.TextExtracting)
                .SetProperty(d => d.ProcessingStartedAt, DateTime.UtcNow)
                .SetProperty(d => d.UpdatedAt, DateTime.UtcNow), ct);
        if (rowsClaimed == 0)
            return; // another worker already claimed (or already finished) this document

        var document = await db.McaFilingDocuments.Include(d => d.Filing).FirstAsync(d => d.FilingDocumentId == filingDocumentId, ct);

        try
        {
            var filing = document.Filing!;
            var tempDir = FilingStoragePaths.TempDir(contentRootPath, document.RequestId, document.BatchId, document.FilingId);

            var extraction = await pdfTextExtractor.ExtractAsync(document.StoragePath, tempDir, ct);

            if (extraction.Status != FilingDocumentProcessingStatus.TextExtracted)
            {
                document.ProcessingStatus = extraction.Status;
                document.LastError = extraction.Error;
            }
            else
            {
                // Classified using the full extracted text rather than only page 1 — strictly more
                // informative for keyword matching and simpler than a separate page-1-only extraction pass.
                var classification = FilingClassifier.Classify(filing.OuterCategoryFolder, document.SourceFolder, document.OriginalFileName, extraction.FullText);
                document.Category = classification.Category;
                document.FormType = classification.FormType;
                document.ClassificationConfidence = classification.Confidence;
                document.ClassificationMethod = classification.Method;
                document.MatchedRule = classification.MatchedRule;
                if (classification.Category == FilingCategory.Unclassified)
                {
                    document.ManualReviewRequired = true;
                    document.ManualReviewReason = "Could not classify this document.";
                }

                var textDir = FilingStoragePaths.TextDir(contentRootPath, document.RequestId, document.BatchId, document.FilingId);
                Directory.CreateDirectory(textDir);
                var textPath = Path.Combine(textDir, $"{document.FilingDocumentId}.txt");
                await File.WriteAllTextAsync(textPath, extraction.FullText, ct);

                document.ExtractedTextPath = textPath;
                document.ExtractedCharCount = extraction.FullText.Length;
                document.NativePageCount = extraction.NativePageCount;
                document.OcrPageCount = extraction.OcrPageCount;
                document.PageCount = extraction.PageCount;
                document.TextExtractionMethod = extraction.Method;
                document.AiExtractionStatus = AiEligibleCategories.Contains(classification.Category)
                    ? AiExtractionStatus.Pending
                    : AiExtractionStatus.NotApplicable;
                document.ProcessingStatus = FilingDocumentProcessingStatus.Completed;
            }

            document.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await MaybeEnqueueFilingExtractionAsync(document.FilingId, ct);
            await MaybeCompleteBatchAsync(document.BatchId, ct);
        }
        catch (Exception ex)
        {
            document.RetryCount++;
            document.LastError = ex.Message;
            document.UpdatedAt = DateTime.UtcNow;

            if (document.RetryCount < MaxRetryCount)
            {
                logger.LogWarning(ex, "Retrying document {DocumentId} (attempt {Attempt})", filingDocumentId, document.RetryCount + 1);
                document.ProcessingStatus = FilingDocumentProcessingStatus.Discovered;
                await db.SaveChangesAsync(ct);
                queue.Enqueue(new ProcessDocumentWorkItem(filingDocumentId));
            }
            else
            {
                logger.LogError(ex, "Document {DocumentId} failed after {Retries} retries", filingDocumentId, document.RetryCount);
                document.ProcessingStatus = FilingDocumentProcessingStatus.Failed;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task MaybeEnqueueFilingExtractionAsync(long filingId, CancellationToken ct)
    {
        var pendingCount = await db.McaFilingDocuments.CountAsync(d =>
            d.FilingId == filingId &&
            d.DuplicateOfDocumentId == null &&
            (d.ProcessingStatus == FilingDocumentProcessingStatus.Discovered
                || d.ProcessingStatus == FilingDocumentProcessingStatus.TextExtracting), ct);

        if (pendingCount == 0)
            queue.Enqueue(new ExtractFilingWorkItem(filingId));
    }

    /// <summary>Resets any AiExtractionStatus=Pending document in a filing back to NotApplicable, and
    /// re-checks batch completion if that changed anything. Used wherever ExtractFilingAsync decides — for
    /// a filing-level reason, not a per-document one — that this filing will never actually get a Gemini
    /// call (quarantined identity, or a non-AI-eligible dominant category): a document's own Pending flag
    /// was set purely from its individual category by ProcessDocumentAsync, with no visibility into that
    /// filing-level decision, so without this it stays Pending forever and the batch can never complete.</summary>
    private async Task ClearStrayPendingAiStatusAsync(long filingId, CancellationToken ct)
    {
        var filing = await db.McaFilings.FirstAsync(f => f.FilingId == filingId, ct);
        var cleared = await db.McaFilingDocuments
            .Where(d => d.FilingId == filingId && d.AiExtractionStatus == AiExtractionStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, AiExtractionStatus.NotApplicable), ct);
        if (cleared > 0)
            await MaybeCompleteBatchAsync(filing.BatchId, ct);
    }

    public async Task ExtractFilingAsync(long filingId, CancellationToken ct)
    {
        var filing = await db.McaFilings.FirstAsync(f => f.FilingId == filingId, ct);
        if (!filing.IdentityMatchesRequest)
        {
            // Quarantined at indexing time — never send to Gemini. Same reset as the not-eligible branch
            // below and for the same reason: ProcessDocumentAsync marks a document AiExtractionStatus =
            // Pending purely from its own category, with no knowledge of the filing's identity-match
            // status, so a quarantined filing's individually-eligible documents need the same cleanup or
            // they're stuck Pending forever too.
            await ClearStrayPendingAiStatusAsync(filingId, ct);
            return;
        }

        var alreadyExtracted = await db.McaFilingExtractions.AnyAsync(e => e.FilingId == filingId, ct);
        if (alreadyExtracted)
            return; // cheap early-out; the real guarantee against a duplicate paid call is the claim below

        var documents = await db.McaFilingDocuments
            .Where(d => d.FilingId == filingId && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed)
            .ToListAsync(ct);
        if (documents.Count == 0)
            return;

        var dominantCategory = documents
            .Where(d => d.Category != FilingCategory.Unclassified)
            .GroupBy(d => d.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        if (dominantCategory == default && documents.All(d => d.Category == FilingCategory.Unclassified))
            dominantCategory = FilingCategory.Unclassified;

        if (!AiEligibleCategories.Contains(dominantCategory))
        {
            // Financial/Unclassified-dominant — stored and text-extracted, no Gemini extraction. But a
            // stray document that was INDIVIDUALLY classified as AI-eligible (e.g. one Charge or
            // Compliance document inside an otherwise Financial-dominant filing) was already marked
            // AiExtractionStatus=Pending by ProcessDocumentAsync. The filing-level decision overrides
            // that — it doesn't get its own separate extraction — so it must be reset here, or it stays
            // Pending forever and MaybeCompleteBatchAsync can never see the batch as done. Found live: a
            // 29-document filing (18 Financial, 10 Compliance, 1 Charge) left the whole batch stuck in
            // Processing indefinitely until this reset was added.
            await ClearStrayPendingAiStatusAsync(filingId, ct);
            return;
        }

        // Which documents this call is actually claiming — checked against the in-memory (pre-claim) state,
        // which is exactly the set the claim UPDATE below targets. Captured before the UPDATE so we can
        // apply the outcome to precisely these documents afterward without relying on EF's change tracker
        // to notice a bulk update it wasn't part of (ExecuteUpdateAsync bypasses the tracker entirely).
        var eligibleDocumentIds = documents
            .Where(d => d.AiExtractionStatus == AiExtractionStatus.Pending)
            .Select(d => d.FilingDocumentId)
            .ToHashSet();

        // Atomic claim, taken only now that we know this filing is actually going to call Gemini — flips
        // every individually-eligible document from Pending to InProgress in one UPDATE statement. Two
        // overlapping calls for the same filing (recovery + a near-simultaneous document-completion
        // trigger, say) have SQL Server serialize their UPDATEs via row locking: only the first to commit
        // sees rows affected; the second, once unblocked, re-evaluates its WHERE clause against the
        // now-InProgress rows and claims zero. That's what actually prevents a duplicate paid Gemini call,
        // not the AnyAsync check above (a plain read-then-act race on its own). Scoped to
        // AiExtractionStatus = Pending specifically (not all "Completed" documents in the filing) so a
        // stray misclassified document — e.g. one Financial-category doc mixed into an otherwise
        // Charge-dominant filing, which was individually marked NotApplicable — is never claimed or left
        // stuck in InProgress; its text still flows into the Gemini context below regardless.
        var claimed = await db.McaFilingDocuments
            .Where(d => d.FilingId == filingId && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
                && d.AiExtractionStatus == AiExtractionStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, AiExtractionStatus.InProgress), ct);
        if (claimed == 0)
            return; // already claimed by another call (or nothing individually eligible after all)

        var dominantFormType = documents
            .Where(d => d.Category == dominantCategory && d.FormType != null)
            .GroupBy(d => d.FormType)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var contexts = new List<FilingDocumentContext>();
        foreach (var d in documents)
        {
            var effectiveTextPath = d.ExtractedTextPath;
            if (effectiveTextPath is null) continue;
            var text = await File.ReadAllTextAsync(effectiveTextPath, ct);
            contexts.Add(new FilingDocumentContext(d.OriginalFileName, d.FormType, text));
        }

        var outcome = await vertexAiService.ExtractAsync(filing.Srn, dominantCategory, dominantFormType, contexts, ct);

        db.McaFilingExtractions.Add(new McaFilingExtraction
        {
            FilingId = filingId,
            Model = VertexAiExtractionService.ModelId,
            PromptVersion = VertexAiExtractionService.PromptVersion,
            SchemaName = outcome.SchemaName,
            SchemaVersion = FilingSchemaNames.SchemaVersion,
            ExtractedJson = outcome.ExtractedJson,
            RawModelResponse = outcome.RawModelResponse,
            ValidationStatus = outcome.ValidationStatus,
            ValidationErrors = outcome.ValidationErrors,
            Status = outcome.Status,
            FailureReason = outcome.FailureReason,
            ExtractedAt = DateTime.UtcNow
        });

        // Only the documents this call actually claimed — a stray NotApplicable document that happened to
        // share this filing (e.g. one Financial-category attachment in an otherwise Charge-dominant
        // filing) was never claimed and must not be overwritten with an AI outcome it was never part of.
        var finalStatus = outcome.Status == ExtractionStatus.Success ? AiExtractionStatus.Success : AiExtractionStatus.Failed;
        foreach (var d in documents.Where(d => eligibleDocumentIds.Contains(d.FilingDocumentId)))
            d.AiExtractionStatus = finalStatus;
        if (outcome.Status != ExtractionStatus.Success)
        {
            filing.ManualReviewRequired = true;
            filing.ManualReviewReason = (filing.ManualReviewReason is null ? "" : filing.ManualReviewReason + " ") + $"AI extraction failed: {outcome.FailureReason}";
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // The atomic claim above should make this unreachable in practice, but the unique index is
            // the actual guarantee, not the claim — if it ever fires, a paid Gemini call was wasted (a
            // known, accepted cost of at-least-once semantics), but no duplicate row lands in the table.
            logger.LogWarning(ex, "Duplicate McaFilingExtraction insert rejected by the unique index for filing {FilingId} — a concurrent call already recorded one.", filingId);
            return;
        }

        await MaybeCompleteBatchAsync(filing.BatchId, ct);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlEx && (sqlEx.Number is 2601 or 2627);

    /// <summary>Status is a terminal flag, not an incremented counter — safe to set redundantly if called
    /// from multiple near-simultaneous completions, unlike a shared count that would need synchronization.</summary>
    private async Task MaybeCompleteBatchAsync(long batchId, CancellationToken ct)
    {
        var nonTerminalDocs = await db.McaFilingDocuments.CountAsync(d =>
            d.BatchId == batchId && d.DuplicateOfDocumentId == null &&
            (d.ProcessingStatus == FilingDocumentProcessingStatus.Discovered
                || d.ProcessingStatus == FilingDocumentProcessingStatus.Classified
                || d.ProcessingStatus == FilingDocumentProcessingStatus.TextExtracting
                || d.ProcessingStatus == FilingDocumentProcessingStatus.AiQueued
                || d.ProcessingStatus == FilingDocumentProcessingStatus.AiProcessing), ct);
        if (nonTerminalDocs > 0) return;

        var pendingAiFilings = await db.McaFilingDocuments.CountAsync(d =>
            d.BatchId == batchId &&
            (d.AiExtractionStatus == AiExtractionStatus.Pending || d.AiExtractionStatus == AiExtractionStatus.InProgress), ct);
        if (pendingAiFilings > 0) return;

        // Both layers of failure: a document that failed text extraction/OCR outright, and a document
        // whose text extracted fine but whose (filing-level) Gemini call failed — found live in the
        // corpus run, where a filing's extraction timed out but the batch still showed plain "Completed"
        // because this only checked ProcessingStatus, never AiExtractionStatus.
        var hasFailures = await db.McaFilingDocuments.AnyAsync(d =>
            d.BatchId == batchId &&
            (d.ProcessingStatus == FilingDocumentProcessingStatus.Failed
                || d.AiExtractionStatus == AiExtractionStatus.Failed), ct);

        var batch = await db.McaFilingBatches.FirstAsync(b => b.BatchId == batchId, ct);
        if (batch.Status is FilingBatchStatus.Completed or FilingBatchStatus.CompletedWithErrors or FilingBatchStatus.Failed)
            return;

        batch.Status = hasFailures ? FilingBatchStatus.CompletedWithErrors : FilingBatchStatus.Completed;
        batch.CompletedDate = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Recovers work left in a non-terminal state by a crash/restart: anything whose heartbeat
    /// (UpdatedAt) is older than the staleness timeout gets re-enqueued.
    ///
    /// IMPORTANT: this runs once at process startup, where the staleness timeout does NOT apply — a fresh
    /// process guarantees nothing from a prior run is still executing, so every non-terminal row here is
    /// orphaned by definition regardless of how recently it was touched. (Confirmed by a real crash during
    /// development: killing the app mid-batch left documents in TextExtracting that were only ~5 minutes
    /// old, well inside what was then a startup-time staleness filter — they were silently never resumed
    /// until this was fixed.) The staleness timeout is reserved for a hypothetical future periodic sweep
    /// that runs *while the process is alive*, to catch a hung worker without disturbing genuinely
    /// in-flight work — this method doesn't do that (yet), so it recovers unconditionally.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        // Also covers a crash mid-unpack (Unpacking/Indexing), not just before it ever started (Uploaded).
        // IndexNestedZipAsync is idempotent (skips a nested zip it already indexed), and UnpackBatchAsync's
        // own post-unpack enqueue only targets still-Discovered documents, so re-running it from the top
        // is safe rather than producing duplicate McaFiling rows or duplicate ProcessDocumentWorkItems.
        var stuckBatches = await db.McaFilingBatches
            .Where(b => b.Status == FilingBatchStatus.Uploaded
                || b.Status == FilingBatchStatus.Unpacking
                || b.Status == FilingBatchStatus.Indexing)
            .Select(b => b.BatchId)
            .ToHashSetAsync(ct);
        foreach (var id in stuckBatches)
            queue.Enqueue(new UnpackBatchWorkItem(id));

        // Excludes documents belonging to a stuck batch above — UnpackBatchAsync's own re-run already
        // re-enqueues its still-Discovered documents at the end, so including them here too would just be
        // a second, redundant enqueue of the same FilingDocumentIds (harmless now that ProcessDocumentAsync
        // claims atomically, but wasteful).
        var staleDocuments = await db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null && !stuckBatches.Contains(d.BatchId) &&
                (d.ProcessingStatus == FilingDocumentProcessingStatus.Discovered
                    || d.ProcessingStatus == FilingDocumentProcessingStatus.Classified
                    || d.ProcessingStatus == FilingDocumentProcessingStatus.TextExtracting))
            .Select(d => d.FilingDocumentId)
            .ToListAsync(ct);
        foreach (var id in staleDocuments)
            queue.Enqueue(new ProcessDocumentWorkItem(id));

        // The gap that actually bit in review: a document reaching ProcessingStatus=Completed with
        // AiExtractionStatus=Pending (queued for AI) or InProgress (claimed, Gemini call was in flight)
        // leaves no Discovered/TextExtracting document and no stuck batch — Status is already Processing —
        // so without this, RecoverStaleWorkAsync finds nothing and the batch stays stuck forever. Any
        // InProgress row here is, by the same startup-means-orphaned logic as above, from a crashed call
        // that never got a McaFilingExtraction row written — reset to Pending so ExtractFilingAsync's claim
        // predicate (WHERE AiExtractionStatus = Pending) can pick it up again.
        await db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null && d.AiExtractionStatus == AiExtractionStatus.InProgress)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, AiExtractionStatus.Pending), ct);

        var filingsNeedingExtraction = await db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null && d.AiExtractionStatus == AiExtractionStatus.Pending)
            .Select(d => d.FilingId)
            .Distinct()
            .ToListAsync(ct);
        var alreadyExtractedFilingIds = await db.McaFilingExtractions
            .Where(e => e.FilingId != null && filingsNeedingExtraction.Contains(e.FilingId.Value))
            .Select(e => e.FilingId!.Value)
            .ToListAsync(ct);
        var filingIdsToEnqueue = filingsNeedingExtraction.Except(alreadyExtractedFilingIds).ToList();
        foreach (var id in filingIdsToEnqueue)
            queue.Enqueue(new ExtractFilingWorkItem(id));

        return staleDocuments.Count + stuckBatches.Count + filingIdsToEnqueue.Count;
    }
}
