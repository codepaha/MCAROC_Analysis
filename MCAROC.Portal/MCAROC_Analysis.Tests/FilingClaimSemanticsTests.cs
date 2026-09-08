using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression coverage for the review finding that recovery/retry could enqueue duplicate work
/// with "only a non-unique lookup before the paid Vertex call." These test the exact SQL-level claim
/// predicates FilingBatchProcessor.ProcessDocumentAsync and ExtractFilingAsync use directly against a real
/// SQL Server test database (not mocked), since the guarantee is about how SQL Server serializes
/// concurrent UPDATEs against the same predicate — not something a fake DbContext would exercise
/// meaningfully. Requires .\SQLEXPRESS.</summary>
public class FilingClaimSemanticsTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(McaRequest request, McaFilingBatch batch, McaFiling filing)> SeedFilingAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Test Client", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"TEST-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        var doc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip",
            StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = doc.DocumentId, Status = FilingBatchStatus.Processing, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "1", NestedZipName = "1.zip", OuterCategoryFolder = "x" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        return (request, batch, filing);
    }

    [Fact]
    public async Task DocumentProcessingClaim_OnlySucceedsOnce()
    {
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingAsync(db);
        var doc = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Discovered, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        // Mirrors FilingBatchProcessor.ProcessDocumentAsync's claim exactly.
        Task<int> Claim() => db.McaFilingDocuments
            .Where(d => d.FilingDocumentId == doc.FilingDocumentId && d.ProcessingStatus == FilingDocumentProcessingStatus.Discovered)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ProcessingStatus, FilingDocumentProcessingStatus.TextExtracting));

        var first = await Claim();
        var second = await Claim();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task AiExtractionClaim_OnlySucceedsOnce()
    {
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingAsync(db);
        var doc = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            AiExtractionStatus = AiExtractionStatus.Pending, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        // Mirrors FilingBatchProcessor.ExtractFilingAsync's claim exactly.
        Task<int> Claim() => db.McaFilingDocuments
            .Where(d => d.FilingId == filing.FilingId && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
                && d.AiExtractionStatus == AiExtractionStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, AiExtractionStatus.InProgress));

        var first = await Claim();
        var second = await Claim();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task StrayPendingDocumentsInANonEligibleFiling_GetResetNotStuckForever()
    {
        // Regression test for a real bug found live during corpus validation: a 29-document filing where
        // the dominant category (18 Financial) wasn't AI-eligible left 11 individually-eligible documents
        // (1 Charge + 10 Compliance) stuck at AiExtractionStatus=Pending forever, because ExtractFilingAsync
        // returned early without resetting them — which meant MaybeCompleteBatchAsync's
        // "any Pending/InProgress documents?" check could never clear, so the whole batch stayed
        // Processing indefinitely. This mirrors ClearStrayPendingAiStatusAsync's exact predicate.
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingAsync(db);

        var financial = Enumerable.Range(0, 18).Select(i => new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = $"fin-{i}.pdf", StoragePath = @"C:\fake\f.pdf", FileHash = $"fh{i}",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed, Category = FilingCategory.Financial,
            AiExtractionStatus = AiExtractionStatus.NotApplicable, UpdatedAt = DateTime.UtcNow
        });
        var strayEligible = Enumerable.Range(0, 11).Select(i => new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = $"stray-{i}.pdf", StoragePath = @"C:\fake\s.pdf", FileHash = $"sh{i}",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            Category = i == 0 ? FilingCategory.Charge : FilingCategory.Compliance,
            AiExtractionStatus = AiExtractionStatus.Pending, UpdatedAt = DateTime.UtcNow
        });
        db.McaFilingDocuments.AddRange(financial.Concat(strayEligible));
        await db.SaveChangesAsync();

        // Mirrors ClearStrayPendingAiStatusAsync's exact predicate.
        var cleared = await db.McaFilingDocuments
            .Where(d => d.FilingId == filing.FilingId && d.AiExtractionStatus == AiExtractionStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, AiExtractionStatus.NotApplicable));

        Assert.Equal(11, cleared);
        var remainingPending = await db.McaFilingDocuments.CountAsync(d => d.FilingId == filing.FilingId && d.AiExtractionStatus == AiExtractionStatus.Pending);
        Assert.Equal(0, remainingPending);
    }

    [Fact]
    public async Task DuplicateInsertConflict_ReconciliationTerminalizesClaimedDocumentsToWinnersOutcome()
    {
        // Regression test for the review finding that ExtractFilingAsync's DbUpdateException catch (unique
        // index on McaFilingExtraction.FilingId) logged and returned WITHOUT terminalizing the documents it
        // had claimed to InProgress — permanently stranding them, since RecoverStaleWorkAsync's old logic
        // would then reset them to Pending but skip re-enqueueing because an extraction row already
        // existed. This mirrors the catch block's exact fix: read the winning row's Status, then
        // ExecuteUpdateAsync the claimed document ids to that outcome directly.
        await using var db = CreateContext();
        var (_, batch, filing) = await SeedFilingAsync(db);

        var claimedDoc = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = filing.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            AiExtractionStatus = AiExtractionStatus.InProgress, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(claimedDoc);
        // The "winning" concurrent call's row — already committed before this call's SaveChangesAsync failed.
        db.McaFilingExtractions.Add(new McaFilingExtraction
        {
            FilingId = filing.FilingId, Model = "m", PromptVersion = "1", SchemaName = "s", SchemaVersion = "1",
            ValidationStatus = ExtractionValidationStatus.Valid, Status = ExtractionStatus.Success, ExtractedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var eligibleDocumentIds = new HashSet<long> { claimedDoc.FilingDocumentId };

        // Mirrors ExtractFilingAsync's catch-block reconciliation exactly.
        var winningStatus = await db.McaFilingExtractions
            .Where(e => e.FilingId == filing.FilingId)
            .OrderBy(e => e.ExtractionId)
            .Select(e => e.Status)
            .FirstAsync();
        var reconciledStatus = winningStatus == ExtractionStatus.Success ? AiExtractionStatus.Success : AiExtractionStatus.Failed;
        await db.McaFilingDocuments
            .Where(d => eligibleDocumentIds.Contains(d.FilingDocumentId))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, reconciledStatus));

        var finalStatus = await db.McaFilingDocuments
            .Where(d => d.FilingDocumentId == claimedDoc.FilingDocumentId)
            .Select(d => d.AiExtractionStatus)
            .FirstAsync();
        Assert.Equal(AiExtractionStatus.Success, finalStatus);
    }

    [Fact]
    public async Task RecoverStaleWork_InProgressDocumentWithExistingExtractionRow_IsReconciledNotResetToPending()
    {
        // Regression test mirroring RecoverStaleWorkAsync's fixed InProgress-handling: a document left
        // InProgress by a crash whose filing already has a McaFilingExtraction row (a concurrent call won
        // the race) must be reconciled to that row's outcome, not blindly reset to Pending — resetting to
        // Pending would strand it, since nothing re-claims a Pending document once its filing already has
        // an extraction row.
        await using var db = CreateContext();
        var (_, batch, filing) = await SeedFilingAsync(db);

        var doc = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = filing.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            AiExtractionStatus = AiExtractionStatus.InProgress, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(doc);
        db.McaFilingExtractions.Add(new McaFilingExtraction
        {
            FilingId = filing.FilingId, Model = "m", PromptVersion = "1", SchemaName = "s", SchemaVersion = "1",
            ValidationStatus = ExtractionValidationStatus.Valid, Status = ExtractionStatus.Failed, ExtractedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Mirrors RecoverStaleWorkAsync's fixed reconciliation branch exactly.
        var inProgressDocuments = await db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null && d.AiExtractionStatus == AiExtractionStatus.InProgress)
            .Select(d => new { d.FilingDocumentId, d.FilingId })
            .ToListAsync();
        var extractionStatusByFilingId = await db.McaFilingExtractions
            .Where(e => e.FilingId != null && inProgressDocuments.Select(d => d.FilingId).Contains(e.FilingId!.Value))
            .GroupBy(e => e.FilingId!.Value)
            .Select(g => new { FilingId = g.Key, Status = g.OrderBy(e => e.ExtractionId).First().Status })
            .ToDictionaryAsync(x => x.FilingId, x => x.Status);

        foreach (var group in inProgressDocuments.GroupBy(d => d.FilingId))
        {
            var ids = group.Select(x => x.FilingDocumentId).ToList();
            if (extractionStatusByFilingId.TryGetValue(group.Key, out var winningStatus))
            {
                var reconciledStatus = winningStatus == ExtractionStatus.Success ? AiExtractionStatus.Success : AiExtractionStatus.Failed;
                await db.McaFilingDocuments.Where(d => ids.Contains(d.FilingDocumentId))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, reconciledStatus));
            }
            else
            {
                await db.McaFilingDocuments.Where(d => ids.Contains(d.FilingDocumentId))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, AiExtractionStatus.Pending));
            }
        }

        var finalStatus = await db.McaFilingDocuments
            .Where(d => d.FilingDocumentId == doc.FilingDocumentId)
            .Select(d => d.AiExtractionStatus)
            .FirstAsync();
        Assert.Equal(AiExtractionStatus.Failed, finalStatus); // matches the existing extraction row's outcome, not Pending
    }

    [Fact]
    public async Task RecoverStaleWork_InProgressDocumentWithNoExtractionRow_IsResetToPending()
    {
        // The other branch of the same fixed logic: no extraction row exists yet (crash happened before
        // Gemini ever responded) — must still reset to Pending so it gets re-claimed and re-processed.
        await using var db = CreateContext();
        var (_, batch, filing) = await SeedFilingAsync(db);

        var doc = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = filing.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            AiExtractionStatus = AiExtractionStatus.InProgress, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var inProgressDocuments = await db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null && d.AiExtractionStatus == AiExtractionStatus.InProgress)
            .Select(d => new { d.FilingDocumentId, d.FilingId })
            .ToListAsync();
        var extractionStatusByFilingId = await db.McaFilingExtractions
            .Where(e => e.FilingId != null && inProgressDocuments.Select(d => d.FilingId).Contains(e.FilingId!.Value))
            .GroupBy(e => e.FilingId!.Value)
            .Select(g => new { FilingId = g.Key, Status = g.OrderBy(e => e.ExtractionId).First().Status })
            .ToDictionaryAsync(x => x.FilingId, x => x.Status);

        foreach (var group in inProgressDocuments.GroupBy(d => d.FilingId))
        {
            var ids = group.Select(x => x.FilingDocumentId).ToList();
            if (extractionStatusByFilingId.TryGetValue(group.Key, out var winningStatus))
            {
                var reconciledStatus = winningStatus == ExtractionStatus.Success ? AiExtractionStatus.Success : AiExtractionStatus.Failed;
                await db.McaFilingDocuments.Where(d => ids.Contains(d.FilingDocumentId))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, reconciledStatus));
            }
            else
            {
                await db.McaFilingDocuments.Where(d => ids.Contains(d.FilingDocumentId))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.AiExtractionStatus, AiExtractionStatus.Pending));
            }
        }

        var finalStatus = await db.McaFilingDocuments
            .Where(d => d.FilingDocumentId == doc.FilingDocumentId)
            .Select(d => d.AiExtractionStatus)
            .FirstAsync();
        Assert.Equal(AiExtractionStatus.Pending, finalStatus);
    }

    [Fact]
    public async Task UniqueIndexOnExtractionFilingId_RejectsADuplicateInsert()
    {
        await using var db = CreateContext();
        var (_, _, filing) = await SeedFilingAsync(db);

        db.McaFilingExtractions.Add(new McaFilingExtraction
        {
            FilingId = filing.FilingId, Model = "m", PromptVersion = "1", SchemaName = "s", SchemaVersion = "1",
            ValidationStatus = ExtractionValidationStatus.Valid, Status = ExtractionStatus.Success, ExtractedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.McaFilingExtractions.Add(new McaFilingExtraction
        {
            FilingId = filing.FilingId, Model = "m", PromptVersion = "1", SchemaName = "s", SchemaVersion = "1",
            ValidationStatus = ExtractionValidationStatus.Valid, Status = ExtractionStatus.Success, ExtractedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
