using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>End-to-end reconciliation against two REAL <c>.xls</c> workbooks: ingest them and prove the
/// Layer-0 <see cref="SourceRow"/> table balances against what the Excel reader sees — nothing is
/// dropped. The workbooks are git-ignored real company data, so this class skips on CI and anywhere the
/// files are absent (see <c>Fixtures/README.md</c>).</summary>
public class SourceReconciliationTests : IAsyncLifetime
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static (string Roc, string Charge)? Fixtures()
    {
        var dir = Path.Combine(RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis.Tests", "Fixtures", "workbooks");
        var roc = Path.Combine(dir, "roc.xls");
        var charge = Path.Combine(dir, "charge.xls");
        return File.Exists(roc) && File.Exists(charge) ? (roc, charge) : null;
    }

    /// <summary>How SourceRowRecorder counts a "source row": a sheet row with at least one non-blank cell.</summary>
    private static int NonBlankRowCount(IReadOnlyList<SheetData> workbook)
    {
        var n = 0;
        foreach (var sheet in workbook)
            foreach (var row in sheet.Rows)
            {
                var blank = true;
                foreach (var cell in row)
                    if (!string.IsNullOrWhiteSpace(cell?.ToString())) { blank = false; break; }
                if (!blank) n++;
            }
        return n;
    }

    [SkippableFact]
    public async Task Every_workbook_row_is_preserved_in_SourceRows_and_lineage_resolves()
    {
        Skip.If(Fixtures() is null,
            "Reconciliation workbooks not present — see MCAROC_Analysis.Tests/Fixtures/README.md");
        var fx = Fixtures()!.Value;

        await using var db = CreateContext();
        var reader = new ExcelSheetReader();

        var client = new Client { ClientCode = $"REC{Guid.NewGuid():N}"[..10], ClientName = "Recon", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Coastal Projects Limited",
            Cin = "U45203OR1995PLC003982", RequestNumber = $"REC-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        var rocDoc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaRocReport, OriginalFileName = "roc.xls",
            StoredFileName = "roc.xls", StoragePath = fx.Roc, FileHash = "roc", UploadedDate = DateTime.UtcNow
        };
        var chargeDoc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.ChargeReport, OriginalFileName = "charge.xls",
            StoredFileName = "charge.xls", StoragePath = fx.Charge, FileHash = "chg", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.AddRange(rocDoc, chargeDoc);
        await db.SaveChangesAsync();

        var orchestrator = new IngestionOrchestrator(db, reader, NullLogger<IngestionOrchestrator>.Instance);
        var run = await orchestrator.RunAsync(request.RequestId, rocDoc.DocumentId, chargeDoc.DocumentId);

        Assert.NotEqual(IngestionRunStatus.Failed, run.Status);

        var expectedRoc = NonBlankRowCount(reader.ReadWorkbook(fx.Roc));
        var expectedCharge = NonBlankRowCount(reader.ReadWorkbook(fx.Charge));

        var sourceRows = await db.SourceRows.Where(x => x.IngestionRunId == run.IngestionRunId).ToListAsync();

        // Layer 0 balances: every non-blank row of every sheet of both workbooks is present.
        Assert.Equal(expectedRoc, sourceRows.Count(x => x.WorkbookRole == "RocReport"));
        Assert.Equal(expectedCharge, sourceRows.Count(x => x.WorkbookRole == "ChargeReport"));

        // Lineage resolves: each typed Director maps back to a SourceRow on its sheet + row.
        var directors = await db.Directors.Where(x => x.IngestionRunId == run.IngestionRunId).ToListAsync();
        Assert.NotEmpty(directors);
        foreach (var d in directors)
            Assert.Contains(sourceRows, s => s.SheetName == d.SourceSheetName && s.RowNumber == d.SourceRowNumber);

        // Completeness the typed layer used to lose:
        var officers = await db.CompanyOfficers.CountAsync(x => x.IngestionRunId == run.IngestionRunId);
        Assert.True(officers >= 3, $"expected >= 3 non-DIN officers, got {officers}");

        var suitFiled = await db.ComplianceRecords.CountAsync(x =>
            x.IngestionRunId == run.IngestionRunId && x.RecordType == ComplianceRecordType.SuitFiled);
        Assert.True(suitFiled > 500, $"suit-filed history should be kept in full, got {suitFiled}");

        var facts = await db.FinancialFacts.CountAsync(x => x.IngestionRunId == run.IngestionRunId);
        Assert.True(facts > 50, $"unmapped financial line items should be captured, got {facts}");
    }
}
