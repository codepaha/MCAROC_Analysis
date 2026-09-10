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
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

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

        // A11: charges exist but the charge workbook was unusable (quarantined) → same flag as "not provided".
        var quarantinedRun = await verifyDb.IngestionRuns.FirstAsync(x => x.IngestionRunId == run.IngestionRunId);
        Assert.True(quarantinedRun.ChargeReportMissing);
    }

    [Fact]
    public async Task RunAsync_RecordsEveryOptionalSheetAbsentFromTheUpload()
    {
        await using var db = CreateContext();
        var (request, rocDoc) = await SeedRequestWithRoc(db, @"C:\fake\roc-sparse.xls");

        // Only the required company sheet + Directors — every other tracked optional sheet is absent.
        var sheetReader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [rocDoc.StoragePath] = [CompanyProfileSheet(request.Cin!), DirectorsSheet()]
        });
        var run = await new IngestionOrchestrator(db, sheetReader, NullLogger<IngestionOrchestrator>.Instance)
            .RunAsync(request.RequestId, rocDoc.DocumentId, chargeDocumentId: null);

        Assert.NotEqual(IngestionRunStatus.Failed, run.Status);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.IngestionRuns.FirstAsync(x => x.IngestionRunId == run.IngestionRunId);

        var absent = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reloaded.AbsentOptionalSheetsJson)!;
        var directorsCanonical = SheetAliases.CanonicalName(SheetAliases.Directors);

        // Directors WAS present → not in the absent list; every other tracked optional sheet IS.
        // This exact-count assertion is also the structural guard that every entry in
        // TrackedOptionalSheets is resolved through the SheetPresence tracker: a tracked sheet the
        // orchestrator never looks up would never be added to _absent, so the count would fall short.
        Assert.DoesNotContain(directorsCanonical, absent);
        Assert.Equal(SheetAliases.TrackedOptionalSheets.Count - 1, absent.Count);
        Assert.Contains(SheetAliases.CanonicalName(SheetAliases.LegalHistory), absent);
        Assert.Contains(SheetAliases.CanonicalName(SheetAliases.PeerComparison), absent);

        // No charges in the ROC report → the "charge report missing" flag stays off.
        Assert.False(reloaded.ChargeReportMissing);

        var coverage = MCAROC_Analysis.Models.SheetCoverage.From(reloaded);
        Assert.Equal(1, coverage.PresentOptionalSheets);
        Assert.True(coverage.WasAbsent(SheetAliases.LegalHistory));
        Assert.False(coverage.WasAbsent(SheetAliases.Directors));
    }

    [Fact]
    public async Task RunAsync_DoesNotRecordEpfoAnnexureAbsent_WhenTheUploadContainsIt()
    {
        // Regression (#75 review): the EPFO section is built from "Annexure - EPFO Establishments"
        // (SheetAliases.EpfoAnnexure) — the one the parser consumes and the one tracked. The separate
        // latest-only "EPFO Establishments" summary sheet (SheetAliases.Epfo) is intentionally NOT
        // parsed or tracked until #36. An upload carrying both must not show EPFO as missing.
        await using var db = CreateContext();
        var (request, rocDoc) = await SeedRequestWithRoc(db, @"C:\fake\roc-with-epfo.xls");

        var epfoSummary = Sheet("EPFO Establishments",
            Row("WORKING STATUS", "ESTABLISHMENT ID", "ESTABLISHMENT NAME", "WAGE MONTH", "TRRN", "NO. OF EMPLOYEES", "AMOUNT (Rs. Crore)"),
            Row("Working", "ORXXX0012345000", "COASTAL HEAD OFFICE", "Jan-2023", "-", 100.0, 1.25));
        var epfoAnnexure = Sheet("Annexure - EPFO Establishments",
            Row("WORKING STATUS", "ESTABLISHMENT ID", "ESTABLISHMENT NAME", "WAGE MONTH", "TRRN", "NO. OF EMPLOYEES", "AMOUNT (Rs. Crore)", "DATE OF CREDIT", "PAYMENT DUE DATE", "STATUS"),
            Row("Working", "ORXXX0012345000", "COASTAL HEAD OFFICE", "Jan-2023", "TRRN123", 100.0, 1.25, "15 Feb, 2023", "20 Feb, 2023", "Paid"));

        var sheetReader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [rocDoc.StoragePath] = [CompanyProfileSheet(request.Cin!), epfoSummary, epfoAnnexure]
        });
        var run = await new IngestionOrchestrator(db, sheetReader, NullLogger<IngestionOrchestrator>.Instance)
            .RunAsync(request.RequestId, rocDoc.DocumentId, chargeDocumentId: null);

        Assert.NotEqual(IngestionRunStatus.Failed, run.Status);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.IngestionRuns.FirstAsync(x => x.IngestionRunId == run.IngestionRunId);
        var absent = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reloaded.AbsentOptionalSheetsJson)!;

        Assert.DoesNotContain(SheetAliases.CanonicalName(SheetAliases.EpfoAnnexure), absent);
        Assert.False(MCAROC_Analysis.Models.SheetCoverage.From(reloaded).WasAbsent(SheetAliases.EpfoAnnexure));
        Assert.True(await verifyDb.EpfoContributions.AnyAsync(x => x.IngestionRunId == run.IngestionRunId));
    }

    [Fact]
    public async Task RunAsync_FlagsChargeReportMissing_WhenRocListsChargesButNoChargeWorkbookIsSupplied()
    {
        await using var db = CreateContext();
        var (request, rocDoc) = await SeedRequestWithRoc(db, @"C:\fake\roc-charges-no-workbook.xls");

        var sheetReader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [rocDoc.StoragePath] = [CompanyProfileSheet(request.Cin!), OpenChargesSequenceSheet()]
        });
        var run = await new IngestionOrchestrator(db, sheetReader, NullLogger<IngestionOrchestrator>.Instance)
            .RunAsync(request.RequestId, rocDoc.DocumentId, chargeDocumentId: null);

        Assert.NotEqual(IngestionRunStatus.Failed, run.Status);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.IngestionRuns.FirstAsync(x => x.IngestionRunId == run.IngestionRunId);
        Assert.True(await verifyDb.RocCharges.AnyAsync(c => c.IngestionRunId == run.IngestionRunId));
        Assert.True(reloaded.ChargeReportMissing);
    }

    private static async Task<(McaRequest Request, RequestDocument RocDoc)> SeedRequestWithRoc(AppDbContext db, string rocStoragePath)
    {
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
            StoredFileName = "1.xls", StoragePath = rocStoragePath, FileHash = "abc", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(rocDoc);
        await db.SaveChangesAsync();
        return (request, rocDoc);
    }
}
