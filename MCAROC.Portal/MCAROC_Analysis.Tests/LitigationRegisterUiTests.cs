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

    [Fact]
    public async Task The_register_order_is_the_reports_order_for_cases_with_no_vendor_direction_or_type_across_a_page_boundary()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("LitigationRegisterExact");
        AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options);
        await using var seed = Db();
        var client = new Client { ClientCode = "RX", ClientName = "Exact Order", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, CompanyName = "Target Company", RequestNumber = "UI-EXACT" };
        seed.Requests.Add(request); await seed.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "h" };
        seed.LitigationSearchJobs.Add(job); await seed.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { RequestId = request.RequestId, LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "h", RetrievedUtc = Retrieved, Status = LitigationReportSnapshotStatus.Completed };
        seed.LitigationReportSnapshots.Add(snapshot); await seed.SaveChangesAsync();

        LitigationCase Mk(string number, string? type, string? category, string court, string? direction, int day, string petitioner, string respondent)
        {
            var c = Case(number, type!, category, "Pending", direction, $"{day:D2}-01-2024");
            c.Type = type; c.Court = court;
            c.PetitionersJson = $"[\"{petitioner}\"]"; c.RespondentsJson = $"[\"{respondent}\"]";
            c.RequestId = request.RequestId;
            return c;
        }

        var cases = new List<LitigationCase>();
        // The forum is found from the court name alone (no type, no category).
        cases.Add(Mk("SC-by-name", null, null, "Supreme Court of India", null, 1, "Someone", "Target Company"));
        // High Court: the vendor says "by" for ten, says nothing for twenty whose party names put the company as respondent (against).
        for (var i = 1; i <= 10; i++) cases.Add(Mk($"HC-by-{i:D2}", "highcourt", "high_court", "Delhi High Court", "by", i, "Target Company", "Other"));
        for (var i = 1; i <= 20; i++) cases.Add(Mk($"HC-blank-{i:D2}", "highcourt", "high_court", "Delhi High Court", null, i, "Other", "Target Company"));
        // A debts recovery tribunal known only by its court name, and two unplaceable cases.
        for (var i = 1; i <= 6; i++) cases.Add(Mk($"DRT-name-{i}", null, null, "Debts Recovery Tribunal, Kolkata", null, i, "Target Company", "Bank"));
        for (var i = 1; i <= 2; i++) cases.Add(Mk($"OTHER-{i}", "others", null, "Unspecified Court", "against", i, "X", "Y"));
        cases.Reverse(); // inserted in the opposite order, so ids do not give the answer
        seed.LitigationCases.AddRange(cases); await seed.SaveChangesAsync();
        seed.LitigationCaseSourceReports.AddRange(cases.Select(c => new LitigationCaseSourceReport
            { LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, LitigationCaseId = c.LitigationCaseId }));
        await seed.SaveChangesAsync();

        await using var read = Db();
        var query = new LitigationCaseListQuery(read);
        var page1 = (await query.BuildAsync(request.RequestId, null, null, 1, 25, null, null, null, default))!;
        var page2 = (await query.BuildAsync(request.RequestId, null, null, 2, 25, null, null, null, default))!;
        Assert.Equal(39, page1.Total);
        var register = page1.Cases.Concat(page2.Cases).ToList();
        Assert.Equal(39, register.Count);

        // Expected, from the rules: pending Supreme Court; then High Court cases against the company (the twenty with no vendor
        // direction, newest filing first), then the ones it brought; then DRT; then Others.
        string[] expected =
        [
            "SC-by-name",
            .. Enumerable.Range(1, 20).Reverse().Select(i => $"HC-blank-{i:D2}"),
            .. Enumerable.Range(1, 10).Reverse().Select(i => $"HC-by-{i:D2}"),
            .. Enumerable.Range(1, 6).Reverse().Select(i => $"DRT-name-{i}"),
            "OTHER-2", "OTHER-1" // direction "against" for both; newest filing first
        ];
        Assert.Equal(expected, register.Select(c => c.CaseNumber).ToArray());

        // The page boundary falls inside the "by" group: the first page ends with its four newest, the second starts with the rest.
        Assert.Equal("HC-by-10", page1.Cases[21].CaseNumber);
        Assert.Equal("HC-by-07", page1.Cases[^1].CaseNumber);
        Assert.Equal("HC-by-06", page2.Cases[0].CaseNumber);

        // What each row displays is what it was ordered by.
        Assert.Equal(LitigationInstitution.SupremeCourt, register[0].Institution);
        Assert.All(register.Where(c => c.CaseNumber!.StartsWith("HC-blank")), c => Assert.Equal(LitigationCompanySide.AgainstCompany, c.CompanySide));
        Assert.All(register.Where(c => c.CaseNumber!.StartsWith("DRT-name")), c => Assert.Equal(LitigationInstitution.Drt, c.Institution));

        // And it is the report's order: the same cases, put through the report's own view builder.
        var dtos = cases.Select(c => new StandaloneReportCaseDto(c.LitigationCaseId, null, null, c.Cnr, c.CourtCategory, c.Direction, null, c.Type,
            c.Court, null, c.CaseNumber, c.CaseType, c.CaseYear, c.CaseStage, c.CaseStatus, null, c.FilingDate, null, null, c.DecisionDate, null, null,
            c.PetitionersJson, c.RespondentsJson, null, null, [])).ToList();
        var report = new StandaloneLitigationReport("A", "Target Company", new DateTimeOffset(Retrieved), snapshot.LitigationReportSnapshotId, Retrieved,
            false, ["Target Company"], new LitigationCourtSummaryGrid(), dtos);
        Assert.Equal(LitigationReportArtifacts.BuildCaseViews(report).Select(v => v.Item.LitigationCaseId), register.Select(c => c.LitigationCaseId));

        // a deep link to a case on the second page lands on that page
        var deepLink = (await query.BuildAsync(request.RequestId, null, null, 1, 25, null, null, page2.Cases[3].LitigationCaseId, default))!;
        Assert.Equal(2, deepLink.Page);
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

        // a company on both sides is a known result with its own column; only a side that could not be told is "not determined"
        summary.Add(LitigationInstitution.Itat, LitigationCompanySide.Both, LitigationCaseStatusBucket.Pending);
        Assert.True(summary.HasBoth);
        Assert.False(summary.HasNotDetermined);
        summary.Add(LitigationInstitution.Itat, LitigationCompanySide.Unknown, LitigationCaseStatusBucket.Disposed);
        Assert.True(summary.HasNotDetermined);
        Assert.Equal(new LitigationForumSummary.Counts(1, 0, 0), summary.Cell(LitigationInstitution.Itat, LitigationForumSummary.SideColumn.Both));
        Assert.Equal(new LitigationForumSummary.Counts(0, 1, 0), summary.Cell(LitigationInstitution.Itat, LitigationForumSummary.SideColumn.NotDetermined));
        Assert.Equal(2, summary.ForForum(LitigationInstitution.Itat).Total);
        Assert.Equal(6, summary.Total.Total);
    }
}
