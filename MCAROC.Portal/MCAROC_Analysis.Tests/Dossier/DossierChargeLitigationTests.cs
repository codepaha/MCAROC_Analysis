using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Dossier;
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
}
