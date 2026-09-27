using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #293 against a real SQL Server: the backfill fills every row, is idempotent, repairs stale
/// values, and the harness's normalized lookup finds variants the legacy prefix search can't. Rows use a
/// per-test identifier prefix and are removed afterwards (the test database is shared).</summary>
public class CompanyMasterNameBackfillTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly string _prefix = $"ZNB{Guid.NewGuid():N}"[..13].ToUpperInvariant();

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        await db.CompanyMasterRecords.Where(r => r.Identifier.StartsWith(_prefix)).ExecuteDeleteAsync();
    }

    private async Task SeedAsync(params (string Suffix, CompanyMasterRecordType Type, string Name)[] rows)
    {
        await using var db = CreateContext();
        foreach (var (suffix, type, name) in rows)
            db.CompanyMasterRecords.Add(new CompanyMasterRecord { Identifier = _prefix + suffix, RecordType = type, Name = name });
        await db.SaveChangesAsync();
    }

    private async Task<CompanyMasterRecord> GetAsync(string suffix)
    {
        await using var db = CreateContext();
        return await db.CompanyMasterRecords.AsNoTracking().SingleAsync(r => r.Identifier == _prefix + suffix);
    }

    [Fact]
    public async Task Backfill_FillsMissingColumns_AndLeavesNoneMissing()
    {
        await SeedAsync(("A", CompanyMasterRecordType.Company, "Sharma & Sons Pvt. Ltd."),
                        ("B", CompanyMasterRecordType.Llp, "Sunrise Advisors L.L.P."));

        await using var connection = new SqlConnection(ConnectionString);
        var result = await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true, batchSize: 1);

        Assert.Equal(0, result.RowsStillMissing);
        Assert.True(result.RowsUpdated >= 2);

        var a = await GetAsync("A");
        Assert.Equal("SHARMA AND SONS PRIVATE LIMITED", a.NameNormalized);
        Assert.Equal("SHARMA AND SONS", a.NameCore);
        Assert.Equal(EntityForm.Private, a.EntityForm);

        var b = await GetAsync("B");
        Assert.Equal("SUNRISE ADVISORS LLP", b.NameNormalized);
        Assert.Equal(EntityForm.Llp, b.EntityForm);
    }

    [Fact]
    public async Task Backfill_RerunOverEveryRow_RewritesNothingAlreadyCorrect()
    {
        await SeedAsync(("A", CompanyMasterRecordType.Company, "Coastal Projects Limited"));
        await using var connection = new SqlConnection(ConnectionString);
        await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);

        var rerun = await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: false);

        Assert.Equal(0, rerun.RowsUpdated);
        Assert.Equal(0, rerun.RowsStillMissing);
    }

    [Fact]
    public async Task Backfill_AllRows_RepairsAStaleValue()
    {
        await SeedAsync(("A", CompanyMasterRecordType.Company, "Coastal Projects Limited"));
        await using var connection = new SqlConnection(ConnectionString);
        await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);
        await using (var db = CreateContext())
            await db.CompanyMasterRecords.Where(r => r.Identifier == _prefix + "A")
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.NameNormalized, "COMPUTED BY AN OLDER VERSION"));

        var result = await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: false);

        Assert.True(result.RowsUpdated >= 1);
        Assert.Equal("COASTAL PROJECTS LIMITED", (await GetAsync("A")).NameNormalized);
    }

    [Fact]
    public async Task NormalizedLookup_FindsAVariantTheLegacyPrefixSearchMisses()
    {
        var core = $"{_prefix} SUNRISE ADVISORS";
        await SeedAsync(("CO", CompanyMasterRecordType.Company, core + " PRIVATE LIMITED"),
                        ("LL", CompanyMasterRecordType.Llp, core + " LLP"));
        await using var connection = new SqlConnection(ConnectionString);
        await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);
        var input = core.ToLowerInvariant() + " Pvt. Ltd.";

        var legacy = await NameResolutionHarness.LegacyPrefixAsync(connection, input);
        var normalized = await NameResolutionHarness.NormalizedLookupAsync(connection, input);

        Assert.Empty(legacy);
        Assert.Equal(1.0, normalized.Single(c => c.Identifier == _prefix + "CO").Score);
        Assert.Equal(0.6, normalized.Single(c => c.Identifier == _prefix + "LL").Score); // same core, other legal form
        Assert.Equal(_prefix + "CO", NameResolutionEvaluator.AutoSelect(normalized, 0.9));
    }

    [Fact]
    public async Task NormalizedLookup_SameNameTwins_Abstain()
    {
        var name = $"{_prefix} TWIN TRADERS PRIVATE LIMITED";
        await SeedAsync(("T1", CompanyMasterRecordType.Company, name), ("T2", CompanyMasterRecordType.Company, name));
        await using var connection = new SqlConnection(ConnectionString);
        await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);

        var candidates = await NameResolutionHarness.NormalizedLookupAsync(connection, name);

        Assert.Equal(2, candidates.Count);
        Assert.Null(NameResolutionEvaluator.AutoSelect(candidates, 0.5));
    }
}
