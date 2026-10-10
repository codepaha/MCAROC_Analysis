using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CaseNumberBackfillTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static CaseBackfillRow Row(long id, string? number, string? type = null, string? year = null, string? court = "DRT Mumbai 1", string? cnr = null, long request = 1) =>
        new(id, request, cnr, null, court, number, type, year, null);

    [Fact]
    public void It_counts_complete_partial_and_unresolved_and_lists_only_the_incomplete()
    {
        var r = CaseNumberBackfill.Analyse([
            Row(1, "413", "OA", "2022"), Row(2, "88", "CONTEMPT PETITION", "2014"), Row(3, "RESTORATION OF SUITS 0-9 R-4", "EXA"), Row(4, "")]);
        Assert.Equal((4, 1, 1, 2), (r.Total, r.Complete, r.Partial, r.Unresolved));
        Assert.Equal([2L, 3L, 4L], r.Issues.Select(i => i.LitigationCaseId));
        Assert.Equal("CONTEMPT PETITION/88/2014", r.Issues[0].Display);
    }

    [Fact]
    public void The_same_number_in_the_same_court_stored_twice_is_a_possible_duplicate()
    {
        var r = CaseNumberBackfill.Analyse([Row(1, "413", "OA", "2022"), Row(2, "OA/000413/2022"), Row(3, "414", "OA", "2022")]);
        var g = Assert.Single(r.PossibleDuplicates);
        Assert.Equal("OA/413/2022", g.Number);
        Assert.Equal([1L, 2L], g.LitigationCaseIds);
    }

    [Fact]
    public void The_same_number_in_another_court_or_request_is_not_a_duplicate()
    {
        Assert.Empty(CaseNumberBackfill.Analyse([Row(1, "413", "OA", "2022", court: "DRT Mumbai 1"), Row(2, "413", "OA", "2022", court: "DRT Delhi 2")]).PossibleDuplicates);
        Assert.Empty(CaseNumberBackfill.Analyse([Row(1, "413", "OA", "2022", request: 1), Row(2, "413", "OA", "2022", request: 2)]).PossibleDuplicates);
    }

    [Fact]
    public void Two_rows_with_different_cnrs_are_different_cases_not_suspected_duplicates() =>
        Assert.Empty(CaseNumberBackfill.Analyse([Row(1, "413", "OA", "2022", cnr: "MHDR010000012022"), Row(2, "413", "OA", "2022", cnr: "MHDR010000022022")]).PossibleDuplicates);

    [Fact]
    public void Malformed_cnrs_do_not_hide_a_duplicate_candidate() =>
        Assert.Single(CaseNumberBackfill.Analyse([Row(1, "413", "OA", "2022", cnr: "legacy-1"), Row(2, "413", "OA", "2022", cnr: "legacy-2")]).PossibleDuplicates);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NA")]
    public void Rows_with_no_court_are_not_grouped_as_same_court(string? court) =>
        Assert.Empty(CaseNumberBackfill.Analyse([Row(1, "413", "OA", "2022", court: court), Row(2, "413", "OA", "2022", court: court)]).PossibleDuplicates);

    [Fact]
    public void Two_spellings_of_one_court_are_still_the_same_court()
    {
        var r = CaseNumberBackfill.Analyse([Row(1, "5", "CRM-M", "2020", court: "PunjabHighCourt"), Row(2, "5", "CRM-M", "2020", court: "High Court of Punjab and Haryana")]);
        Assert.Single(r.PossibleDuplicates);
    }

    [Fact]
    public void A_glued_number_is_never_suspected_as_a_duplicate() =>
        Assert.Empty(CaseNumberBackfill.Analyse([Row(1, "TPNo.93/CTB/2019CP/1317/2018"), Row(2, "TPNo.93/CTB/2019CP/1317/2018")]).PossibleDuplicates);

    [Fact]
    public void The_report_is_the_same_whatever_order_the_rows_arrive_in()
    {
        var rows = new[] { Row(3, "88", "CONTEMPT PETITION", "2014"), Row(1, "413", "OA", "2022"), Row(2, "413", "OA", "2022") };
        Assert.Equal(CaseNumberBackfill.Analyse(rows).ToText(), CaseNumberBackfill.Analyse(rows.Reverse()).ToText());
    }

    [Fact]
    public async Task Running_it_against_the_database_reads_and_writes_nothing_and_is_repeatable()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "BF" + Guid.NewGuid().ToString("N")[..8], ClientName = "Backfill Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Backfill Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"BF-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        db.LitigationCases.Add(new LitigationCase { RequestId = request.RequestId, CaseNumber = "413", CaseType = "OA", CaseYear = "2022", Court = "DRT Mumbai 1", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow });
        db.LitigationCases.Add(new LitigationCase { RequestId = request.RequestId, CaseNumber = "88", CaseType = "SERVICE TAX", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var first = await CaseNumberBackfill.RunAsync(db, request.RequestId, CancellationToken.None);
        var second = await CaseNumberBackfill.RunAsync(db, request.RequestId, CancellationToken.None);

        Assert.Equal((2, 1, 1, 0), (first.Total, first.Complete, first.Partial, first.Unresolved));
        Assert.Equal(first.ToText(), second.ToText());
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(2, await db.LitigationCases.CountAsync(c => c.RequestId == request.RequestId));
    }
}
