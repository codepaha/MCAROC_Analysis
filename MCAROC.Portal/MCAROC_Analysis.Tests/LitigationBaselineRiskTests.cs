using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>The baseline R0–R3 tier of a litigation case, worked out from its own court record (forum, case type, act, status, the
/// company's side) by the owner's Case Risk Analysis trigger table for a bank lender. The inputs here are the values the real report
/// for the demo company carried.</summary>
public class LitigationBaselineRiskTests
{
    private static LitigationRiskAssessment Assess(
        string? type = null, string? court = null, string? caseType = null, string? act = null, string? stage = null,
        LitigationCaseStatusBucket status = LitigationCaseStatusBucket.Pending, LitigationCompanySide side = LitigationCompanySide.Unknown,
        string? proceeding = null, string[]? petitioners = null) =>
        LitigationBaselineRisk.Assess(new LitigationRiskInput(type, court, "high_risk_court", caseType, act, stage, null, proceeding, status, side, petitioners ?? []));

    [Theory]
    [InlineData(LitigationCaseStatusBucket.Pending)]
    [InlineData(LitigationCaseStatusBucket.Disposed)]
    [InlineData(LitigationCaseStatusBucket.Unknown)]
    public void A_debt_recovery_tribunal_case_is_critical_at_any_status(LitigationCaseStatusBucket status)
    {
        var a = Assess(type: "drt", court: "Drt", caseType: "Original Application-", status: status);

        Assert.Equal(LitigationRiskTier.R3, a.Tier);
        Assert.Contains("DRT", a.Trigger);
    }

    [Theory]
    [InlineData("C.P.(IB)No.593/KB/2017CA(IB)No.582/KB/2018", null)]
    [InlineData("interlocutory application (i.b.c)-", null)]
    [InlineData("miscellaneous application (ibc)-", null)]
    [InlineData("Original Application-", "7 IBC")]
    [InlineData("SA-", "60 (5) IBC")]
    public void An_insolvency_case_at_the_company_law_tribunal_is_critical_at_any_status(string caseType, string? act)
    {
        foreach (var status in Enum.GetValues<LitigationCaseStatusBucket>())
        {
            var a = Assess(type: "nclt", court: "nclt", caseType: caseType, act: act, status: status);

            Assert.Equal(LitigationRiskTier.R3, a.Tier);
            Assert.Equal("NCLT/NCLAT insolvency proceeding", a.Trigger);
        }
    }

    [Theory]
    [InlineData("transfer petition(companies act)-", null)]
    [InlineData("TP-", "433, 434 & 439 Companies Act")]
    public void A_company_or_winding_up_petition_is_critical(string caseType, string? act) =>
        Assert.Equal(LitigationRiskTier.R3, Assess(type: "nclt", court: "nclt", caseType: caseType, act: act).Tier);

    [Fact]
    public void A_company_law_tribunal_matter_not_stated_as_insolvency_is_high_not_critical()
    {
        var a = Assess(type: "nclt", court: "nclt", caseType: "CA-", act: "621A/441");

        Assert.Equal(LitigationRiskTier.R2, a.Tier);
        Assert.Contains("not stated as insolvency", a.Trigger);
    }

    [Theory]
    [InlineData("itat")]
    [InlineData("cestat")]
    public void A_tax_tribunal_appeal_is_critical(string forum) =>
        Assert.Equal(LitigationRiskTier.R3, Assess(type: forum, caseType: "Appeal").Tier);

    [Fact]
    public void A_cheque_bounce_case_against_the_company_or_with_no_stated_side_is_critical_but_not_when_the_company_is_the_complainant()
    {
        Assert.Equal(LitigationRiskTier.R3, Assess(type: "districtcourt", caseType: "Complaint under Section 138 Negotiable Instruments Act", side: LitigationCompanySide.AgainstCompany).Tier);
        Assert.Equal(LitigationRiskTier.R3, Assess(type: "districtcourt", caseType: "Complaint under Section 138 Negotiable Instruments Act").Tier);
        Assert.Equal(LitigationRiskTier.R1, Assess(type: "districtcourt", caseType: "Complaint under Section 138 Negotiable Instruments Act", side: LitigationCompanySide.ByCompany).Tier);
    }

    [Fact]
    public void A_pending_recovery_or_execution_proceeding_is_critical_but_falls_once_disposed()
    {
        Assert.Equal(LitigationRiskTier.R3, Assess(type: "districtcourt", caseType: "Execution Petition").Tier);
        Assert.Equal(LitigationRiskTier.R3, Assess(type: "districtcourt", caseType: "Money Recovery Suit", status: LitigationCaseStatusBucket.Unknown).Tier);
        Assert.Equal(LitigationRiskTier.R1, Assess(type: "districtcourt", caseType: "Money Recovery Suit", status: LitigationCaseStatusBucket.Disposed).Tier);
    }

    [Fact]
    public void A_criminal_case_is_critical_while_pending_for_the_company_and_high_when_disposed_or_brought_by_it()
    {
        Assert.Equal(LitigationRiskTier.R3, Assess(type: "districtcourt", caseType: "Criminal Misc. Bail Application").Tier);
        Assert.Equal(LitigationRiskTier.R2, Assess(type: "districtcourt", caseType: "Criminal Case", status: LitigationCaseStatusBucket.Disposed).Tier);
        Assert.Equal(LitigationRiskTier.R2, Assess(type: "districtcourt", caseType: "Criminal Case", side: LitigationCompanySide.ByCompany).Tier);
    }

    [Fact]
    public void A_pending_matter_brought_by_a_bank_or_finance_company_is_high()
    {
        var a = Assess(type: "highcourt", caseType: "Civil Suit", petitioners: ["State Bank of India"]);

        Assert.Equal(LitigationRiskTier.R2, a.Tier);
        Assert.Contains("Financial institution", a.Trigger);
    }

    [Fact]
    public void Ordinary_civil_matters_are_low_pending_or_disposed()
    {
        Assert.Equal(LitigationRiskTier.R1, Assess(type: "highcourt", caseType: "Writ Petition").Tier);
        var disposed = Assess(type: "highcourt", caseType: "Writ Petition", status: LitigationCaseStatusBucket.Disposed);
        Assert.Equal(LitigationRiskTier.R1, disposed.Tier);
        Assert.Contains("Disposed", disposed.Trigger);
    }

    [Fact]
    public void The_first_matching_trigger_decides_so_a_debt_tribunal_case_with_a_civil_looking_type_is_still_critical()
    {
        var a = Assess(type: "drt", court: "Debts Recovery Tribunal", caseType: "Writ Petition", status: LitigationCaseStatusBucket.Disposed);

        Assert.Equal(LitigationRiskTier.R3, a.Tier);
    }

    [Theory]
    [InlineData("drt", null, LitigationForum.Drt)]
    [InlineData("nclat", null, LitigationForum.Nclat)]
    [InlineData("nclt", "nclt", LitigationForum.Nclt)]
    [InlineData(null, "Debts Recovery Tribunal, Kolkata", LitigationForum.Drt)]
    [InlineData(null, "Debts Recovery Appellate Tribunal", LitigationForum.Drat)]
    [InlineData(null, "National Company Law Appellate Tribunal", LitigationForum.Nclat)]
    [InlineData(null, "National Company Law Tribunal, Hyderabad Bench", LitigationForum.Nclt)]
    [InlineData(null, "High Court of Delhi", LitigationForum.HighCourt)]
    [InlineData(null, "Supreme Court of India", LitigationForum.SupremeCourt)]
    [InlineData(null, "CMM Courts, Hyderabad", LitigationForum.Other)]
    [InlineData(null, "District and Sessions Judge, New Delhi", LitigationForum.DistrictCourt)]
    [InlineData(null, null, LitigationForum.Other)]
    public void The_forum_is_read_from_the_provider_code_or_the_court_name(string? type, string? court, LitigationForum expected) =>
        Assert.Equal(expected, LitigationBaselineRisk.ForumOf(type, court, null));

    [Fact]
    public void A_report_with_no_cases_is_R0_and_otherwise_takes_its_highest_case_tier()
    {
        Assert.Equal(LitigationRiskTier.R0, new LitigationRiskProfile().Overall);
        Assert.Equal(LitigationRiskTier.R0, LitigationBaselineRisk.PortfolioTier([]));

        var profile = new LitigationRiskProfile();
        profile.Add(Assess(type: "drt"));
        profile.Add(Assess(type: "drt"));
        profile.Add(Assess(type: "highcourt", caseType: "Writ Petition"));
        profile.Add(Assess(type: "nclt", court: "nclt", caseType: "CA-", act: "621A/441"));

        Assert.Equal(LitigationRiskTier.R3, profile.Overall);
        Assert.Equal(4, profile.TotalCases);
        Assert.Equal((2, 1, 1, 0), (profile.Cases[LitigationRiskTier.R3], profile.Cases[LitigationRiskTier.R2], profile.Cases[LitigationRiskTier.R1], profile.Cases[LitigationRiskTier.R0]));
        Assert.Equal(("DRT/DRAT recovery proceeding", 2), Assert.Single(profile.TriggersOf(LitigationRiskTier.R3)));
    }
}

public class LitigationCompanySidesTests
{
    private static readonly string[] Names = ["Coastal Projects Limited", "COASTAL PROJECTS PRIVATE LIMITED", "East Coast Carriers Pvt Ltd"];

    [Theory]
    [InlineData("M/s Coastal Projects Ltd.", "COASTAL PROJECTS")]
    [InlineData("COASTAL PROJECTS LIMITED (IN LIQUIDATION)", "COASTAL PROJECTS")]
    [InlineData("The Coastal Projects Private Limited", "COASTAL PROJECTS")]
    [InlineData("East Coast Carriers (P) Ltd", "EAST COAST CARRIERS P")]
    public void Names_are_reduced_to_their_distinguishing_words(string name, string expectedStart) =>
        Assert.StartsWith(expectedStart, LitigationCompanySides.Core(name));

    [Fact]
    public void The_side_follows_which_list_names_the_company_current_or_earlier_name()
    {
        Assert.Equal(LitigationCompanySide.AgainstCompany,
            LitigationCompanySides.Determine(["State Bank of India"], ["M/s COASTAL PROJECTS LIMITED"], Names));
        Assert.Equal(LitigationCompanySide.ByCompany,
            LitigationCompanySides.Determine(["Coastal Projects Ltd"], ["Karur Vysya Bank Limited"], Names));
        Assert.Equal(LitigationCompanySide.AgainstCompany,
            LitigationCompanySides.Determine(["Some Creditor"], ["East Coast Carriers Pvt Ltd"], Names)); // an earlier name
        Assert.Equal(LitigationCompanySide.Both,
            LitigationCompanySides.Determine(["Coastal Projects Limited"], ["Coastal Projects Limited"], Names));
        Assert.Equal(LitigationCompanySide.Unknown,
            LitigationCompanySides.Determine(["Alpha Traders"], ["Beta Exports"], Names));
    }

    [Fact]
    public void A_different_company_that_merely_shares_a_word_is_not_the_company()
    {
        Assert.Equal(LitigationCompanySide.Unknown,
            LitigationCompanySides.Determine(["Coastal Shipping Limited"], ["Projects Coastal Services"], Names));
        // A one-word name must match whole, never as the start of a longer one.
        Assert.Equal(LitigationCompanySide.Unknown,
            LitigationCompanySides.Determine(["Coastline Finance"], ["Coastal Holdings"], ["Coastal Limited"]));
    }

    [Fact]
    public void With_no_company_names_the_side_is_unknown() =>
        Assert.Equal(LitigationCompanySide.Unknown, LitigationCompanySides.Determine(["A"], ["B"], []));
}

public sealed class LitigationCasePageServiceTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Seeded(McaRequest Request, long DrtCaseId, long HighCourtCaseId, long OtherRequestCaseId);

    private static async Task<Seeded> SeedAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "PG" + Guid.NewGuid().ToString("N")[..8], ClientName = "Case Page Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Coastal Projects Limited", RequestNumber = $"CP-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        var other = new McaRequest { Client = client, CompanyName = "Other Co", RequestNumber = $"CP-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(other);
        await db.SaveChangesAsync();

        async Task<LitigationReportSnapshot> Report(McaRequest r, string hash)
        {
            var job = new LitigationSearchJob { RequestId = r.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = hash };
            db.LitigationSearchJobs.Add(job);
            await db.SaveChangesAsync();
            var snapshot = new LitigationReportSnapshot
            {
                LitigationSearchJobId = job.LitigationSearchJobId, RequestId = r.RequestId, ReportHash = hash,
                Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = new DateTime(2026, 10, 2, 13, 54, 0, DateTimeKind.Utc)
            };
            db.LitigationReportSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
            return snapshot;
        }
        async Task<LitigationCase> Case(McaRequest r, LitigationReportSnapshot s, LitigationCase c)
        {
            c.RequestId = r.RequestId;
            c.FirstSeenUtc = c.LastSeenUtc = DateTime.UtcNow;
            db.LitigationCases.Add(c);
            await db.SaveChangesAsync();
            db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = s.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return c;
        }

        var snapshot = await Report(request, "page-hash-" + Guid.NewGuid().ToString("N"));
        var drt = await Case(request, snapshot, new LitigationCase
        {
            Type = "drt", Court = "Drt", CourtCategory = "high_risk_court", CaseNumber = "OA/12/2021", CaseType = "Original Application-", CaseStatus = "PENDING",
            FilingDate = "2021-04-26", PetitionersJson = "[\"Karur Vysya Bank Limited\"]", RespondentsJson = "[\"Coastal Projects Limited\",\"A. Guarantor\"]",
            Orders = [new LitigationCaseOrder { OrderDate = "2024-01-10", OrderType = "Order" }, new LitigationCaseOrder { OrderDate = "2022-06-01", OrderType = "Notice" }]
        });
        var hc = await Case(request, snapshot, new LitigationCase
        {
            Type = "highcourt", Court = "High Court of Delhi", CourtCategory = "high_risk_court", CaseNumber = "WP/9/2020", CaseType = "Writ Petition", CaseStatus = "Disposed",
            PetitionersJson = "[\"Karur Vysya Bank Limited\"]", RespondentsJson = "[\"Someone Else\"]"
        });
        var otherSnapshot = await Report(other, "page-hash-" + Guid.NewGuid().ToString("N"));
        var otherCase = await Case(other, otherSnapshot, new LitigationCase { Type = "drt", Court = "Drt", CaseNumber = "OA/1/2019", CaseStatus = "PENDING" });
        return new Seeded(request, drt.LitigationCaseId, hc.LitigationCaseId, otherCase.LitigationCaseId);
    }

    [Fact]
    public async Task A_case_of_the_current_report_gets_its_page_with_age_tier_side_orders_and_the_cases_sharing_a_party()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);

        var page = await new LitigationCasePageService(db).GetAsync(s.Request.RequestId, s.DrtCaseId, CancellationToken.None);

        Assert.NotNull(page);
        var card = page!.Card;
        Assert.Equal("OA/12/2021", card.CaseNumber);
        Assert.Equal((LitigationRiskTier.R3, LitigationCompanySide.AgainstCompany), (card.Risk!.Tier, card.CompanySide));
        Assert.Equal("Pending for 5 years 5 months", card.Age!.Headline); // measured to the data retrieval date, 2 Oct 2026 in IST
        Assert.Equal(["2024-01-10", "2022-06-01"], card.Orders.Select(o => o.OrderDate)); // newest first
        // The bank is a party to the other case of this report too — not the company, and never the other request's case.
        var related = Assert.Single(page.RelatedCases);
        Assert.Equal((s.HighCourtCaseId, "Karur Vysya Bank Limited", LitigationRiskTier.R1), (related.LitigationCaseId, related.SharedParty, related.Tier));
    }

    [Fact]
    public async Task A_case_of_another_request_or_one_that_does_not_exist_is_not_found()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var service = new LitigationCasePageService(db);

        Assert.Null(await service.GetAsync(s.Request.RequestId, s.OtherRequestCaseId, CancellationToken.None)); // not in this request's report
        Assert.Null(await service.GetAsync(s.Request.RequestId, -42, CancellationToken.None));
        Assert.Null(await service.GetAsync(-42, s.DrtCaseId, CancellationToken.None));
    }

    [Fact]
    public async Task A_request_without_a_completed_report_has_no_case_pages()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var bare = new McaRequest { ClientId = s.Request.ClientId, CompanyName = "No Report Co", RequestNumber = $"CP-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(bare);
        await db.SaveChangesAsync();

        Assert.Null(await new LitigationCasePageService(db).GetAsync(bare.RequestId, s.DrtCaseId, CancellationToken.None));
    }

    [Fact]
    public async Task The_case_page_route_returns_the_page_for_a_case_of_the_report_and_not_found_otherwise()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var controller = new MCAROC_Analysis.Controllers.LitigationCaseController(new LitigationCasePageService(db));

        var ok = await controller.Detail(s.Request.RequestId, s.DrtCaseId, CancellationToken.None);
        var model = Assert.IsType<LitigationCasePage>(Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(ok).Model);
        Assert.Equal(s.DrtCaseId, model.Card.LitigationCaseId);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Detail(s.Request.RequestId, s.OtherRequestCaseId, CancellationToken.None));
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Detail(s.Request.RequestId, -1, CancellationToken.None));
    }
}
