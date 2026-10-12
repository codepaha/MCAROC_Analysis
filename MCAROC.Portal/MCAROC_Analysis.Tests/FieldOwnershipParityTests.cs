using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>The field-ownership check of plan section 12.7: a column the portal owns must hold what the projector writes.</summary>
public class FieldOwnershipParityTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime T0 = new(2026, 10, 11, 6, 0, 0, DateTimeKind.Utc);

    private static LitigationModuleParity.OwnedCase Owned(string? legacyCourt, string? projectedCourt, string source = "Portal", bool confirmedEmpty = false,
        long version = 2, long? published = 2) =>
        new(7, new Dictionary<string, string?> { [LivePortalOwnership.Court] = legacyCourt },
            new Dictionary<string, string?> { [LiveProjectionEngine.FCourt] = projectedCourt },
            new Dictionary<string, FieldSource> { [LiveProjectionEngine.FCourt] = new(source, T0, 1, confirmedEmpty) }, version, published);

    [Fact]
    public void A_portal_owned_column_that_matches_the_projection_is_clean()
    {
        var (unexplained, explained) = LitigationModuleParity.OwnershipDifferences([Owned("DRT Mumbai 1", "DRT Mumbai 1")]);
        Assert.Empty(unexplained);
        Assert.Empty(explained);
    }

    [Fact]
    public void A_differing_portal_owned_column_with_nothing_pending_is_a_second_writer()
    {
        var (unexplained, _) = LitigationModuleParity.OwnershipDifferences([Owned("Vendor Court", "DRT Mumbai 1")]);
        var message = Assert.Single(unexplained);
        Assert.Contains("Court is portal-owned", message);
        Assert.Contains("another writer", message);
    }

    [Fact]
    public void A_difference_while_a_newer_projection_waits_to_be_published_is_explained()
    {
        var (unexplained, explained) = LitigationModuleParity.OwnershipDifferences([Owned("Vendor Court", "DRT Mumbai 1", version: 3, published: 2)]);
        Assert.Empty(unexplained);
        Assert.Contains("waiting to be published", Assert.Single(explained));
    }

    [Fact]
    public void A_never_published_projection_is_pending()
    {
        var (unexplained, explained) = LitigationModuleParity.OwnershipDifferences([Owned("Vendor Court", "DRT Mumbai 1", published: null)]);
        Assert.Empty(unexplained);
        Assert.Single(explained);
    }

    [Fact]
    public void A_vendor_owned_field_is_not_checked()
    {
        var (unexplained, explained) = LitigationModuleParity.OwnershipDifferences([Owned("Vendor Court", "Something Else", source: "Vendor")]);
        Assert.Empty(unexplained);
        Assert.Empty(explained);
    }

    [Fact]
    public void A_missing_projected_value_never_demands_an_empty_column_but_a_portal_confirmed_empty_does()
    {
        Assert.Empty(LitigationModuleParity.OwnershipDifferences([Owned("Vendor Court", null)]).Unexplained);
        Assert.Single(LitigationModuleParity.OwnershipDifferences([Owned("Stale Court", null, confirmedEmpty: true)]).Unexplained);
        Assert.Empty(LitigationModuleParity.OwnershipDifferences([Owned(null, null, confirmedEmpty: true)]).Unexplained);
    }

    [Fact]
    public void Dates_are_compared_in_the_stored_convention()
    {
        var c = new LitigationModuleParity.OwnedCase(7,
            new Dictionary<string, string?> { [LivePortalOwnership.NextHearingDate] = "20-10-2026" },
            new Dictionary<string, string?> { [LiveProjectionEngine.FNextHearing] = "20-10-2026" },
            new Dictionary<string, FieldSource> { [LiveProjectionEngine.FNextHearing] = new("Portal", T0, 1) }, 1, 1);
        Assert.Empty(LitigationModuleParity.OwnershipDifferences([c]).Unexplained);
        var d = c with { ProjectedFields = new Dictionary<string, string?> { [LiveProjectionEngine.FNextHearing] = "20-10-2026" }, LegacyColumns = new Dictionary<string, string?> { [LivePortalOwnership.NextHearingDate] = "2026-10-20" } };
        Assert.Single(LitigationModuleParity.OwnershipDifferences([d]).Unexplained);
    }

    [Fact]
    public async Task After_the_publisher_runs_the_database_check_is_clean_and_a_foreign_edit_is_caught()
    {
        await using var seed = Ctx();
        var client = new Client { ClientCode = "FO" + Guid.NewGuid().ToString("N")[..8], ClientName = "Ownership Test Co", CreatedDate = DateTime.UtcNow };
        seed.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Ownership Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"FO-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        seed.Requests.Add(request);
        await seed.SaveChangesAsync();
        var legacy = new LitigationCase
        {
            RequestId = request.RequestId, CaseNumber = "413", CaseType = "OA", CaseYear = "2022", Court = "Vendor Court Name", CaseStatus = "PENDING",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        seed.LitigationCases.Add(legacy);
        await seed.SaveChangesAsync();
        var moduleCase = await new LitigationModuleStore(seed).EnsureCaseForLegacyAsync(legacy.LitigationCaseId, default);
        await new LiveProjectionStore(seed).PublishAsync(new ObservationInput(moduleCase.CaseId, ObservationSource.DrtPortal, ObservationOutcome.Found, true, T0,
            new ParsedCaseSnapshot("PENDING", null, "DRT Mumbai 1", "5-12-2022", "15-9-2026", "20-10-2026", null, null, null, 6)), default);
        await new RequestLinkStore(seed).UpsertAsync(request.RequestId, moduleCase.CaseId,
            new PartyMatchResult(PartyMatchOutcome.Verified, "exact", "OWNERSHIP TEST COMPANY LIMITED", null), "respondent", false, null, default);

        // Observed and linked but not yet published: the difference is explained, not a violation.
        await using (var db = Ctx())
        {
            var (unexplained, explained) = await new LitigationModuleParity(db).OwnershipDifferencesAsync(request.RequestId, default);
            Assert.Empty(unexplained);
            Assert.NotEmpty(explained);
        }

        await using (var db = Ctx()) await LiveCasePublisher.PublishAsync(db, request.RequestId, legacy.LitigationCaseId, default);
        await using (var db = Ctx())
        {
            var (unexplained, explained) = await new LitigationModuleParity(db).OwnershipDifferencesAsync(request.RequestId, default);
            Assert.Empty(unexplained);
            Assert.Empty(explained);
        }

        // Another writer changes a portal-owned column behind the projector's back.
        await using (var db = Ctx()) await db.LitigationCases.Where(c => c.LitigationCaseId == legacy.LitigationCaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Court, "Edited By Someone"));
        await using (var db = Ctx())
        {
            var (unexplained, _) = await new LitigationModuleParity(db).OwnershipDifferencesAsync(request.RequestId, default);
            Assert.Contains("Court is portal-owned", Assert.Single(unexplained));
        }
    }
}