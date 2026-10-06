using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.RequestDetails;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public class LitigationRegisterUiTests
{
    private static readonly DateTime Retrieved = new(2026, 10, 5, 5, 41, 0, DateTimeKind.Utc); // 5 Oct 2026, 11:11 IST

    private static LitigationCase Case(string number, string type, string? category, string status, string? direction, string? filed, string? decided = null) => new()
    {
        CaseNumber = number, Type = type, CourtCategory = category, Court = type, CaseStatus = status, Direction = direction,
        FilingDate = filed, DecisionDate = decided, LastSeenUtc = DateTime.UtcNow,
        PetitionersJson = "[\"Someone\"]", RespondentsJson = "[\"Someone Else\"]"
    };

    [Fact]
    public async Task The_register_lists_pending_then_disposed_then_unknown_by_forum_against_first_and_newest_filing_first()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("LitigationRegisterOrder");
        AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options);
        await using var seed = Db();
        var client = new Client { ClientCode = "RO", ClientName = "Order Test", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, CompanyName = "Target Company", RequestNumber = "UI-ORDER" };
        seed.Requests.Add(request); await seed.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "h" };
        seed.LitigationSearchJobs.Add(job); await seed.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { RequestId = request.RequestId, LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "h", RetrievedUtc = Retrieved, Status = LitigationReportSnapshotStatus.Completed };
        seed.LitigationReportSnapshots.Add(snapshot); await seed.SaveChangesAsync();

        // Inserted in a deliberately scrambled order; ids therefore do not match the expected order.
        var scrambled = new[]
        {
            Case("F-itat-unknown", "itat", "tribunal_cases", "Fixed", null, "05-05-2018"),
            Case("E-district-disposed", "district", "district_court", "Disposed", "against", "01-01-2020", "01-06-2021"),
            Case("D-drt-pending", "drt", "high_risk_court", "Pending", "against", "01-01-2022"),
            Case("B-hc-older", "highcourt", "high_court", "Pending", "against", "01-01-2023"),
            Case("G-hc-by", "highcourt", "high_court", "Pending", "by", "01-01-2026"),
            Case("C-hc-newer", "highcourt", "high_court", "Pending", "against", "01-01-2025"),
            Case("A-sc-by", "supreme", "supreme_court", "Pending", "by", "01-01-2024"),
        };
        foreach (var c in scrambled) c.RequestId = request.RequestId;
        seed.LitigationCases.AddRange(scrambled); await seed.SaveChangesAsync();
        seed.LitigationCaseSourceReports.AddRange(scrambled.Select(c => new LitigationCaseSourceReport
            { LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, LitigationCaseId = c.LitigationCaseId }));
        await seed.SaveChangesAsync();

        await using var read = Db();
        var query = new LitigationCaseListQuery(read);
        var all = (await query.BuildAsync(request.RequestId, null, null, 1, 25, null, null, null, default))!;

        // pending: Supreme Court; High Court against (newest filing first) then by; DRT; then disposed; then unknown
        Assert.Equal(["A-sc-by", "C-hc-newer", "B-hc-older", "G-hc-by", "D-drt-pending", "E-district-disposed", "F-itat-unknown"],
            all.Cases.Select(c => c.CaseNumber).ToArray());

        Assert.Equal(new LitigationStatusCounts(Pending: 5, Disposed: 1, Unknown: 1), all.StatusCounts);
        Assert.Equal(7, all.StatusCounts.All);

        Assert.Equal(LitigationInstitution.SupremeCourt, all.Cases[0].Institution);
        Assert.Equal(LitigationInstitution.HighCourt, all.Cases[1].Institution);
        Assert.Equal(LitigationInstitution.Drt, all.Cases[4].Institution);
        Assert.Equal(LitigationInstitution.DistrictCourt, all.Cases[5].Institution);
        Assert.Equal(LitigationInstitution.Itat, all.Cases[6].Institution);

        // ages are measured to the day the data was retrieved, not today
        Assert.Equal("Pending for 2 years 9 months", all.Cases[0].Age!.Headline);
        Assert.Equal("Ran for 1 year 5 months", all.Cases[5].Age!.Headline);

        // a status filter narrows the list but every segment keeps showing what choosing it would list
        var disposed = (await query.BuildAsync(request.RequestId, null, "Disposed", 1, 25, null, null, null, default))!;
        Assert.Equal("E-district-disposed", Assert.Single(disposed.Cases).CaseNumber);
        Assert.Equal(1, disposed.Total);
        Assert.Equal(new LitigationStatusCounts(5, 1, 1), disposed.StatusCounts);

        // counts follow the other filters
        var searched = (await query.BuildAsync(request.RequestId, null, "Disposed", 1, 25, "hc-", null, null, default))!;
        Assert.Equal(new LitigationStatusCounts(3, 0, 0), searched.StatusCounts);

        // a deep link lands on the page that holds the case in this order
        var focused = (await query.BuildAsync(request.RequestId, null, null, 1, 25, null, null, scrambled[0].LitigationCaseId, default))!;
        Assert.Contains(focused.Cases, c => c.CaseNumber == "F-itat-unknown");
    }

    private static PartialViewResult AsPartial(LitigationCaseListViewModel model) => new()
    {
        ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model }
    };

    [Fact]
    public async Task The_register_renders_status_segments_with_counts_and_each_row_shows_its_forum_and_age()
    {
        var model = new LitigationCaseListViewModel
        {
            RequestId = 7, Status = "Pending", Total = 1, PageSize = 25, Search = "a & b",
            StatusCounts = new LitigationStatusCounts(Pending: 5, Disposed: 1_200, Unknown: 2),
            Cases =
            [
                new LitigationCaseCardViewModel
                {
                    LitigationCaseId = 1, CaseNumber = "CS 1/2020", Court = "BombayHighCourt", Institution = LitigationInstitution.HighCourt,
                    StatusBucket = LitigationCaseStatusBucket.Pending, CompanySide = LitigationCompanySide.AgainstCompany,
                    Age = LitigationCaseAges.Compute("01-01-2024", null, null, LitigationCaseStatusBucket.Pending, [], new DateOnly(2026, 10, 5))
                }
            ]
        };

        var html = await LitigationTabRenderingTests.RenderCaseListAsync(AsPartial(model));

        Assert.Contains("aria-label=\"Filter cases by status\"", html);
        Assert.Contains("All <span", html);
        Assert.Contains(">1,207<", html); // All = 5 + 1,200 + 2
        Assert.Contains(">1,200<", html);
        Assert.Contains("Status not reported", html);
        // the active segment is marked, the others link to page 1 with only the status changed and the search kept
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "aria-current=\"true\""), m => m.Index > html.IndexOf("litigation-segments", StringComparison.Ordinal)
            && m.Index < html.IndexOf("litigation-list-caption", StringComparison.Ordinal));
        Assert.Contains("status=Disposed", html);
        Assert.Contains("q=a%20%26%20b", html);
        Assert.DoesNotContain("<select id=\"litigation-status\"", html); // replaced by the segments
        Assert.Contains("name=\"status\" value=\"Pending\"", html); // the form keeps the chosen status when searching again

        Assert.Contains("High Court", html);
        Assert.Contains("BombayHighCourt", html);
        Assert.Contains("Pending for 2 years 9 months", html);
    }

    [Fact]
    public void The_forum_summary_counts_each_case_once_by_forum_side_and_status()
    {
        var summary = new LitigationForumSummary();
        summary.Add(LitigationInstitution.HighCourt, LitigationCompanySide.AgainstCompany, LitigationCaseStatusBucket.Pending);
        summary.Add(LitigationInstitution.HighCourt, LitigationCompanySide.AgainstCompany, LitigationCaseStatusBucket.Disposed);
        summary.Add(LitigationInstitution.HighCourt, LitigationCompanySide.ByCompany, LitigationCaseStatusBucket.Unknown);
        summary.Add(LitigationInstitution.Drt, LitigationCompanySide.AgainstCompany, LitigationCaseStatusBucket.Pending);

        Assert.Equal(new LitigationForumSummary.Counts(1, 1, 0), summary.Cell(LitigationInstitution.HighCourt, LitigationForumSummary.SideColumn.Against));
        Assert.Equal(3, summary.ForForum(LitigationInstitution.HighCourt).Total);
        Assert.Equal(new LitigationForumSummary.Counts(2, 1, 0), summary.ForSide(LitigationForumSummary.SideColumn.Against));
        Assert.Equal(4, summary.Total.Total);
        Assert.Equal(0, summary.ForForum(LitigationInstitution.Nclat).Total); // a forum with nothing found is still a row of zeros
        Assert.False(summary.HasNotDetermined);

        // a company on both sides, or on neither, is "not determined"
        summary.Add(LitigationInstitution.Itat, LitigationCompanySide.Both, LitigationCaseStatusBucket.Pending);
        summary.Add(LitigationInstitution.Itat, LitigationCompanySide.Unknown, LitigationCaseStatusBucket.Disposed);
        Assert.True(summary.HasNotDetermined);
        Assert.Equal(new LitigationForumSummary.Counts(1, 1, 0), summary.Cell(LitigationInstitution.Itat, LitigationForumSummary.SideColumn.NotDetermined));
        Assert.Equal(6, summary.Total.Total);
    }
}
