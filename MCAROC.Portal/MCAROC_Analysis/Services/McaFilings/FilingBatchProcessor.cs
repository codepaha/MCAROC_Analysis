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

            using (var outerArchive = ZipFile.OpenRead(outerZipDoc.StoragePath))
            {
                var nestedZipEntries = outerArchive.Entries.Where(e => e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var nestedEntry in nestedZipEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    await IndexNestedZipAsync(batch, nestedEntry, tempDir, ct);
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

    private async Task IndexNestedZipAsync(McaFilingBatch batch, ZipArchiveEntry nestedEntry, string batchTempDir, CancellationToken ct)
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
            var nestedSafety = ArchiveSafetyValidator.ValidateNestedArchive(tempNestedZipPath, Limits, currentDepth: 2);

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
        var document = await db.McaFilingDocuments.Include(d => d.Filing).FirstAsync(d => d.FilingDocumentId == filingDocumentId, ct);
        document.ProcessingStatus = FilingDocumentProcessingStatus.TextExtracting;
        document.ProcessingStartedAt = DateTime.UtcNow;
        document.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

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

    public async Task ExtractFilingAsync(long filingId, CancellationToken ct)
    {
        var filing = await db.McaFilings.FirstAsync(f => f.FilingId == filingId, ct);
        if (!filing.IdentityMatchesRequest)
            return; // quarantined at indexing time — never send to Gemini

        var alreadyExtracted = await db.McaFilingExtractions.AnyAsync(e => e.FilingId == filingId, ct);
        if (alreadyExtracted)
            return; // idempotent guard against duplicate enqueue from near-simultaneous document completions

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
            return; // Financial/Unclassified — stored and text-extracted, no Gemini extraction

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

        foreach (var d in documents) d.AiExtractionStatus = AiExtractionStatus.Pending;
        await db.SaveChangesAsync(ct);

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

        var finalStatus = outcome.Status == ExtractionStatus.Success ? AiExtractionStatus.Success : AiExtractionStatus.Failed;
        foreach (var d in documents) d.AiExtractionStatus = finalStatus;
        if (outcome.Status != ExtractionStatus.Success)
        {
            filing.ManualReviewRequired = true;
            filing.ManualReviewReason = (filing.ManualReviewReason is null ? "" : filing.ManualReviewReason + " ") + $"AI extraction failed: {outcome.FailureReason}";
        }

        await db.SaveChangesAsync(ct);
        await MaybeCompleteBatchAsync(filing.BatchId, ct);
    }

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
            d.BatchId == batchId && d.AiExtractionStatus == AiExtractionStatus.Pending, ct);
        if (pendingAiFilings > 0) return;

        var hasFailures = await db.McaFilingDocuments.AnyAsync(d =>
            d.BatchId == batchId && d.ProcessingStatus == FilingDocumentProcessingStatus.Failed, ct);

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
        var staleDocuments = await db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null &&
                (d.ProcessingStatus == FilingDocumentProcessingStatus.Discovered
                    || d.ProcessingStatus == FilingDocumentProcessingStatus.Classified
                    || d.ProcessingStatus == FilingDocumentProcessingStatus.TextExtracting))
            .Select(d => d.FilingDocumentId)
            .ToListAsync(ct);

        foreach (var id in staleDocuments)
            queue.Enqueue(new ProcessDocumentWorkItem(id));

        // Also covers a crash mid-unpack (Unpacking/Indexing), not just before it ever started (Uploaded).
        // IndexNestedZipAsync is idempotent (skips a nested zip it already indexed), so re-running
        // UnpackBatchAsync from the top is safe rather than producing duplicate McaFiling rows.
        var stuckBatches = await db.McaFilingBatches
            .Where(b => b.Status == FilingBatchStatus.Uploaded
                || b.Status == FilingBatchStatus.Unpacking
                || b.Status == FilingBatchStatus.Indexing)
            .Select(b => b.BatchId)
            .ToListAsync(ct);
        foreach (var id in stuckBatches)
            queue.Enqueue(new UnpackBatchWorkItem(id));

        return staleDocuments.Count + stuckBatches.Count;
    }
}
