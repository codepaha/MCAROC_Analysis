using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Identifiers printed in a litigation order (CIN, LLPIN, PAN, GSTIN, TAN, DIN) are read exactly as written and tied to the
/// company. The corporate formats are strict; the ones that look like any number or word count only where the text labels them.</summary>
public class OrderIdentifiersTests
{
    private static string[] Values(string text, OrderIdentifierType type) =>
        OrderIdentifiers.Extract(text).Where(i => i.Type == type).Select(i => i.Value).ToArray();

    [Fact]
    public void A_cin_with_a_real_ownership_code_is_read_and_one_with_a_made_up_code_is_not()
    {
        Assert.Equal(["U12345MH2010PTC123456"], Values("the Corporate Debtor, CIN U12345MH2010PTC123456, having its office at Mumbai", OrderIdentifierType.Cin));
        Assert.Equal(["L99999GJ1995PLC000001"], Values("Listed company L99999GJ1995PLC000001.", OrderIdentifierType.Cin));
        Assert.Empty(Values("U12345MH2010XYZ123456", OrderIdentifierType.Cin));       // not an ownership code
        Assert.Empty(Values("XU12345MH2010PTC123456", OrderIdentifierType.Cin));      // inside a longer token
        Assert.Empty(Values("U12345MH2010PTC1234567", OrderIdentifierType.Cin));      // one digit too many
    }

    [Theory]
    [InlineData("PAN ABCCE1234F of the respondent", "ABCCE1234F")]
    [InlineData("(PAN: AAACC1234D)", "AAACC1234D")]
    public void A_pan_is_read(string text, string expected) => Assert.Equal([expected], Values(text, OrderIdentifierType.Pan));

    [Theory]
    [InlineData("ABCXE1234F")]   // the fourth letter must be a holder type
    [InlineData("ABCDE12345")]   // the last character must be a letter
    [InlineData("ABCCE1234FG")]  // a longer token
    public void Something_that_is_not_a_pan_is_not_read(string text) => Assert.Empty(Values($"Ref {text} filed", OrderIdentifierType.Pan));

    [Fact]
    public void A_gstin_is_read_whole_and_its_pan_is_not_also_read_as_a_separate_pan()
    {
        var found = OrderIdentifiers.Extract("GSTIN 29ABCCE1234F1Z5 was registered.");

        var gstin = Assert.Single(found);
        Assert.Equal((OrderIdentifierType.Gstin, "29ABCCE1234F1Z5"), (gstin.Type, gstin.Value));
        Assert.Empty(Values("GSTIN 99ABCCE1234F1Z5", OrderIdentifierType.Gstin)); // there is no state code 99
    }

    [Theory]
    [InlineData("Director DIN: 01234567 appeared", "01234567")]
    [InlineData("(DIN No. 01234567)", "01234567")]
    [InlineData("Director Identification Number 01234567", "01234567")]
    [InlineData("D.I.N. 01234567", "01234567")]
    public void A_din_is_read_where_the_text_labels_it(string text, string expected) => Assert.Equal([expected], Values(text, OrderIdentifierType.Din));

    [Fact]
    public void An_eight_digit_number_with_no_din_label_is_not_a_din()
    {
        Assert.Empty(Values("Diary No. 20240123 was listed; amount Rs. 12345678", OrderIdentifierType.Din));
        Assert.Empty(Values("The DIN of the company's bankers", OrderIdentifierType.Din));
    }

    [Fact]
    public void A_tan_and_an_llpin_are_read_only_where_labelled()
    {
        Assert.Equal(["ABCD12345E"], Values("TAN: ABCD12345E", OrderIdentifierType.Tan));
        Assert.Empty(Values("Reference ABCD12345E", OrderIdentifierType.Tan));
        Assert.Equal(["AAB-1234"], Values("the LLP (LLPIN AAB-1234) was", OrderIdentifierType.Llpin));
        Assert.Empty(Values("Case AAB-1234 listed", OrderIdentifierType.Llpin));
    }

    [Fact]
    public void Each_identifier_carries_its_page_and_the_words_around_it_and_a_value_twice_on_a_page_is_one_entry()
    {
        const string text =
            "--- Page 1 (native) ---\nIn the matter of Example Builders, CIN U12345MH2010PTC123456, the petition\n" +
            "and again U12345MH2010PTC123456 here.\n--- Page 2 (OCR) ---\nThe respondent PAN ABCCE1234F, and later U12345MH2010PTC123456.\n";

        var found = OrderIdentifiers.Extract(text);

        Assert.Equal([(OrderIdentifierType.Cin, 1), (OrderIdentifierType.Cin, 2), (OrderIdentifierType.Pan, 2)],
            found.Select(i => (i.Type, i.Page)).OrderBy(x => x.Page).ThenBy(x => x.Type));
        var first = found.First(i => i.Page == 1);
        Assert.Contains("In the matter of Example Builders", first.Context);
        Assert.Contains("the petition", first.Context);
    }

    [Fact]
    public void Text_with_no_page_markers_is_page_one_and_empty_text_has_no_identifiers()
    {
        Assert.Equal(1, Assert.Single(OrderIdentifiers.Extract("CIN U12345MH2010PTC123456")).Page);
        Assert.Empty(OrderIdentifiers.Extract(null));
        Assert.Empty(OrderIdentifiers.Extract("   "));
    }

    [Fact]
    public void A_pan_is_masked_and_the_corporate_identifiers_stay_whole()
    {
        Assert.Equal("ABCCE****F", OrderIdentifiers.Mask(OrderIdentifierType.Pan, "ABCCE1234F"));
        Assert.Equal("U12345MH2010PTC123456", OrderIdentifiers.Mask(OrderIdentifierType.Cin, "U12345MH2010PTC123456"));
        Assert.Equal("29ABCCE1234F1Z5", OrderIdentifiers.Mask(OrderIdentifierType.Gstin, "29ABCCE1234F1Z5"));
    }

    private static readonly CompanyIdentity Company = new("U12345MH2010PTC123456", null, "ABCCE1234F", ["27ABCCE1234F1Z9"], ["01234567", "00098765"]);

    private static OrderIdentifier Id(OrderIdentifierType type, string value) => new(type, value, 1, "");

    [Fact]
    public void An_identifier_is_the_companys_when_it_is_its_cin_pan_or_a_gstin_carrying_its_pan()
    {
        Assert.Equal(IdentifierMatch.Company, OrderIdentifiers.Classify(Id(OrderIdentifierType.Cin, "U12345MH2010PTC123456"), Company));
        Assert.Equal(IdentifierMatch.Company, OrderIdentifiers.Classify(Id(OrderIdentifierType.Pan, "ABCCE1234F"), Company));
        Assert.Equal(IdentifierMatch.Company, OrderIdentifiers.Classify(Id(OrderIdentifierType.Gstin, "27ABCCE1234F1Z9"), Company));
        Assert.Equal(IdentifierMatch.Company, OrderIdentifiers.Classify(Id(OrderIdentifierType.Gstin, "29ABCCE1234F1Z5"), Company)); // another state, same PAN
    }

    [Fact]
    public void A_dins_leading_zeros_do_not_matter_and_anything_else_is_another_partys()
    {
        Assert.Equal(IdentifierMatch.Director, OrderIdentifiers.Classify(Id(OrderIdentifierType.Din, "00098765"), Company));
        Assert.Equal(IdentifierMatch.Director, OrderIdentifiers.Classify(Id(OrderIdentifierType.Din, "98765"), Company));
        Assert.Equal(IdentifierMatch.Other, OrderIdentifiers.Classify(Id(OrderIdentifierType.Din, "11111111"), Company));
        Assert.Equal(IdentifierMatch.Other, OrderIdentifiers.Classify(Id(OrderIdentifierType.Cin, "U99999DL2001PTC999999"), Company));
        Assert.Equal(IdentifierMatch.Other, OrderIdentifiers.Classify(Id(OrderIdentifierType.Pan, "ZZZPZ9999Z"), Company));
        Assert.Equal(IdentifierMatch.Other, OrderIdentifiers.Classify(Id(OrderIdentifierType.Tan, "ABCD12345E"), Company));
    }

    [Fact]
    public void A_company_with_no_identifiers_matches_nothing()
    {
        var empty = new CompanyIdentity(null, null, null, [], []);

        Assert.True(empty.IsEmpty);
        Assert.Equal(IdentifierMatch.Other, OrderIdentifiers.Classify(Id(OrderIdentifierType.Cin, "U12345MH2010PTC123456"), empty));
        Assert.Equal(IdentifierMatch.Other, OrderIdentifiers.Classify(Id(OrderIdentifierType.Pan, ""), empty)); // an empty value never equals an empty id
    }

    [Fact]
    public void The_standing_of_a_case_follows_the_best_identifier_its_orders_print()
    {
        MatchedOrderIdentifier M(IdentifierMatch match) => new(Id(OrderIdentifierType.Cin, "X"), match, 1, null, null, null);

        Assert.Equal(IdentityEvidenceStatus.Confirmed, OrderIdentifiers.StatusOf([M(IdentifierMatch.Other), M(IdentifierMatch.Company)], 2));
        Assert.Equal(IdentityEvidenceStatus.DirectorOnly, OrderIdentifiers.StatusOf([M(IdentifierMatch.Director), M(IdentifierMatch.Other)], 2));
        Assert.Equal(IdentityEvidenceStatus.NameOnly, OrderIdentifiers.StatusOf([M(IdentifierMatch.Other)], 2));
        Assert.Equal(IdentityEvidenceStatus.NameOnly, OrderIdentifiers.StatusOf([], 1));
        Assert.Equal(IdentityEvidenceStatus.NoOrderText, OrderIdentifiers.StatusOf([], 0));
    }
}

public sealed class LitigationIdentityEvidenceTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(McaRequest Request, long CaseId, long[] OrderIds)> SeedAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "ID" + Guid.NewGuid().ToString("N")[..8], ClientName = "Identity Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Example Builders Private Limited", Cin = "U12345MH2010PTC123456", Pan = "ABCCE1234F",
            RequestNumber = $"ID-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "id-" + Guid.NewGuid().ToString("N") };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = job.RawResponseHash!,
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        var theCase = new LitigationCase
        {
            RequestId = request.RequestId, Type = "nclt", Court = "nclt", CaseNumber = "C.P.(IB)No.593/KB/2017", CaseStatus = "PENDING",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
            Orders = [new LitigationCaseOrder { OrderDate = "2018-03-12", OrderType = "interm_order" }, new LitigationCaseOrder { OrderDate = "2018-04-18", OrderType = "interm_order" }]
        };
        db.LitigationCases.Add(theCase);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = theCase.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (request, theCase.LitigationCaseId, theCase.Orders.Select(o => o.LitigationCaseOrderId).ToArray());
    }

    private static async Task AddTextAsync(AppDbContext db, long orderId, string? text)
    {
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument
        {
            LitigationCaseOrderId = orderId, Status = LitigationOrderDocumentStatus.Downloaded, RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            ExtractedText = text, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_order_that_prints_the_companys_cin_confirms_the_case_and_names_the_page()
    {
        await using var db = CreateContext();
        var (request, caseId, orders) = await SeedAsync(db);
        await AddTextAsync(db, orders[0], "--- Page 1 (native) ---\nIn the matter of Example Builders Private Limited\n--- Page 2 (native) ---\nCIN U12345MH2010PTC123456 and a financial creditor with CIN U99999DL2001PTC999999.");

        var page = await new LitigationCasePageService(db).GetAsync(request.RequestId, caseId, CancellationToken.None);

        Assert.Equal(IdentityEvidenceStatus.Confirmed, page!.Identity.Status);
        Assert.Equal(1, page.Identity.OrdersWithText);
        var company = Assert.Single(page.Identity.Identifiers, m => m.Match == IdentifierMatch.Company);
        Assert.Equal(("U12345MH2010PTC123456", 2), (company.Identifier.Value, company.Identifier.Page));
        var other = Assert.Single(page.Identity.Identifiers, m => m.Match == IdentifierMatch.Other);
        Assert.Equal("U99999DL2001PTC999999", other.Identifier.Value);
        Assert.Equal(IdentifierMatch.Company, page.Identity.Identifiers[0].Match); // the company's own first
    }

    [Fact]
    public async Task Orders_with_no_identifier_of_the_company_leave_the_case_on_its_name_match_and_no_text_is_not_checked()
    {
        await using var db = CreateContext();
        var (request, caseId, orders) = await SeedAsync(db);
        var service = new LitigationCasePageService(db);

        Assert.Equal(IdentityEvidenceStatus.NoOrderText, (await service.GetAsync(request.RequestId, caseId, CancellationToken.None))!.Identity.Status);

        await AddTextAsync(db, orders[0], "The petition is admitted. List on 12.04.2018. Diary No. 20180412.");
        await AddTextAsync(db, orders[1], null); // a document with no text does not count as read

        var page = await service.GetAsync(request.RequestId, caseId, CancellationToken.None);
        Assert.Equal((IdentityEvidenceStatus.NameOnly, 1, 0), (page!.Identity.Status, page.Identity.OrdersWithText, page.Identity.OrdersNamingCompany));
        Assert.Empty(page.Identity.Identifiers);
    }

    [Fact]
    public async Task A_name_only_case_reports_how_many_of_the_orders_read_name_the_company()
    {
        await using var db = CreateContext();
        var (request, caseId, orders) = await SeedAsync(db);
        await AddTextAsync(db, orders[0], "Heard Ld. Counsel for M/s EXAMPLE BUILDERS PVT. LTD. The application is listed on 12.04.2018.");
        await AddTextAsync(db, orders[1], "The Example  Builders\nPrivate Limited has not appeared; List on 18.04.2018. Another party, Example Traders Limited, appeared.");

        var page = await new LitigationCasePageService(db).GetAsync(request.RequestId, caseId, CancellationToken.None);

        Assert.Equal((IdentityEvidenceStatus.NameOnly, 2, 2), (page!.Identity.Status, page.Identity.OrdersWithText, page.Identity.OrdersNamingCompany));
    }

    [Theory]
    [InlineData("Counsel for M/s Example Builders (P) Ltd.", true)]
    [InlineData("EXAMPLE   BUILDERS\nPRIVATE LIMITED", true)]
    [InlineData("Example Builders and Developers Limited", true)]   // the company's words occur in order as whole words
    [InlineData("Example Buildersmith Limited", false)]             // not whole words
    [InlineData("Builders Example Limited", false)]                  // not in order
    [InlineData("Example Traders Limited", false)]
    [InlineData("", false)]
    public void The_company_is_named_when_its_distinguishing_words_occur_in_order_as_whole_words(string text, bool expected) =>
        Assert.Equal(expected, LitigationCompanySides.NamesCompany(text, [LitigationCompanySides.Core("Example Builders Private Limited")]));
}
