using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
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
        // 1. git-ignored Fixtures/workbooks/{roc,charge}.xls (local dev).
        var dir = Path.Combine(RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis.Tests", "Fixtures", "workbooks");
        var roc = Path.Combine(dir, "roc.xls");
        var charge = Path.Combine(dir, "charge.xls");
        if (File.Exists(roc) && File.Exists(charge)) return (roc, charge);

        // 2. a secured directory on the self-hosted runner, by original filename (CI sets this env var).
        var env = Environment.GetEnvironmentVariable("MCAROC_RECON_FIXTURES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var r = Path.Combine(env, "U45203OR1995PLC003982.xls");
            var c = Path.Combine(env, "U45203OR1995PLC003982-charge.xls");
            if (File.Exists(r) && File.Exists(c)) return (r, c);
        }
        return null;
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

        var facts = await db.FinancialFacts.Where(x => x.IngestionRunId == run.IngestionRunId).ToListAsync();
        Assert.True(facts.Count > 50, $"unmapped financial line items should be captured, got {facts.Count}");

        // ── exact content, not just totals ──

        // A known row is captured verbatim on its own sheet at its own row number, and the hash is
        // exactly SHA-256(CellsJson).
        var cinRow = sourceRows.First(s => s.WorkbookRole == "RocReport"
            && s.CellsJson.Contains("U45203OR1995PLC003982"));
        var cells = System.Text.Json.JsonSerializer.Deserialize<string?[]>(cinRow.CellsJson)!;
        Assert.Contains(cells, v => v == "U45203OR1995PLC003982");
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(cinRow.CellsJson)));
        Assert.Equal(expectedHash, cinRow.RowHash);

        // Re-recording the same workbook is deterministic — same (sheet, row) → same CellsJson + hash.
        var reRecorded = SourceRowRecorder.Record(reader.ReadWorkbook(fx.Roc), "RocReport", 1, 1, 1, DateTime.UtcNow);
        var same = reRecorded.First(s => s.SheetName == cinRow.SheetName && s.RowNumber == cinRow.RowNumber);
        Assert.Equal(cinRow.CellsJson, same.CellsJson);
        Assert.Equal(cinRow.RowHash, same.RowHash);

        // Representative financial retention: at least one ratio and one balance-sheet reserves line
        // survived into FinancialFacts with a parsed number.
        Assert.Contains(facts, x => x.Section == FinancialStatementSection.Ratios && x.NumericValue is not null);
        Assert.Contains(facts, x => x.Label.Contains("Reserves", StringComparison.OrdinalIgnoreCase) && x.NumericValue is not null);

        // No structural year-header row leaked in as a fact.
        Assert.DoesNotContain(facts, x => x.Label.Trim().Equals("Year", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Control totals for the real COASTAL <c>Legal History</c> sheet (960 rows): the parser
    /// must extract exactly 592 Confirmed + 68 Probable + 292 Uncertain = 952, and no more — the 8
    /// structural rows (section titles, per-section headers, the blank separator) must not become
    /// records, and none of the three column layouts may bleed into another. The one-row synthetic
    /// unit test proves the mapping; this proves the whole 960-row input is exhaustively bounded.</summary>
    [SkippableFact]
    public void Legal_history_sheet_extracts_the_exact_control_totals_for_all_three_sections()
    {
        Skip.If(Fixtures() is null,
            "Reconciliation workbooks not present — see MCAROC_Analysis.Tests/Fixtures/README.md");

        var sheets = new ExcelSheetReader().ReadWorkbook(Fixtures()!.Value.Roc);
        var legalHistory = sheets.Single(s => s.Name == "Legal History");
        Assert.Equal(960, legalHistory.Rows.Count);

        var items = LegalHistoryParser.Parse(legalHistory, requestId: 1, ingestionRunId: 1, sourceDocumentId: 1).Items;

        var confirmed = items.Where(x => x.MatchStatus == LitigationMatchStatus.Confirmed).ToList();
        var probable = items.Where(x => x.MatchStatus == LitigationMatchStatus.Probable).ToList();
        var uncertain = items.Where(x => x.MatchStatus == LitigationMatchStatus.Uncertain).ToList();

        Assert.Equal(592, confirmed.Count);
        Assert.Equal(68, probable.Count);
        Assert.Equal(292, uncertain.Count);
        Assert.Equal(952, items.Count); // 960 rows - 8 structural (2 titles, 2 sub-headers, 1 blank, ... )

        // ── Confirmed: full 7-column layout, first + last ──
        Assert.Equal("Filed Against this Corporate", confirmed[0].CaseType);
        Assert.Equal("Insolvency", confirmed[0].CaseCategory);
        Assert.Equal("State Bank of India", confirmed[0].Litigants);
        Assert.StartsWith("(CP(IB)No.593/KB/2017)", confirmed[0].CaseNumber);
        Assert.Equal("Consolidation of Corporate Affairs", confirmed[^1].CaseType);
        Assert.Equal("Disposed", confirmed[^1].CaseStatus);
        Assert.Contains("TPNo.94/CTB/2019", confirmed[^1].CaseNumber);

        // ── Probable: shifted layout (no Case Type; Petitioner + Respondent) ──
        Assert.All(probable, p => Assert.Null(p.CaseType));
        Assert.Equal("Pending", probable[0].CaseStatus);
        Assert.Equal("Insolvency", probable[0].CaseCategory);
        Assert.Contains("APPELLATE", probable[0].Court);
        Assert.Contains("Himachal Pradesh Power Corporation", probable[0].Litigants);
        Assert.Contains(" vs. ", probable[0].Litigants);
        Assert.StartsWith("Company Appeal(AT)(Ins) - 935/2023", probable[0].CaseNumber);
        Assert.Equal("Disposed", probable[^1].CaseStatus);
        Assert.Equal("UN CR /18/2014", probable[^1].CaseNumber);

        // ── Uncertain: short 5-column layout (Court | Petitioner | Respondent | Case No | Date) ──
        Assert.All(uncertain, u =>
        {
            Assert.Null(u.CaseType);
            Assert.Null(u.CaseStatus);
            Assert.Null(u.CaseCategory);
            Assert.NotNull(u.Court);
        });
        Assert.Equal("CCH1 PRL. CITY CIVIL AND SESSIONS JUDGE", uncertain[0].Court);
        Assert.Equal("BHARATH HEAVY ELECTRICALS LIMITED vs. M/S COASTAL PROJECTS LIMITED", uncertain[0].Litigants);
        Assert.Equal("AA/319/2018", uncertain[0].CaseNumber);
        Assert.Equal("SR. CIVIL COURTS, HYDERABAD - C", uncertain[^1].Court);
        Assert.Equal("EP/200110/2014", uncertain[^1].CaseNumber);

        // No structural row leaked through as a record.
        Assert.DoesNotContain(items, x => x.Court is "Court" or "PROBABLE CASES" or "UNVERIFIED COURT RECORDS");
        Assert.DoesNotContain(items, x => x.CaseStatus is "Case Status" or "PROBABLE CASES");
    }

    [Fact]
    public async Task Duplicate_raw_row_insertion_is_rejected_by_the_database()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = $"DUP{Guid.NewGuid():N}"[..10], ClientName = "Dup", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Dup Co",
            RequestNumber = $"DUP-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.Running };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();

        SourceRow Make() => new()
        {
            RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, SourceDocumentId = 1,
            WorkbookRole = "RocReport", SheetName = "Directors", SheetIndex = 2, RowNumber = 5,
            CellsJson = "[\"x\"]", RowHash = new string('0', 64), ExtractedAt = DateTime.UtcNow
        };
        db.SourceRows.Add(Make());
        await db.SaveChangesAsync();

        db.SourceRows.Add(Make());
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
