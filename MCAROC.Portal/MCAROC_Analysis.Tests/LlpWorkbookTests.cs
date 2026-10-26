using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>#539 PR 1: an LLP's export ingests. Its profile sheet is "About the LLP" (LLPIN, LLP Status, contribution rows) and its Highlights
/// lists business activities in two columns; on 2026-10-25 every LLP request failed at "Required sheet 'About the Company' was not found". The
/// sheets below carry the real export's labels and layout (Nature Earth Kharkhoda LLP, the #514 round), with made-up values.</summary>
public class LlpWorkbookTests : IAsyncLifetime
{
    private const string Llpin = "ZZZ-1234";

    private static SheetData AboutTheLlp(string llpin = Llpin) => Sheet("About the LLP",
        Row("Legal Name", "SAMPLE FARMS LLP"),
        Row("LLPIN", llpin),
        Row("PAN", "AAAFZ0000Z"),
        Row("Printed at", "25 Oct, 2026 18:23 Hours"),
        Row("Probed at", "19 Oct, 2026 14:24 Hours"),
        Row("MCA Master Data updated at", "16 Oct, 2026 19:23 Hours"),
        Row("Documents collected from MCA at", "17 Oct, 2026 02:17 Hours"),
        Row("", ""),
        Row("Total Obligation of Contribution (Crore)", 2.0),
        Row("Total Contribution Received (Crore)", 2.0),
        Row("Sum of Charges (Crore)", 0.28),
        Row("LLP Status", "Active"),
        Row("", ""),
        Row("REGISTERED ADDRESS:", ""),
        Row("Address Line 1", "1 SAMPLE ROAD"),
        Row("Address Line 2", "SAMPLE NAGAR"),
        Row("City", "New Delhi"),
        Row("State", "Delhi"),
        Row("Pin Code", 110001.0),
        Row("", ""),
        Row("Website", "https://sample.example/"),
        Row("Email", "one@sample.example"),
        Row("", "two@sample.example"),
        Row("Phone", "+91-9000000001"),
        Row("", "+91-9000000002"),
        Row("", ""),
        Row("About the LLP", "Sample Farms LLP grows samples."),
        Row("Type of Entity", "Limited Liability Partnership"),
        Row("Date of Incorporation", "14 Dec, 2023"),
        Row("FYED for which Annual Report Filed", "31 Mar, 2026"),
        Row("", ""),
        Row("BROAD INDUSTRY CATEGORY", 1.0),
        Row("Industry", "Real Estate"),
        Row("Segment(s)", "Builders and Developers"));

    private static SheetData Highlights() => Sheet("Highlights",
        Row("PRINCIPAL BUSINESS ACTIVITIES - 31 Mar, 2026", ""),
        Row("Business Classification", "Description of Business Activity"),
        Row("Business", 43.0),
        Row("NAME HISTORY", ""),
        Row("Name", "Till Date"),
        Row("This corporate has not had any name change", ""));

    private static SheetData Directors() => Sheet("Directors",
        Row("NAME", "DIN", "PRESENT DESIGNATION", "PRESENT DESIGNATION APPOINTMENT DATE", "ORIGINAL APPOINTMENT DATE", "DATE OF CESSATION", "FLAGS"),
        Row("SAMPLE PARTNER", 1234567.0, "Designated Partner", "14 Dec, 2023", "-", "-", "-"));

    [Fact]
    public void The_LLP_profile_sheet_is_read_with_its_LLPIN_status_narrative_and_every_phone()
    {
        var result = CompanyProfileParser.Parse(AboutTheLlp(), 1, 1, null, out var emails, out var printedAt);

        Assert.Empty(result.Errors);
        var p = Assert.Single(result.Items);
        Assert.Equal(("SAMPLE FARMS LLP", null, Llpin, "AAAFZ0000Z", "Active"), (p.CompanyName, p.Cin, p.Llpin, p.Pan, p.CompanyStatus));
        Assert.Equal("Sample Farms LLP grows samples.", p.NarrativeDescription);
        Assert.Equal(("Limited Liability Partnership", new DateOnly(2023, 12, 14), 0.28m), (p.EntityType, p.IncorporationDate, p.McaSumOfChargesCrore));
        Assert.Equal("+91-9000000001, +91-9000000002", p.Phone);
        Assert.Equal("1 SAMPLE ROAD, SAMPLE NAGAR, New Delhi, Delhi, 110001", p.RegisteredAddress);
        Assert.Equal(["one@sample.example", "two@sample.example"], emails.Select(e => e.EmailAddress));
        Assert.Equal(new DateTime(2026, 10, 25, 18, 23, 0), printedAt);
    }

    [Fact]
    public void A_profile_with_neither_CIN_nor_LLPIN_is_an_error_naming_its_own_sheet()
    {
        var result = CompanyProfileParser.Parse(AboutTheLlp(llpin: ""), 1, 1, null);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Legal name, or the CIN or LLPIN, missing from the About the LLP sheet", error.Message);
    }

    [Fact]
    public void A_company_profile_keeps_a_second_phone_number_too()
    {
        var sheet = Sheet("About the Company", Row("Legal Name", "SAMPLE LIMITED"), Row("CIN", "U00000DL2000PLC000000"),
            Row("Phone", "+91-11-1111111"), Row("", "+91-11-2222222"), Row("", ""), Row("Website", "https://sample.example/"));
        var p = Assert.Single(CompanyProfileParser.Parse(sheet, 1, 1, null).Items);
        Assert.Equal(("+91-11-1111111, +91-11-2222222", "https://sample.example/"), (p.Phone, p.Website));
    }

    /// <summary>Review of #540: only a NIC code's shape (2 to 5 digits) is a code; a short wording is the description.</summary>
    [Theory]
    [InlineData(43.0, "43", null)]
    [InlineData("4100", "4100", null)]
    [InlineData("45200", "45200", null)]
    [InlineData("Services", null, "Services")]
    [InlineData("Retail", null, "Retail")]
    [InlineData("A1", null, "A1")]
    [InlineData("123456", null, "123456")]
    [InlineData("Construction of buildings", null, "Construction of buildings")]
    public void An_LLP_activity_is_a_code_only_when_it_has_a_NIC_codes_shape(object value, string? code, string? description)
    {
        var sheet = Sheet("Highlights", Row("PRINCIPAL BUSINESS ACTIVITIES - 31 Mar, 2026", ""),
            Row("Business Classification", "Description of Business Activity"), Row("Business", value));
        var a = Assert.Single(HighlightsParser.ParsePrincipalBusinessActivities(sheet, 1, 1, null).Items);
        Assert.Equal((code, description), (a.BusinessActivityCode, a.BusinessActivityDescription));
    }

    [Fact]
    public void The_LLP_business_activity_block_is_read_in_its_two_columns()
    {
        var result = HighlightsParser.ParsePrincipalBusinessActivities(Highlights(), 1, 1, null);
        var a = Assert.Single(result.Items);
        Assert.Equal(("Business", "43", (string?)null, (string?)null, new DateOnly(2026, 3, 31)),
            (a.MainActivityGroupDescription, a.BusinessActivityCode, a.BusinessActivityDescription, a.MainActivityGroupCode, a.AsOnDate));
    }

    // ---- the whole ingestion ------------------------------------------------------------------------------------------------------------------

    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(McaRequest Request, RequestDocument Roc, RequestDocument Charge)> SeedAsync(AppDbContext db, string requestLlpin)
    {
        var client = new Client { ClientCode = $"LLP{Guid.NewGuid():N}"[..10], ClientName = "LLP test", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.LLP, CompanyName = "SAMPLE FARMS LLP", Llpin = requestLlpin,
            RequestNumber = $"LLP-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        var roc = new RequestDocument { Request = request, DocumentType = DocumentType.McaRocReport, OriginalFileName = "llp.xls", StoredFileName = "1.xls", StoragePath = $@"C:\fake\{Guid.NewGuid():N}-roc.xls", FileHash = "a", UploadedDate = DateTime.UtcNow };
        var charge = new RequestDocument { Request = request, DocumentType = DocumentType.ChargeReport, OriginalFileName = "llp-charge.xls", StoredFileName = "2.xls", StoragePath = $@"C:\fake\{Guid.NewGuid():N}-charge.xls", FileHash = "b", UploadedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        db.RequestDocuments.AddRange(roc, charge);
        await db.SaveChangesAsync();
        return (request, roc, charge);
    }

    [Fact]
    public async Task An_LLP_export_ingests_with_its_profile_partners_and_business_activity()
    {
        await using var db = Ctx();
        var (request, roc, charge) = await SeedAsync(db, Llpin);
        var reader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [roc.StoragePath] = [AboutTheLlp(), Directors(), Highlights()],
            [charge.StoragePath] = [AboutTheLlp()],
        });

        var run = await new IngestionOrchestrator(db, reader, NullLogger<IngestionOrchestrator>.Instance).RunAsync(request.RequestId, roc.DocumentId, charge.DocumentId);

        Assert.NotEqual(IngestionRunStatus.Failed, run.Status);
        await using var verify = Ctx();
        var profile = await verify.CompanyProfiles.SingleAsync(p => p.IngestionRunId == run.IngestionRunId);
        Assert.Equal((Llpin, "Active"), (profile.Llpin, profile.CompanyStatus));
        Assert.Equal("Designated Partner", (await verify.Directors.SingleAsync(d => d.IngestionRunId == run.IngestionRunId)).Designation);
        Assert.Equal("43", (await verify.PrincipalBusinessActivities.SingleAsync(a => a.IngestionRunId == run.IngestionRunId)).BusinessActivityCode);
        var saved = await verify.Requests.SingleAsync(r => r.RequestId == request.RequestId);
        Assert.False(saved.IsManualReviewRequired);
        Assert.NotEqual(DocumentUploadStatus.Quarantined, (await verify.RequestDocuments.SingleAsync(d => d.DocumentId == charge.DocumentId)).UploadStatus);
    }

    [Fact]
    public async Task Another_LLPs_report_or_charge_report_is_caught_by_its_LLPIN()
    {
        await using var db = Ctx();
        var (request, roc, charge) = await SeedAsync(db, "YYY-9999");   // the request names another LLP
        var reader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [roc.StoragePath] = [AboutTheLlp(), Directors()],
            [charge.StoragePath] = [AboutTheLlp(llpin: "XXX-0000")],   // and the charge report is a third one's
        });

        await new IngestionOrchestrator(db, reader, NullLogger<IngestionOrchestrator>.Instance).RunAsync(request.RequestId, roc.DocumentId, charge.DocumentId);

        await using var verify = Ctx();
        var saved = await verify.Requests.SingleAsync(r => r.RequestId == request.RequestId);
        Assert.True(saved.IsManualReviewRequired);
        Assert.Contains($"Request LLPIN 'YYY-9999' does not match ROC report LLPIN '{Llpin}'.", saved.ManualReviewReason);
        var quarantined = await verify.RequestDocuments.SingleAsync(d => d.DocumentId == charge.DocumentId);
        Assert.Equal(DocumentUploadStatus.Quarantined, quarantined.UploadStatus);
        Assert.Contains("Charge report LLPIN 'XXX-0000' does not match ROC report LLPIN", quarantined.QuarantineReason);
    }

    // ---- #539 PR 2: partners' contributions and the LLP statement of accounts ----------------------------------------------------------------

    private static SheetData Structure(bool bodyCorporate = false) => Sheet("Structure",
        Row("CONTRIBUTION BY PARTNERS - 31 Mar, 2026", "", "", "", ""),
        Row("Name", "Designation", "Obligation of Contribution (Crore)", "Contribution Received (Crore)", ""),
        Row("PARTNER ONE", "Designated Partner", 0.75, 0.75, ""),
        Row("PARTNER TWO", "Designated Partner", 1.25, 1.0, ""),
        Row("", "", "", "", ""),
        Row("CONTRIBUTION BY BODY CORPORATES - 31 Mar, 2026", "", "", "", ""),
        Row("Name", "Nominee Name", "Obligation of Contribution (Crore)", "Contribution Received (Crore)", ""),
        bodyCorporate ? Row("SAMPLE HOLDINGS PRIVATE LIMITED", "NOMINEE PERSON", 0.5, 0.25, "") : Row("There is no contribution by body corporates.", "", "", "", ""),
        Row("", "", "", "", ""),
        Row("SUMMARY OF DESIGNATED PARTNER(S) / PARTNER(S) - 31 Mar, 2026", "", "", "", ""),
        Row("Category", "Number of Partners", "Number of Designated Partners", "", "Total"),
        Row("", "", "Resident in India", "Others", ""),
        Row("Individual", 1.0, 2.0, 0.0, 3.0),
        Row("Companies", 0.0, 0.0, 1.0, 1.0),
        Row("Total", 1.0, 2.0, 1.0, 4.0));

    /// <summary>The real export's layout and labels (one year column), with made-up values.</summary>
    private static SheetData FinancialData() => Sheet("Financial Data",
        Row("STATEMENT OF ASSETS AND LIABILITIES (Rs. Crore)", "", "31 Mar, 2025", "31 Mar, 2024"),
        Row("Contribution and Liabilities", "", "", ""),
        Row("Partner's Funds", "", "", ""),
        Row("Contribution Received", "", 2.0, 1.5),
        Row("Reserves and Surplus", "", -0.25, -0.01),
        Row("Liabilities", "", "", ""),
        Row("Secured Loans", "", 0.4, 0.0),
        Row("Unsecured Loans", "", 0.1, 0.0),
        Row("Short Term Borrowing", "", 0.2, 0.0),
        Row("Creditor / Trade Payables - Advance from Customers", "", 0.3, 0.0),
        Row("Total", "", 2.75, 1.49),
        Row("Assets", "", "", ""),
        Row("Debtors / Trade Receivables", "", 0.6, 0.0),
        Row("Cash and Cash Equivalents", "", 1.87, 1.37),
        Row("Inventories", "", 0.15, 0.0),
        Row("Total", "", 2.75, 1.49),
        Row("STATEMENT OF INCOME AND EXPENDITURE (Rs. Crore)", "", "31 Mar, 2025", "31 Mar, 2024"),
        Row("Net Revenue", "", 3.5, 0.0),
        Row("Operating Profit ( EBITDA )", "", 0.4, 0.0),
        Row("Finance Costs", "", 0.05, 0.0),
        Row("Profit Before Tax", "", 0.3, 0.0),
        Row("Profit for the Period", "", 0.24, 0.0),
        Row("RATIOS", "", "31 Mar, 2025", "31 Mar, 2024"),
        Row("Return on Partners' Fund (%)", "", 13.6, -0.1),
        Row("CERTIFIER(s)", "", "", ""),
        Row("Year", "Certifier Name", "Designation", "Certifier Membership Number", "Certifier Firm Name", "Firm Registration Number", "Address (of firm or auditor)"),
        Row(2025.0, "SAMPLE AUDITOR", "Auditor", 111111.0, "SAMPLE & CO", "000000N", "1 SAMPLE STREET NEW DELHI 110001"));

    [Fact]
    public void The_LLP_profile_gives_its_contribution_in_place_of_capital()
    {
        var p = Assert.Single(CompanyProfileParser.Parse(AboutTheLlp(), 1, 1, null).Items);
        Assert.Equal((2.0m, 2.0m), (p.LlpObligationOfContributionCrore, p.LlpContributionReceivedCrore));
        Assert.Equal((null, null), (p.AuthorisedCapital, p.PaidUpCapital));
    }

    [Fact]
    public void An_LLP_structure_lists_each_contributor_and_the_partner_counts()
    {
        Assert.True(LlpStructureParser.IsLlpStructure(Structure()));
        Assert.False(LlpStructureParser.IsLlpStructure(Sheet("Structure", Row("SHARE HOLDING SUMMARY - 31 Mar, 2025", ""))));

        var result = LlpStructureParser.Parse(Structure(bodyCorporate: true), 1, 1, null, out var contributions);

        Assert.Empty(result.Warnings);
        Assert.Equal([("PARTNER ONE", LlpContributorKind.Partner, "Designated Partner", (string?)null, 0.75m, 0.75m),
                ("PARTNER TWO", LlpContributorKind.Partner, "Designated Partner", null, 1.25m, 1.0m),
                ("SAMPLE HOLDINGS PRIVATE LIMITED", LlpContributorKind.BodyCorporate, null, "NOMINEE PERSON", 0.5m, 0.25m)],
            contributions.Select(c => (c.Name, c.Kind, c.Designation, c.NomineeName, c.ObligationOfContributionCrore!.Value, c.ContributionReceivedCrore!.Value)));
        Assert.All(contributions, c => Assert.Equal(new DateOnly(2026, 3, 31), c.AsOnDate));
        var s = Assert.Single(result.Items);
        Assert.Equal((1, 3), (s.LlpPartners!.Value, s.LlpDesignatedPartners!.Value));   // designated: 2 resident + 1 other

        LlpStructureParser.Parse(Structure(), 1, 1, null, out var noBodyCorporate);
        Assert.Equal(2, noBodyCorporate.Count);   // "There is no contribution by body corporates." is not a contributor
    }

    [Fact]
    public void The_LLP_statement_of_accounts_gives_the_years_figures_with_partners_funds_as_net_worth()
    {
        var result = StandaloneFinancialDataParser.Parse(FinancialData(), 1, 1, null, out var facts);

        Assert.Empty(result.Errors);
        var fy25 = result.Items.Single(y => y.FinancialYear == 2025);
        Assert.Equal((2.0m, 1.75m, 0.5m, 0.2m, 0.7m), (fy25.ShareCapital!.Value, fy25.NetWorth!.Value, fy25.LongTermBorrowings!.Value,
            fy25.ShortTermBorrowings!.Value, fy25.TotalDebt!.Value));
        Assert.Equal((0.3m, 0.6m, 1.87m, 0.15m), (fy25.TradePayables!.Value, fy25.TradeReceivables!.Value, fy25.CashAndBank!.Value, fy25.Inventory!.Value));
        Assert.Equal((3.5m, 0.4m, 0.05m, 0.3m, 0.24m), (fy25.Revenue!.Value, fy25.Ebitda!.Value, fy25.FinanceCost!.Value, fy25.Pbt!.Value, fy25.Pat!.Value));
        Assert.Equal(1.49m, result.Items.Single(y => y.FinancialYear == 2024).NetWorth);

        Assert.Contains(facts, f => f.Label == "Reserves and Surplus" && f.FinancialYear == 2025 && f.NumericValue == -0.25m && f.Section == FinancialStatementSection.BalanceSheet);
        Assert.Contains(facts, f => f.Label == "Return on Partners' Fund (%)" && f.Section == FinancialStatementSection.Ratios);
        Assert.DoesNotContain(facts, f => f.RawValue.Contains("SAMPLE AUDITOR") || f.Label is "Year" or "2025");   // the certifier block is not figures
    }

    /// <summary>Review of #546: a part not reported (a row missing, or "-") is never taken as zero. The figure it would make stays empty, with a
    /// warning, instead of a confident total that may be wrong.</summary>
    [Fact]
    public void An_LLP_figure_with_a_part_not_reported_is_left_empty_and_says_so()
    {
        var sheet = Sheet("Financial Data",
            Row("STATEMENT OF ASSETS AND LIABILITIES (Rs. Crore)", "", "31 Mar, 2025"),
            Row("Contribution Received", "", 2.0),
            Row("Reserves and Surplus", "", "-"),
            Row("Secured Loans", "", 0.4),
            Row("Short Term Borrowing", "", 0.2));

        var result = StandaloneFinancialDataParser.Parse(sheet, 1, 1, null, out _);

        var fy = Assert.Single(result.Items);
        Assert.Equal((2.0m, (decimal?)null, (decimal?)null, 0.2m, (decimal?)null),
            (fy.ShareCapital!.Value, fy.NetWorth, fy.LongTermBorrowings, fy.ShortTermBorrowings!.Value, fy.TotalDebt));
        Assert.Equal(["NetWorth FY2025 is not derived: Reserves and Surplus is not reported on the LLP's statement.",
                "LongTermBorrowings FY2025 is not derived: Unsecured Loans is not reported on the LLP's statement.",
                "TotalDebt FY2025 is not derived: a long-term loan line is not reported on the LLP's statement."],
            result.Warnings.Where(w => w.IssueCode == "LLP_FIGURE_INCOMPLETE").Select(w => w.Message));
    }

    [Fact]
    public void A_company_statement_without_a_total_equity_line_is_never_given_an_LLPs_derived_net_worth()
    {
        var sheet = Sheet("Standalone Financial Data",
            Row("BALANCE SHEET (Rs. Crore)", "", "Mar 2025"),
            Row("Share Capital", "", 1.0),
            Row("Reserves and Surplus", "", 4.0),
            Row("Secured Loans", "", 2.0));
        var fy = Assert.Single(StandaloneFinancialDataParser.Parse(sheet, 1, 1, null, out _).Items);
        Assert.Equal((1.0m, (decimal?)null, (decimal?)null), (fy.ShareCapital!.Value, fy.NetWorth, fy.LongTermBorrowings));
    }

    [Fact]
    public async Task An_LLP_ingests_its_contributors_partner_counts_and_statement_of_accounts()
    {
        await using var db = Ctx();
        var (request, roc, _) = await SeedAsync(db, Llpin);
        var reader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
        {
            [roc.StoragePath] = [AboutTheLlp(), Directors(), Highlights(), Structure(), FinancialData()],
        });

        var run = await new IngestionOrchestrator(db, reader, NullLogger<IngestionOrchestrator>.Instance).RunAsync(request.RequestId, roc.DocumentId, chargeDocumentId: null);

        Assert.NotEqual(IngestionRunStatus.Failed, run.Status);
        await using var verify = Ctx();
        Assert.Equal(["PARTNER ONE", "PARTNER TWO"], await verify.LlpContributions.Where(c => c.IngestionRunId == run.IngestionRunId).OrderBy(c => c.DisplayOrder).Select(c => c.Name).ToListAsync());
        var structure = await verify.CompanyStructures.SingleAsync(s => s.IngestionRunId == run.IngestionRunId);
        Assert.Equal((1, 3), (structure.LlpPartners!.Value, structure.LlpDesignatedPartners!.Value));
        Assert.Equal(1.75m, (await verify.FinancialYearData.SingleAsync(f => f.IngestionRunId == run.IngestionRunId && f.FinancialYear == 2025)).NetWorth);
        Assert.Equal(2.0m, (await verify.CompanyProfiles.SingleAsync(p => p.IngestionRunId == run.IngestionRunId)).LlpContributionReceivedCrore);
        // The company structure parser never ran on it: no "no share holding summary" warning.
        Assert.False(await verify.IngestionIssues.AnyAsync(i => i.IngestionRunId == run.IngestionRunId && i.IssueCode == "STRUCTURE_SUMMARY_EMPTY"));
    }
}
