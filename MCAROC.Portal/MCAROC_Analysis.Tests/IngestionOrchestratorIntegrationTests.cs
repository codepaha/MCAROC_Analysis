using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>Runs the full orchestrator (real SQL Server, not EF's InMemory provider, since InMemory
/// doesn't enforce constraints or reflect real transaction/rollback behavior) against a dedicated test
/// database, with a fake sheet reader standing in for real Excel files. Requires .\SQLEXPRESS to be
/// reachable locally — skip this class if that's not available in your environment.</summary>
public class IngestionOrchestratorIntegrationTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options;
        return new AppDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static SheetData CompanyProfileSheet(string cin) => Sheet("About the Company",
        Row("Legal Name", "TEST COMPANY PRIVATE LIMITED"),
        Row("CIN", cin),
        Row("PAN", "AAAAA0000A"),
        Row("Company Status", "Active"));

    private static SheetData DirectorsSheet() => Sheet("Directors",
        Row("NAME", "DIN", "PRESENT DESIGNATION", "PRESENT DESIGNATION APPOINTMENT DATE", "ORIGINAL APPOINTMENT DATE", "DATE OF CESSATION", "FLAGS"),
        Row("TEST DIRECTOR", 12345678.0, "Director", "1 Jan, 2020", "1 Jan, 2020", "-", "-"));

    [Fact]
    public async Task SuccessfulRunPersistsExtractedDataAndMarksRequestComplete()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Test Client", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Company",
            RequestNumber = $"TEST-{Guid.NewGuid():N}",
            Cin = "U00000TEST0000000001", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        var rocDoc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaRocReport, OriginalFileName = "roc.xls",
            StoredFileName = "1.xls", StoragePath = @"C:\fake\roc.xls", FileHash = "abc", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(rocDoc);
        await db.SaveChangesAsync();

        var sheetReader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [rocDoc.StoragePath] = [CompanyProfileSheet(request.Cin!), DirectorsSheet()]
        });
        var orchestrator = new IngestionOrchestrator(db, sheetReader, NullLogger<IngestionOrchestrator>.Instance);

        var run = await orchestrator.RunAsync(request.RequestId, rocDoc.DocumentId, chargeDocumentId: null);

        Assert.Equal(IngestionRunStatus.CompletedClean, run.Status);
        Assert.Equal(1, run.RunNumber);

        await using var verifyDb = CreateContext();
        var reloadedRequest = await verifyDb.Requests.FirstAsync(r => r.RequestId == request.RequestId);
        Assert.Equal(RequestStatus.DataExtracted, reloadedRequest.RequestStatus);
        Assert.Equal(run.IngestionRunId, reloadedRequest.LatestCompletedIngestionRunId);

        var profile = await verifyDb.CompanyProfiles.FirstAsync(p => p.IngestionRunId == run.IngestionRunId);
        Assert.Equal("TEST COMPANY PRIVATE LIMITED", profile.CompanyName);
        var director = await verifyDb.Directors.FirstAsync(d => d.IngestionRunId == run.IngestionRunId);
        Assert.Equal("12345678", director.Din); // already 8 digits — DIN zero-padding is exercised in DinNormalizerTests
    }

    [Fact]
    public async Task MissingRequiredSheetFailsTheRunWithoutOrphaningPartialData()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Test Client", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Broken Company",
            RequestNumber = $"TEST-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        var rocDoc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaRocReport, OriginalFileName = "roc.xls",
            StoredFileName = "1.xls", StoragePath = @"C:\fake\broken.xls", FileHash = "abc", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(rocDoc);
        await db.SaveChangesAsync();

        // No "About the Company" sheet at all — should fail the run, not silently produce partial data.
        var sheetReader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [rocDoc.StoragePath] = [DirectorsSheet()]
        });
        var orchestrator = new IngestionOrchestrator(db, sheetReader, NullLogger<IngestionOrchestrator>.Instance);

        var run = await orchestrator.RunAsync(request.RequestId, rocDoc.DocumentId, chargeDocumentId: null);

        Assert.Equal(IngestionRunStatus.Failed, run.Status);
        Assert.NotNull(run.FailureReason);

        await using var verifyDb = CreateContext();
        var reloadedRequest = await verifyDb.Requests.FirstAsync(r => r.RequestId == request.RequestId);
        Assert.Equal(RequestStatus.ExtractionFailed, reloadedRequest.RequestStatus);
        Assert.Null(reloadedRequest.LatestCompletedIngestionRunId);

        // Nothing from this failed attempt should have landed, including rows for sections that never
        // even got the chance to run (CompanyProfile lookup fails before any other parser executes).
        var orphanedDirectors = await verifyDb.Directors.Where(d => d.RequestId == request.RequestId).ToListAsync();
        Assert.Empty(orphanedDirectors);
    }

    private static SheetData OpenChargesSequenceSheet() => Sheet("Open Charges Sequence",
        Row("SERIAL NUMBER", "CHARGE ID", "STATUS", "DATE", "FILING DATE", "HOLDER NAME", "CHARGE AMOUNT (Rs. Crore)", "PROPERTY TYPE", "NUMBER OF HOLDERS"),
        Row(1.1, 100000001.0, "Creation", "10 Mar, 2026", "6 Apr, 2026", "SOME BANK LIMITED", 5.0, "-", 1.0));

    [Fact]
    public async Task ChargeReportWithMatchingCinButMismatchedNameOrPan_IsQuarantinedNotUsedForEnrichment()
    {
        // Regression test for a real gap: checking CIN alone let a charge workbook belonging to a
        // different company (matching/blank CIN, but a different Legal Name or PAN) through unchallenged.
        await using var db = CreateContext();
        var client = new Client { ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Test Client", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Company",
            RequestNumber = $"TEST-{Guid.NewGuid():N}",
            Cin = "U00000TEST0000000001", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        var rocDoc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaRocReport, OriginalFileName = "roc.xls",
            StoredFileName = "1.xls", StoragePath = @"C:\fake\roc-mismatch.xls", FileHash = "abc", UploadedDate = DateTime.UtcNow
        };
        var chargeDoc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.ChargeReport, OriginalFileName = "charge.xls",
            StoredFileName = "2.xls", StoragePath = @"C:\fake\charge-mismatch.xls", FileHash = "def", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.AddRange(rocDoc, chargeDoc);
        await db.SaveChangesAsync();

        // Same CIN as the ROC report, but a different Legal Name and PAN — must be caught by name/PAN
        // comparison since CIN alone matches.
        var mismatchedChargeCompanySheet = Sheet("About the Company",
            Row("Legal Name", "A COMPLETELY DIFFERENT COMPANY PRIVATE LIMITED"),
            Row("CIN", "U00000TEST0000000001"),
            Row("PAN", "ZZZZZ9999Z"));

        var sheetReader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [rocDoc.StoragePath] = [CompanyProfileSheet(request.Cin!), OpenChargesSequenceSheet()],
            [chargeDoc.StoragePath] = [mismatchedChargeCompanySheet]
        });
        var orchestrator = new IngestionOrchestrator(db, sheetReader, NullLogger<IngestionOrchestrator>.Instance);

        var run = await orchestrator.RunAsync(request.RequestId, rocDoc.DocumentId, chargeDoc.DocumentId);

        Assert.NotEqual(IngestionRunStatus.Failed, run.Status);

        await using var verifyDb = CreateContext();
        var reloadedCharge = await verifyDb.RequestDocuments.FirstAsync(d => d.DocumentId == chargeDoc.DocumentId);
        Assert.Equal(DocumentUploadStatus.Quarantined, reloadedCharge.UploadStatus);
        Assert.Contains("company name", reloadedCharge.QuarantineReason, StringComparison.OrdinalIgnoreCase);

        var reloadedRequest = await verifyDb.Requests.FirstAsync(r => r.RequestId == request.RequestId);
        Assert.True(reloadedRequest.IsManualReviewRequired);

        // The ROC report's own charge sequence data still ingests — only enrichment from the mismatched
        // charge workbook is skipped, per ChargesParser's design (see plan doc).
        var charges = await verifyDb.RocCharges.Where(c => c.IngestionRunId == run.IngestionRunId).ToListAsync();
        var chargeEvent = Assert.Single(await verifyDb.RocChargeEvents.Where(e => e.IngestionRunId == run.IngestionRunId).ToListAsync());
        Assert.Equal(ChargeEventMatchConfidence.Unmatched, chargeEvent.MatchConfidence); // never enriched from the quarantined workbook
        Assert.Single(charges);
    }
}
