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
}
