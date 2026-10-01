using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>Charge-to-case links in the dossier: shown on the affected charge and as a callout when the client's
/// dossier includes litigation, absent otherwise, and never served stale after a litigation refresh.</summary>
public class DossierChargeLitigationTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
    }

    /// <summary>PdfPig drops the space at a line wrap and renders ligatures as a NUL: compare squashed.</summary>
    private static string Squash(string s) => new string(s.Where(c => !char.IsWhiteSpace(c) && c != '\0').ToArray());

    private static string TextOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Squash(string.Concat(doc.GetPages().Select(p => p.Text)));
    }

    private static ChargeLitigationSummary SummaryFor(long chargeId, string chargeNumber) => new([
        new ChargeLitigationLink(chargeId, chargeNumber, "State Bank of India", ChargeLitigationSignal.ImmovableAddress,
            new LinkedLitigation("Court records", 10, null, "CS 88/2021", "High Court of Bombay", "Pending", null, null),
            "The order names the property charged under this charge.", 500, 2, "Plot No. A-36, Nayapalli is under attachment.", "Order"),
        new ChargeLitigationLink(chargeId, chargeNumber, "State Bank of India", ChargeLitigationSignal.LenderRecoveryCase,
            new LinkedLitigation("MCA workbook", null, 7, "OA 12/2021", "DRT Hyderabad", "Pending", null, "Debts Recovery Tribunal"),
            "The holder is a party in a recovery-type proceeding. Indirect: the assets may be contested.")], 3, 2, 1, true, 0, 2, SnapshotId: 42);

    [SkippableFact]
    public async Task Pdf_marks_the_affected_charge_and_adds_a_callout_when_the_client_includes_litigation()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = (await new DossierAssembler(db).BuildAsync(requestId))!;
        var charge = model.Charges.Open.First();

        var withLinks = model with { IncludeLitigation = true, ChargeLinks = SummaryFor(charge.ChargeId, charge.RocChargeNumber) };
        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(withLinks, DossierVariant.Executive));

        Assert.Contains(Squash("charged property: 1 open charge(s)"), text);   // the callout
        Assert.Contains(Squash("CS 88/2021"), text);                            // the case, on the charge
        Assert.Contains(Squash("Case names the charged"), text);
        Assert.Contains("Nayapalli", text);                                      // the evidence excerpt
        Assert.Contains(Squash("(page 2)"), text);
        Assert.Contains(Squash("OA 12/2021"), text);
        Assert.Contains(Squash("NCLT/NCLAT orders are not scanned"), text);
        Assert.Contains(Squash("Only strong matches are shown"), text);
    }

    [SkippableFact]
    public async Task Pdf_shows_no_links_when_the_client_excludes_litigation_or_there_are_none()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = (await new DossierAssembler(db).BuildAsync(requestId))!;
        var charge = model.Charges.Open.First();
        var links = SummaryFor(charge.ChargeId, charge.RocChargeNumber);

        var excluded = TextOf(new DossierPdfRenderer(WebRoot()).Render(model with { IncludeLitigation = false, ChargeLinks = links }, DossierVariant.Executive));
        Assert.DoesNotContain(Squash("CS 88/2021"), excluded);
        Assert.DoesNotContain(Squash("OA 12/2021"), excluded);
        Assert.DoesNotContain(Squash("charged property: 1 open charge"), excluded);

        var none = TextOf(new DossierPdfRenderer(WebRoot()).Render(model with { IncludeLitigation = true, ChargeLinks = ChargeLitigationSummary.Empty }, DossierVariant.Executive));
        Assert.DoesNotContain(Squash("CS 88/2021"), none);
        Assert.DoesNotContain(Squash("charged property: 1 open charge"), none);

        var unknown = TextOf(new DossierPdfRenderer(WebRoot()).Render(model with { IncludeLitigation = true, ChargeLinks = null }, DossierVariant.Executive));
        Assert.DoesNotContain(Squash("CS 88/2021"), unknown);
    }

    [Fact]
    public async Task Pdf_with_links_renders_without_error_on_any_platform()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = (await new DossierAssembler(db).BuildAsync(requestId))!;
        var charge = model.Charges.Open.First();

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model with { ChargeLinks = SummaryFor(charge.ChargeId, charge.RocChargeNumber) }, DossierVariant.Executive);

        Assert.True(pdf.Length > 5000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public async Task A_litigation_refresh_produces_a_new_dossier_model_instead_of_serving_the_cached_one()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var cache = new DossierCache(db, new DossierAssembler(db, new ChargeLitigationService(db, memory)), memory);

        var first = await cache.GetAsync(requestId);
        var again = await cache.GetAsync(requestId);
        Assert.NotNull(first);
        Assert.Same(first, again);                        // unchanged data: served from cache

        // A refreshed litigation search completes: a new snapshot, retrieved later.
        var job = new LitigationSearchJob { RequestId = requestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow, RawResponseHash = "refresh-" + requestId };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        db.LitigationReportSnapshots.Add(new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = requestId, ReportHash = job.RawResponseHash!,
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var afterRefresh = await cache.GetAsync(requestId);

        Assert.NotNull(afterRefresh);
        Assert.NotSame(first, afterRefresh);              // its links were built from the old snapshot: rebuilt
        Assert.NotNull(afterRefresh!.ChargeLinks?.SnapshotId);
    }

    // ── order text extracted AFTER the snapshot (review of #335) ──────────────────────────────────────────

    private const string MortgageParticulars = "Equitable mortgage on the land and building at Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, 751012";
    private const string OrderNamingProperty = "--- Page 1 (native) ---\nIN THE HIGH COURT OF ORISSA\n\n--- Page 2 (native) ---\nThe property situated at Plot No. A-36, Nayapalli, Bhubaneswar is attached.";

    private sealed class TestEnv(string contentRoot) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
    }

    /// <summary>A dossier-ready request whose charge C1 mortgages Plot A-36, with a completed litigation snapshot
    /// whose single order has NOT had its text extracted yet (as right after the snapshot completes).</summary>
    private static async Task<(long RequestId, long OrderId)> SeedPendingExtractionAsync(Data.AppDbContext db)
    {
        var (requestId, runId, _) = await DossierTestSeed.SeedAsync(db);
        var c1 = await db.RocCharges.Include(c => c.Events).FirstAsync(c => c.RequestId == requestId && c.RocChargeNumber == "C1");
        foreach (var ev in c1.Events) { ev.PropertyType = "Immovable property"; ev.PropertyParticulars = MortgageParticulars; }

        var when = DateTime.UtcNow.AddDays(-1);
        var job = new LitigationSearchJob { RequestId = requestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = when, CompletedUtc = when, RawResponseHash = "late-" + requestId };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = requestId, ReportHash = job.RawResponseHash!, Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = when, CreatedUtc = when };
        var litCase = new LitigationCase { RequestId = requestId, Court = "High Court of Orissa", CaseNumber = "WP(C) 11227/2019", CaseStatus = "Pending", FirstSeenUtc = when, LastSeenUtc = when };
        db.LitigationReportSnapshots.Add(snapshot);
        db.LitigationCases.Add(litCase);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = litCase.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = when });
        var order = new LitigationCaseOrder { LitigationCaseId = litCase.LitigationCaseId, OrderDate = "15-10-2019", OrderType = "Order", CreatedUtc = when };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument { LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded, RetainedUntilUtc = DateTime.UtcNow.AddDays(7) });
        await db.SaveChangesAsync();
        return (requestId, order.LitigationCaseOrderId);
    }

    [Fact]
    public async Task Order_text_extracted_after_a_dossier_was_rendered_regenerates_the_pdf_with_the_new_link()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, orderId) = await SeedPendingExtractionAsync(seed);

        var root = Path.Combine(Path.GetTempPath(), "dossier-late-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var db = DossierGoldenMasterTests.CreateContext();
            var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
            var cache = new DossierCache(db, new DossierAssembler(db, new ChargeLitigationService(db, memory)), memory);
            var gate = new CalculationArtifactGateService(db, new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<CalculationArtifactGateService>.Instance);
            var artifacts = new DossierArtifactService(cache, new DossierPdfRenderer(WebRoot()), gate, new TestEnv(root));

            // 1. Rendered while the order's text is still pending: nothing to compare, so no link.
            var before = await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None);
            Assert.True(before.Rendered);
            Assert.False((await cache.GetAsync(requestId))!.ChargeLinks!.HasAny);
            var pathBefore = before.Path!;
            Assert.True(File.Exists(pathBefore));

            // 2. Extraction finishes under the SAME snapshot: the matching text is published.
            await using var writer = DossierGoldenMasterTests.CreateContext();
            await writer.LitigationOrderDocuments.Where(d => d.LitigationCaseOrderId == orderId)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.ExtractedText, OrderNamingProperty)
                    .SetProperty(d => d.TextExtractionStatus, (FilingDocumentProcessingStatus?)FilingDocumentProcessingStatus.TextExtracted)
                    .SetProperty(d => d.ExtractedUtc, (DateTime?)DateTime.UtcNow));

            // 3. The next download must not reuse the earlier file: it is regenerated and carries the link.
            var after = await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None);
            Assert.True(after.Rendered);
            Assert.NotEqual(pathBefore, after.Path);                       // a different file, not File.Exists on the old one
            Assert.True(File.Exists(after.Path));
            var links = (await cache.GetAsync(requestId))!.ChargeLinks!;
            Assert.True(links.HasAny);
            Assert.Contains("WP(C) 11227/2019", links.Links.Select(l => l.Case.CaseNumber));

            if (OperatingSystem.IsWindows())   // PdfPig text extraction is only reliable on Windows fonts
            {
                Assert.DoesNotContain(Squash("WP(C) 11227/2019"), TextOf(await File.ReadAllBytesAsync(pathBefore)));
                Assert.Contains(Squash("WP(C) 11227/2019"), TextOf(await File.ReadAllBytesAsync(after.Path!)));
            }

            // 4. With nothing further changed, a third download reuses the second file (the cache still works).
            var again = await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None);
            Assert.Equal(after.Path, again.Path);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
