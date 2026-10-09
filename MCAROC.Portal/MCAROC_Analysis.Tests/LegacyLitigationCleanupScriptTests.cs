using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>#425: runs docs/scripts/cleanup_legacy_litigation_duplicates.sql itself (committed, scoped to one seeded request)
/// and checks that merging a legacy duplicate never loses a case identity, an order or the only usable order evidence.</summary>
public class LegacyLitigationCleanupScriptTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<long> SeedRequestAsync()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "LGCL" + Guid.NewGuid().ToString("N")[..8], ClientName = "Cleanup Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Cleanup Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"LGCL-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    private static async Task<long> AddCaseAsync(long requestId, string? providerId, string csp, string? cnr = null,
        string? type = "OS", string? number = "22/2020", string? year = "2020")
    {
        await using var db = CreateContext();
        var row = new LitigationCase
        {
            RequestId = requestId, ProviderCaseId = providerId, CspId = csp, Cnr = cnr, ProceedingType = type,
            CaseNumber = number, CaseYear = year, FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(row);
        await db.SaveChangesAsync();
        return row.LitigationCaseId;
    }

    private static async Task<long> AddOrderAsync(long caseId, string url, LitigationOrderDocumentStatus status = LitigationOrderDocumentStatus.Pending,
        string? text = null, string date = "2020-01-01")
    {
        await using var db = CreateContext();
        var order = new LitigationCaseOrder { LitigationCaseId = caseId, PdfUrl = url, OrderDate = date, OrderType = "Order", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(order);
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument
        {
            Order = order, Status = status, ExtractedText = text, RetainedUntilUtc = DateTime.UtcNow.AddDays(15), CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return order.LitigationCaseOrderId;
    }

    private static async Task RunCleanupAsync(long requestId)
    {
        var script = await File.ReadAllTextAsync(FindScript());
        script = script.Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;")
            .Replace("DECLARE @RequestId bigint = NULL;", $"DECLARE @RequestId bigint = {requestId};");
        Assert.Contains($"@RequestId bigint = {requestId};", script); // the scope really was applied

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(script, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static string FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "scripts", "cleanup_legacy_litigation_duplicates.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("docs/scripts/cleanup_legacy_litigation_duplicates.sql not found above the test output folder.");
    }

    [Fact]
    public async Task Keeps_the_legacy_documents_evidence_when_the_kept_copy_of_the_order_is_still_pending()
    {
        var requestId = await SeedRequestAsync();
        var legacy = await AddCaseAsync(requestId, null, "csp-ev");
        var kept = await AddCaseAsync(requestId, "rec-ev", "csp-ev");
        await AddOrderAsync(legacy, "https://x.example/o1.pdf", LitigationOrderDocumentStatus.Downloaded, text: "older extracted text");
        var keptOrder = await AddOrderAsync(kept, "https://x.example/o1.pdf");

        await RunCleanupAsync(requestId);

        await using var db = CreateContext();
        Assert.Equal(kept, Assert.Single(await db.LitigationCases.Where(c => c.RequestId == requestId).ToListAsync()).LitigationCaseId);
        var order = Assert.Single(await db.LitigationCaseOrders.Where(o => o.LitigationCaseId == kept).ToListAsync());
        Assert.Equal(keptOrder, order.LitigationCaseOrderId);
        var document = await db.LitigationOrderDocuments.SingleAsync(d => d.LitigationCaseOrderId == keptOrder);
        Assert.Equal(LitigationOrderDocumentStatus.Downloaded, document.Status);
        Assert.Equal("older extracted text", document.ExtractedText);
    }

    [Fact]
    public async Task Keeps_the_kept_document_when_both_copies_hold_evidence()
    {
        var requestId = await SeedRequestAsync();
        var legacy = await AddCaseAsync(requestId, null, "csp-both");
        var kept = await AddCaseAsync(requestId, "rec-both", "csp-both");
        await AddOrderAsync(legacy, "https://x.example/o1.pdf", LitigationOrderDocumentStatus.Downloaded, text: "older text");
        var keptOrder = await AddOrderAsync(kept, "https://x.example/o1.pdf", LitigationOrderDocumentStatus.Downloaded, text: "newer text");

        await RunCleanupAsync(requestId);

        await using var db = CreateContext();
        Assert.Equal("newer text", (await db.LitigationOrderDocuments.SingleAsync(d => d.LitigationCaseOrderId == keptOrder)).ExtractedText);
        Assert.Equal(1, await db.LitigationCaseOrders.CountAsync(o => o.LitigationCaseId == kept));
    }

    [Fact]
    public async Task Moves_orders_only_the_legacy_row_holds_and_drops_re_signed_copies()
    {
        var requestId = await SeedRequestAsync();
        var legacy = await AddCaseAsync(requestId, null, "csp-orders");
        var kept = await AddCaseAsync(requestId, "rec-orders", "csp-orders");
        await AddOrderAsync(legacy, "https://x.example/older-only.pdf", LitigationOrderDocumentStatus.Downloaded, text: "only here", date: "2020-02-02");
        await AddOrderAsync(legacy, "https://x.example/s3.pdf?X-Amz-Date=1&X-Amz-Signature=aaa", date: "2020-03-03");
        await AddOrderAsync(kept, "https://x.example/s3.pdf?X-Amz-Date=2&X-Amz-Signature=bbb", date: "2020-03-03");
        await AddOrderAsync(legacy, "https://x.example/get?id=1&X-Amz-Signature=aaa", date: "2020-04-04");
        await AddOrderAsync(kept, "https://x.example/get?id=2&X-Amz-Signature=bbb", date: "2020-04-04");

        await RunCleanupAsync(requestId);

        await using var db = CreateContext();
        var urls = await db.LitigationCaseOrders.Where(o => o.LitigationCaseId == kept).Select(o => o.PdfUrl).ToListAsync();
        Assert.Equal(4, urls.Count);
        Assert.Contains("https://x.example/older-only.pdf", urls);
        Assert.Contains("https://x.example/get?id=1&X-Amz-Signature=aaa", urls); // a different document, not a re-signed copy
        Assert.DoesNotContain("https://x.example/s3.pdf?X-Amz-Date=1&X-Amz-Signature=aaa", urls);
        var moved = await db.LitigationCaseOrders.SingleAsync(o => o.LitigationCaseId == kept && o.PdfUrl == "https://x.example/older-only.pdf");
        Assert.Equal("only here", (await db.LitigationOrderDocuments.SingleAsync(d => d.LitigationCaseOrderId == moved.LitigationCaseOrderId)).ExtractedText);
    }

    [Fact]
    public async Task Does_not_merge_conflicting_CNRs_or_rows_with_blank_identity_fields()
    {
        var requestId = await SeedRequestAsync();
        await AddCaseAsync(requestId, null, "csp-cnr", cnr: "TNKP070001332023");
        await AddCaseAsync(requestId, "rec-cnr", "csp-cnr", cnr: "TNKP070009992023");
        await AddCaseAsync(requestId, null, "csp-blank", type: null, number: "", year: null);
        await AddCaseAsync(requestId, "rec-blank", "csp-blank", type: null, number: "", year: null);

        await RunCleanupAsync(requestId);

        await using var db = CreateContext();
        var rows = await db.LitigationCases.Where(c => c.RequestId == requestId).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal("TNKP070001332023", rows.Single(r => r.CspId == "csp-cnr" && r.ProviderCaseId is null).Cnr);
    }
}
