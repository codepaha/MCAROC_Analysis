using System.IO.Compression;
using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

public class McaDocumentRetentionTests : IAsyncLifetime, IDisposable
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mca-retention-tests-" + Guid.NewGuid().ToString("N"));

    public McaDocumentRetentionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class FakeHostEnv(string path) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public string ContentRootPath { get; set; } = path;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class NoOpHealthService : IIntegrationHealthService
    {
        public Task ReportFailureAsync(IntegrationName name, DateTime callStartUtc, string? error, bool authRejected, int threshold, TimeSpan probeLease, CancellationToken ct) => Task.CompletedTask;
        public Task ReportSuccessAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> TryClaimHalfOpenProbeAsync(IntegrationName name, TimeSpan probeLease, CancellationToken ct) => Task.FromResult(false);
        public Task TryCloseAfterProbeAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct) => Task.CompletedTask;
        public Task<IntegrationHealth?> GetAsync(IntegrationName name, CancellationToken ct) => Task.FromResult<IntegrationHealth?>(null);
        public Task<bool> IsOpenAsync(IntegrationName name, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task Prunes_expired_non_charge_pdfs_while_preserving_charge_and_sidecars()
    {
        await using var db = CreateContext();

        var client = await db.Clients.FirstAsync();
        var request = new McaRequest
        {
            ClientId = client.ClientId,
            RequestNumber = "REQ-RET-" + Guid.NewGuid().ToString("N")[..8], CompanyName = "Retention Test Corp",
            Cin = "U12345MH2020PTC123456",
            CreatedDate = DateTime.UtcNow,
            KeepPermanently = false
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow.AddDays(-20),
            CompletedDate = DateTime.UtcNow.AddDays(-20),
            ChargeLinksStamp = "stamp-123"
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            Srn = "SRN-RETENTION-1"
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        // 1. Regular non-charge document (should be pruned)
        var file1Path = Path.Combine(_tempDir, "doc1.pdf");
        await File.WriteAllBytesAsync(file1Path, "%PDF-1.4\nRegular Doc"u8.ToArray());
        var text1Path = Path.Combine(_tempDir, "doc1.txt");
        await File.WriteAllTextAsync(text1Path, "Extracted text of regular doc");

        var doc1 = new McaFilingDocument
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            FilingId = filing.FilingId,
            OriginalFileName = "annual-return.pdf",
            Category = FilingCategory.Financial,
            StoragePath = file1Path,
            ExtractedTextPath = text1Path,
            SourceAwsPath = "annual-return-aws.pdf",
            FileHash = "HASH1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            UpdatedAt = DateTime.UtcNow
        };

        // 2. Charge document candidate (Category == Charge) -> PRESERVED
        var fileChargePath = Path.Combine(_tempDir, "charge.pdf");
        await File.WriteAllBytesAsync(fileChargePath, "%PDF-1.4\nCharge Doc"u8.ToArray());

        var docCharge = new McaFilingDocument
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            FilingId = filing.FilingId,
            OriginalFileName = "Form CHG-1.pdf",
            Category = FilingCategory.Charge,
            StoragePath = fileChargePath,
            SourceAwsPath = "charge-aws.pdf",
            FileHash = "HASH_CHARGE",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            UpdatedAt = DateTime.UtcNow
        };

        // 3. Document linked in ChargeDocumentLinks -> PRESERVED
        var fileLinkedPath = Path.Combine(_tempDir, "sanction-letter.pdf");
        await File.WriteAllBytesAsync(fileLinkedPath, "%PDF-1.4\nSanction Letter"u8.ToArray());

        var docLinked = new McaFilingDocument
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            FilingId = filing.FilingId,
            OriginalFileName = "sanction-letter.pdf",
            Category = FilingCategory.Unclassified,
            StoragePath = fileLinkedPath,
            SourceAwsPath = "sanction-aws.pdf",
            FileHash = "HASH_LINKED",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            UpdatedAt = DateTime.UtcNow
        };

        db.McaFilingDocuments.AddRange(doc1, docCharge, docLinked);
        await db.SaveChangesAsync();

        var link = new ChargeDocumentLink
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            RocChargeNumber = "100200",
            FilingDocumentId = docLinked.FilingDocumentId,
            Method = ChargeDocumentLinkMethod.FileName
        };
        db.ChargeDocumentLinks.Add(link);
        await db.SaveChangesAsync();

        var opts = Options.Create(new ReferenceToolOptions
        {
            DocumentRetentionDays = 15,
            KeepDocumentsPermanently = false
        });

        var retentionService = new McaDocumentRetentionService(db, opts, TimeProvider.System, NullLogger<McaDocumentRetentionService>.Instance);
        var pruned = await retentionService.PruneExpiredDocumentsAsync();

        Assert.Equal(1, pruned);

        // Verify doc1 is pruned
        var refreshedDoc1 = await db.McaFilingDocuments.FirstAsync(d => d.FilingDocumentId == doc1.FilingDocumentId);
        Assert.NotNull(refreshedDoc1.RetiredUtc);
        Assert.Equal(string.Empty, refreshedDoc1.StoragePath);
        Assert.False(File.Exists(file1Path));
        Assert.True(File.Exists(text1Path)); // Extracted text PRESERVED

        // Verify charge docs are NOT pruned
        var refreshedCharge = await db.McaFilingDocuments.FirstAsync(d => d.FilingDocumentId == docCharge.FilingDocumentId);
        Assert.Null(refreshedCharge.RetiredUtc);
        Assert.True(File.Exists(fileChargePath));

        var refreshedLinked = await db.McaFilingDocuments.FirstAsync(d => d.FilingDocumentId == docLinked.FilingDocumentId);
        Assert.Null(refreshedLinked.RetiredUtc);
        Assert.True(File.Exists(fileLinkedPath));
    }

    [Fact]
    public async Task Prunes_superseded_batch_chunks_after_grace_period()
    {
        await using var db = CreateContext();

        var client = await db.Clients.FirstAsync();
        var request = new McaRequest
        {
            ClientId = client.ClientId,
            RequestNumber = "REQ-CHK-" + Guid.NewGuid().ToString("N")[..8], CompanyName = "Chunks Pruning Corp",
            Cin = "U99999MH2021PTC999999",
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        // Old superseded batch (completed 40 days ago)
        var oldBatch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow.AddDays(-40),
            CompletedDate = DateTime.UtcNow.AddDays(-40),
            ChargeLinksStamp = "stamp-old"
        };

        // Newer authoritative batch (completed 5 days ago)
        var authorBatch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow.AddDays(-5),
            CompletedDate = DateTime.UtcNow.AddDays(-5),
            ChargeLinksStamp = "stamp-new"
        };

        db.McaFilingBatches.AddRange(oldBatch, authorBatch);
        await db.SaveChangesAsync();

        var chunkOld = new DocumentChunk
        {
            RequestId = request.RequestId,
            BatchId = oldBatch.BatchId,
            FilingDocumentId = 1,
            FilingId = 1,
            ChunkIndex = 0,
            PageNumber = 1,
            ChunkText = "Old chunk", Embedding = new Microsoft.Data.SqlTypes.SqlVector<float>(new float[768]),
            CreatedDate = DateTime.UtcNow.AddDays(-40)
        };

        var chunkNew = new DocumentChunk
        {
            RequestId = request.RequestId,
            BatchId = authorBatch.BatchId,
            FilingDocumentId = 2,
            FilingId = 2,
            ChunkIndex = 0,
            PageNumber = 1,
            ChunkText = "Authoritative chunk", Embedding = new Microsoft.Data.SqlTypes.SqlVector<float>(new float[768]),
            CreatedDate = DateTime.UtcNow.AddDays(-5)
        };

        db.DocumentChunks.AddRange(chunkOld, chunkNew);
        await db.SaveChangesAsync();

        var opts = Options.Create(new ReferenceToolOptions
        {
            SupersededBatchGraceDays = 30
        });

        var retentionService = new McaDocumentRetentionService(db, opts, TimeProvider.System, NullLogger<McaDocumentRetentionService>.Instance);
        var deletedChunks = await retentionService.PruneSupersededBatchChunksAsync();

        Assert.Equal(1, deletedChunks);

        var remainingChunks = await db.DocumentChunks.Where(c => c.RequestId == request.RequestId).ToListAsync();
        Assert.Single(remainingChunks);
        Assert.Equal(authorBatch.BatchId, remainingChunks[0].BatchId);
    }

    [Fact]
    public async Task Restore_rejects_when_one_year_unlock_has_expired()
    {
        await using var db = CreateContext();

        var cin = "U8" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant() + "MH2019PTC888888";
        var client = await db.Clients.FirstAsync();
        var request = new McaRequest
        {
            ClientId = client.ClientId,
            RequestNumber = "REQ-EXP-" + Guid.NewGuid().ToString("N")[..8], CompanyName = "Unlock Expired Corp",
            Cin = cin,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);

        // Lifecycle expired: unlocked 14 months ago
        var lifecycle = new CompanyReportLifecycle
        {
            Identifier = cin,
            UnlockedUtc = DateTime.UtcNow.AddMonths(-14),
            State = CompanyReportLifecycleState.Expired
        };
        db.CompanyReportLifecycles.Add(lifecycle);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            Srn = "SRN-EXP-1"
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        var doc = new McaFilingDocument
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            FilingId = filing.FilingId,
            OriginalFileName = "test.pdf",
            SourceAwsPath = "aws/path/test.pdf",
            RetiredUtc = DateTime.UtcNow.AddDays(-10),
            StoragePath = string.Empty,
            FileHash = "TESTHASH"
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var opts = Options.Create(new ReferenceToolOptions());
        var env = new FakeHostEnv(_tempDir);
        var session = new ReferenceToolSession();
        var health = new NoOpHealthService();
        var refClient = new ReferenceToolClient(new System.Net.Http.HttpClient(), opts, session, health, NullLogger<ReferenceToolClient>.Instance);

        var restoreService = new McaDocumentRestoreService(db, refClient, env, opts, TimeProvider.System, NullLogger<McaDocumentRestoreService>.Instance);
        var result = await restoreService.RestoreDocumentAsync(doc);

        Assert.Equal(DocumentRestoreStatus.SourceExpired, result.Status);
        Assert.Contains("expired", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_redownloads_and_validates_hash_successfully()
    {
        await using var db = CreateContext();

        var cin = "U7" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant() + "MH2020PTC777777";
        var client = await db.Clients.FirstAsync();
        var request = new McaRequest
        {
            ClientId = client.ClientId,
            RequestNumber = "REQ-RST-" + Guid.NewGuid().ToString("N")[..8],
            CompanyName = "Restore Valid Corp",
            Cin = cin,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);

        // Lifecycle: unlocked 2 months ago (valid)
        var lifecycle = new CompanyReportLifecycle
        {
            Identifier = cin,
            UnlockedUtc = DateTime.UtcNow.AddMonths(-2),
            State = CompanyReportLifecycleState.Unlocked
        };
        db.CompanyReportLifecycles.Add(lifecycle);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            Srn = "SRN-RESTORE-1"
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        var samplePdfBytes = "%PDF-1.4\n%Stub Content for restore"u8.ToArray();
        var expectedHash = Convert.ToHexString(SHA256.HashData(samplePdfBytes));

        var doc = new McaFilingDocument
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            FilingId = filing.FilingId,
            OriginalFileName = "test-doc.pdf",
            SourceDocId = "DOC-123",
            SourceAwsPath = "aws/path/test-doc.pdf",
            RetiredUtc = DateTime.UtcNow.AddDays(-5),
            StoragePath = string.Empty,
            FileHash = expectedHash
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var fakeHandler = new FakeDownloadHandler(samplePdfBytes);
        var httpClient = new System.Net.Http.HttpClient(fakeHandler);
        var opts = Options.Create(new ReferenceToolOptions { BaseUrl = "https://reference.test", SessionCookie = "fake_cookie", UserId = "12345" });
        var env = new FakeHostEnv(_tempDir);
        var session = new ReferenceToolSession();
        session.SetCookie("fake_cookie", "12345");
        var health = new NoOpHealthService();
        var refClient = new ReferenceToolClient(httpClient, opts, session, health, NullLogger<ReferenceToolClient>.Instance);

        var restoreService = new McaDocumentRestoreService(db, refClient, env, opts, TimeProvider.System, NullLogger<McaDocumentRestoreService>.Instance);
        var result = await restoreService.RestoreDocumentAsync(doc);

        Assert.Equal(DocumentRestoreStatus.Success, result.Status);
        Assert.NotNull(result.RestoredPath);
        Assert.True(File.Exists(result.RestoredPath));
        Assert.Null(doc.RetiredUtc);

        var refreshed = await db.McaFilingDocuments.FirstAsync(d => d.FilingDocumentId == doc.FilingDocumentId);
        Assert.Null(refreshed.RetiredUtc);
        Assert.Equal(result.RestoredPath, refreshed.StoragePath);
    }

    [Fact]
    public async Task Restore_rejects_when_downloaded_hash_mismatches()
    {
        await using var db = CreateContext();

        var cin = "U6" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant() + "MH2020PTC666666";
        var client = await db.Clients.FirstAsync();
        var request = new McaRequest
        {
            ClientId = client.ClientId,
            RequestNumber = "REQ-MIS-" + Guid.NewGuid().ToString("N")[..8],
            CompanyName = "Restore Mismatch Corp",
            Cin = cin,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);

        var lifecycle = new CompanyReportLifecycle
        {
            Identifier = cin,
            UnlockedUtc = DateTime.UtcNow.AddMonths(-2),
            State = CompanyReportLifecycleState.Unlocked
        };
        db.CompanyReportLifecycles.Add(lifecycle);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            Srn = "SRN-RESTORE-2"
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        var samplePdfBytes = "%PDF-1.4\n%Stub Content for restore"u8.ToArray();

        var doc = new McaFilingDocument
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            FilingId = filing.FilingId,
            OriginalFileName = "test-doc.pdf",
            SourceDocId = "DOC-123",
            SourceAwsPath = "aws/path/test-doc.pdf",
            RetiredUtc = DateTime.UtcNow.AddDays(-5),
            StoragePath = string.Empty,
            FileHash = "DIFFERENT_HASH"
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var fakeHandler = new FakeDownloadHandler(samplePdfBytes);
        var httpClient = new System.Net.Http.HttpClient(fakeHandler);
        var opts = Options.Create(new ReferenceToolOptions { BaseUrl = "https://reference.test", SessionCookie = "fake_cookie", UserId = "12345" });
        var env = new FakeHostEnv(_tempDir);
        var session = new ReferenceToolSession();
        session.SetCookie("fake_cookie", "12345");
        var health = new NoOpHealthService();
        var refClient = new ReferenceToolClient(httpClient, opts, session, health, NullLogger<ReferenceToolClient>.Instance);

        var restoreService = new McaDocumentRestoreService(db, refClient, env, opts, TimeProvider.System, NullLogger<McaDocumentRestoreService>.Instance);
        var result = await restoreService.RestoreDocumentAsync(doc);

        Assert.Equal(DocumentRestoreStatus.HashMismatch, result.Status);
        Assert.Null(result.RestoredPath);
        Assert.NotNull(doc.RetiredUtc);
    }

    private sealed class FakeDownloadHandler(byte[] content) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(content)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            return Task.FromResult(response);
        }
    }
}