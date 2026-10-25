using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#497 PR B: the Registrar's certificate of change of name is read for the date the new name took effect and the page it is on,
/// and checked against the name history. The texts below are the extracted text of real certificates (both wordings in use), cut to the
/// certificate's own words; the run-together words are as the extraction gives them.</summary>
public class NameChangeCertificateTests : IAsyncLifetime
{
    // Companies Act 1956 wording, a conversion to public company (request 4's certificate).
    private const string Coastal1956 = """
        --- Page 1 (native) ---
        Baart sarkar–ka^pao-roT kaya- maM~alayakmpnaI rijasT/ar kayaa-laya‚ maOsasa-COASTAL PROJECTS PRIVATE LIMITEDGOVERNMENT OF INDIA - MINISTRY OF CORPORATE AFFAIRSFresh Certificate of Incorporation Consequent upon Change of Name onConversion to Public Limited CompanyCorporate Identity Number : U45203OR1995PLC003982In the matter of M/s COASTAL PROJECTS PRIVATE LIMITEDI hereby certify that  COASTAL PROJECTS PRIVATE LIMITED which was originally incorporated on   First day ofMay Nineteen Hundred Ninety Five under the Companies Act, 1956 (No. 1 of 1956) as  COASTAL PROJECTSPRIVATE LIMITED having duly passed the necessary resolution on  30/06/2010 in terms of Section 31/ 21 readwith Section 44 of the Companies Act, 1956; the name of the said company is this day changed to COASTALPROJECTS  LIMITED and this Certificate is issued pursuant to Section 23(1) of the said Act.Given under my hand at Cuttack this Eleventh day of August Two Thousand Ten .ka^pao-roT phcaana saM#yaa  :U45203OR1995PLC003982
        """;

    // Companies Act 1956 wording, the name corrected (request 9's certificate).
    private const string Pruksa1956 = """
        --- Page 1 (native) ---
        maOsasa-ko maamalao mao‚ maOM etdWara sa%yaaipt krta hU^M ik GOVERNMENT OF INDIA - MINISTRY OF CORPORATE AFFAIRSRegistrar of Companies, KarnatakaFresh Certificate of Incorporation Consequent upon Change of NameIn the matter of M/s Preuksa India Housing Private LimitedI hereby certify that  Preuksa India Housing Private Limited which was originally incorporated on  Twenty First day ofJanuary Two Thousand  Nine under the Companies Act, 1956 (No. 1 of 1956) as  Preuksa India Housing PrivateLimited having duly passed the necessary resolution in terms of Section 21 of the Companies Act, 1956 and theapproval of the Central Government signified in writing having been accorded thereto under Section 21 of theCompanies Act, 1956 vide SRN  A98173974 dated  25/11/2010 the name of the said company is this daychanged to  PRUKSA INDIA HOUSING PRIVATE LIMITED and this Certificate is issued pursuant to Section 23(1) ofthe said Act.Given under my hand at Bangalore this Twenty Fifth day of November Two Thousand Ten .Baart sarkar–ka^pao-roT kaya- maM~alaya
        """;

    // Companies (Incorporation) Rules 2014 wording (request 11's certificate); the year runs on into the next word.
    private const string Lifex2014 = """
        --- Page 1 (native) ---
        GOVERNMENT OF INDIAMINISTRY OF CORPORATE AFFAIRSOffice of the Central Processing CentreCertificate of Incorporation pursuant to change of name [Pursuant to rule 29 of the Companies (Incorporation) Rules, 2014]Corporate Identification Number (CIN): U51909DL2019PTC355815I hereby certify that the name of the company has been changed from KE HEALTHCARE PRIVATE LIMITED to LIFEXSCIENCES PRIVATE LIMITED with effect from the date of this certificate and that the company is Company limited byshares.Company was originally incorporated with the name CINQUE AYURVEDA SCIENCES PRIVATE LIMITEDGiven under my hand at ROC, CPC this  ELEVENTH day of APRIL  TWO THOUSAND TWENTY FIVEAssistant Registrar of Companies/ Deputy Registrar of Companies/ Registrar of CompaniesCentral Processing Centre
        """;

    // The notice calling the meeting that approved the change (filed in the same request): it talks about the change but certifies nothing.
    private const string EgmNotice = """
        --- Page 1 (OCR) ---
        SHORTER NOTICE OF EXTRAORDINARY GENERAL MEETING
        1. APPROVAL FOR CHANGE OF NAME OF THE COMPANY AND CONSEQUENT ALTERATION TO THE MEMORANDUM OF ASSOCIATION
        "RESOLVED THAT ... the consent of the members be and is hereby accorded to change the name of the Company from "Ke Healthcare Private Limited" to "LifeX Sciences Private Limited".
        --- Page 2 (OCR) ---
        RESOLVED FURTHER THAT subsequent to the issue of fresh Certificate of Incorporation by Registrar of Companies (ROC), the old name has been changed from Ke Healthcare Private Limited to LifeX Sciences Private Limited and the company shall ...
        """;

    // A certificate whose details are images: only the form's labels come out as text.
    private const string ImageOnly = """
        --- Page 1 (native) ---
        Certificate of Incorporation pursuant to change of name [Pursuant to rule 29 of the Companies (Incorporation) Rules, 2014]    GOVERNMENT OF INDIA MINISTRY OF CORPORATE AFFAIRS  Registrar of Companies  Mailing Address as per record available in Registrar of Companies office:  Corporate Identification Number (CIN): Office of the Registrar of Companies
        """;

    [Fact]
    public void The_1956_wording_gives_the_old_name_the_new_one_and_the_date_in_words()
    {
        var change = Assert.Single(NameChangeCertificateReader.Read(Coastal1956, 135));
        Assert.Equal(("COASTAL PROJECTS PRIVATE LIMITED", "COASTALPROJECTS LIMITED", new DateOnly(2010, 8, 11), 135L, 1),
            (change.FromName, change.ToName, change.EffectiveDate, change.FilingDocumentId, change.Page));

        var pruksa = Assert.Single(NameChangeCertificateReader.Read(Pruksa1956, 1040));
        Assert.Equal(("Preuksa India Housing Private Limited", "PRUKSA INDIA HOUSING PRIVATE LIMITED", new DateOnly(2010, 11, 25)),
            (pruksa.FromName, pruksa.ToName, pruksa.EffectiveDate));
    }

    [Fact]
    public void The_2014_rules_wording_gives_both_names_and_the_date_even_when_the_year_runs_into_the_next_word()
    {
        var change = Assert.Single(NameChangeCertificateReader.Read(Lifex2014, 1338));
        Assert.Equal(("KE HEALTHCARE PRIVATE LIMITED", "LIFEXSCIENCES PRIVATE LIMITED", new DateOnly(2025, 4, 11)),
            (change.FromName, change.ToName, change.EffectiveDate));
    }

    [Fact]
    public void A_certificate_on_a_later_page_of_another_document_is_found_with_its_page()
    {
        var memorandum = "--- Page 1 (OCR) ---\nMEMORANDUM OF ASSOCIATION OF LIFEX SCIENCES PRIVATE LIMITED\n" + Lifex2014.Replace("--- Page 1 (native) ---", "--- Page 2 (OCR) ---");
        Assert.Equal(2, Assert.Single(NameChangeCertificateReader.Read(memorandum, 1336)).Page);
    }

    [Theory]
    [InlineData(EgmNotice)]
    [InlineData(ImageOnly)]
    [InlineData("")]
    public void A_notice_a_resolution_or_a_certificate_with_no_readable_text_states_no_change(string text) =>
        Assert.Empty(NameChangeCertificateReader.Read(text, 1));

    [Theory]
    [InlineData("Twenty Fifth day of November Two Thousand Ten .", 2010, 11, 25)]
    [InlineData("ELEVENTH day of APRIL  TWO THOUSAND TWENTY FIVEAssistant", 2025, 4, 11)]
    [InlineData("First day of May Nineteen Hundred Ninety Five under", 1995, 5, 1)]
    [InlineData("Thirtieth day of June Two Thousand and Nine", 2009, 6, 30)]
    [InlineData("twenty-first day of January two thousand nine", 2009, 1, 21)]
    [InlineData("11th day of April 2025.", 2025, 4, 11)]
    [InlineData("1st day of May, 2019", 2019, 5, 1)]
    [InlineData("11/04/2025", 2025, 4, 11)]
    public void The_certificate_date_is_read_in_words_or_figures(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), NameChangeCertificateReader.ParseDate(text));

    [Theory]
    [InlineData("Thirty Second day of May Two Thousand Ten")]
    [InlineData("the day of the meeting")]
    [InlineData("31/02/2020")]
    public void A_date_that_is_not_one_is_not_read(string text) => Assert.Null(NameChangeCertificateReader.ParseDate(text));

    // ── Checked against the name history ──

    private static CompanyNameHistory History(int order, string name, DateOnly? till) =>
        new() { CompanyNameHistoryId = order, DisplayOrder = order, PreviousName = name, TillDate = till };

    [Fact]
    public void A_certificate_that_agrees_with_the_history_confirms_it_and_the_register_spelling_is_shown()
    {
        var history = new List<CompanyNameHistory>
        {
            History(1, "CINQUE AYURVEDA SCIENCES PRIVATE LIMITED", new DateOnly(2020, 10, 1)),
            History(2, "KE HEALTHCARE PRIVATE LIMITED", new DateOnly(2025, 4, 11)),
        };
        var changes = NameChanges.Merge(history, "LIFEX SCIENCES PRIVATE LIMITED", NameChangeCertificateReader.Read(Lifex2014, 1338));

        Assert.Equal(2, changes.Count);
        Assert.Equal((NameChangeCheck.HistoryOnly, "CINQUE AYURVEDA SCIENCES PRIVATE LIMITED", "KE HEALTHCARE PRIVATE LIMITED"), (changes[0].Check, changes[0].FromName, changes[0].ToName));
        Assert.Null(changes[0].Certificate);
        Assert.Equal((NameChangeCheck.Confirmed, "KE HEALTHCARE PRIVATE LIMITED", "LIFEX SCIENCES PRIVATE LIMITED", new DateOnly(2025, 4, 11), 1338L),
            (changes[1].Check, changes[1].FromName, changes[1].ToName, changes[1].Date, changes[1].Certificate?.FilingDocumentId));
    }

    /// <summary>Request 4 as it is in the dev database: the name history dates "COASTAL PROJECTS PRIVATE LIMITED" to 1 May 1995 (the day of
    /// incorporation) and has it renamed to "EAST COAST CARRIERS PVT LTD"; the certificate says it became Coastal Projects Limited on
    /// 11 Aug 2010. The certificate's date and new name are shown, with the history's date beside it.</summary>
    [Fact]
    public void A_certificate_whose_date_differs_from_the_history_is_dated_by_the_certificate_and_says_so()
    {
        var history = new List<CompanyNameHistory>
        {
            History(1, "COASTAL PROJECTS PRIVATE LIMITED", new DateOnly(1995, 5, 1)),
            History(2, "EAST COAST CARRIERS PVT LTD", new DateOnly(2014, 6, 19)),
        };
        var changes = NameChanges.Merge(history, "Coastal Projects Limited", NameChangeCertificateReader.Read(Coastal1956, 135));

        Assert.Equal(2, changes.Count);
        var certified = changes[0];
        Assert.Equal((NameChangeCheck.DatesDiffer, "COASTAL PROJECTS PRIVATE LIMITED", "Coastal Projects Limited", new DateOnly(2010, 8, 11), new DateOnly(1995, 5, 1)),
            (certified.Check, certified.FromName, certified.ToName, certified.Date, certified.HistoryDate));
        Assert.Equal((NameChangeCheck.HistoryOnly, "EAST COAST CARRIERS PVT LTD"), (changes[1].Check, changes[1].FromName));
    }

    [Fact]
    public void A_change_only_a_certificate_states_is_added_and_with_no_certificate_the_history_is_unchanged()
    {
        var history = new List<CompanyNameHistory> { History(1, "PRUKSA HOMES PRIVATE LIMITED", new DateOnly(2015, 3, 2)) };
        var changes = NameChanges.Merge(history, "PRUKSA INDIA HOUSING PRIVATE LIMITED", NameChangeCertificateReader.Read(Pruksa1956, 1040));

        Assert.Equal(2, changes.Count);
        Assert.Equal((NameChangeCheck.CertificateOnly, "Preuksa India Housing Private Limited", "PRUKSA INDIA HOUSING PRIVATE LIMITED", new DateOnly(2010, 11, 25)),
            (changes[0].Check, changes[0].FromName, changes[0].ToName, changes[0].Date));
        Assert.Null(changes[0].HistoryRow);
        Assert.Equal(NameChangeCheck.HistoryOnly, changes[1].Check);

        var plain = NameChanges.Merge(history, "PRUKSA INDIA HOUSING PRIVATE LIMITED", []);
        Assert.Equal(("PRUKSA HOMES PRIVATE LIMITED", "PRUKSA INDIA HOUSING PRIVATE LIMITED", new DateOnly(2015, 3, 2), NameChangeCheck.HistoryOnly),
            (plain.Single().FromName, plain.Single().ToName, plain.Single().Date, plain.Single().Check));
    }

    [Fact]
    public void An_undated_history_row_keeps_its_place_and_an_undated_certificate_comes_last()
    {
        var history = new List<CompanyNameHistory>
        {
            History(1, "ALPHA PRIVATE LIMITED", new DateOnly(2001, 1, 1)),
            History(2, "BETA PRIVATE LIMITED", null),
            History(3, "GAMMA PRIVATE LIMITED", new DateOnly(2012, 1, 1)),
        };
        var changes = NameChanges.Merge(history, "DELTA LIMITED", [new CertificateNameChange("OMEGA PRIVATE LIMITED", "SIGMA PRIVATE LIMITED", null, 7, 1)]);

        Assert.Equal(["ALPHA PRIVATE LIMITED", "BETA PRIVATE LIMITED", "GAMMA PRIVATE LIMITED", "OMEGA PRIVATE LIMITED"], changes.Select(c => c.FromName));
    }

    [Fact]
    public void The_timeline_dates_a_certified_change_by_the_certificate_and_links_its_page()
    {
        var vm = new RequestDetailsViewModel
        {
            Request = new McaRequest { RequestId = 4, CompanyName = "Coastal Projects Limited" },
            Documents = [],
            LatestRun = new IngestionRun(),
            NameHistory = [History(1, "COASTAL PROJECTS PRIVATE LIMITED", new DateOnly(1995, 5, 1))],
            NameChangeCertificates = NameChangeCertificateReader.Read(Coastal1956, 135),
        };

        var renamed = Assert.Single(CorporateTimelineBuilder.BuildFrom(vm));
        Assert.Equal(new DateOnly(2010, 8, 11), renamed.Date);
        Assert.Equal("Renamed from \"COASTAL PROJECTS PRIVATE LIMITED\" to \"Coastal Projects Limited\"", renamed.Title);
        Assert.Equal("The certificate of change of name is dated 11 Aug 2010; the name history gives 1 May 1995.", renamed.Detail);
        Assert.Equal(("Certificate of change of name, page 1", "/Requests/4/documents/135/view#page=1"), (renamed.LinkText, renamed.LinkUrl));
        Assert.Equal(nameof(CompanyNameHistory), renamed.Provenance.EntityType);
    }

    // ── Against the database: which documents are read ──

    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "namecert-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>The certificate is filed on its own and again inside the altered memorandum; a byte-identical copy of the certificate is in
    /// the batch too, and a charge document that happens to quote the wording is not constitutional. One change is read, from the certificate.</summary>
    [Fact]
    public async Task The_constitutional_documents_are_read_and_the_same_change_in_several_is_one_from_the_certificate()
    {
        long requestId, certificateId;
        await using (var db = Ctx())
        {
            var client = new Client { ClientCode = "NC" + Guid.NewGuid().ToString("N")[..8], ClientName = "Name change", CreatedDate = DateTime.UtcNow };
            var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "LIFEX SCIENCES PRIVATE LIMITED", RequestNumber = $"NC-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
            var source = new RequestDocument { Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip", StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow };
            db.RequestDocuments.Add(source);
            await db.SaveChangesAsync();
            var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = source.DocumentId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
            db.McaFilingBatches.Add(batch);
            await db.SaveChangesAsync();
            var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "AB1234567", NestedZipName = "1.zip", OuterCategoryFolder = "Incorporation Documents" };
            db.McaFilings.Add(filing);
            await db.SaveChangesAsync();

            McaFilingDocument Doc(string name, string formType, FilingCategory category, string text, long? duplicateOf = null)
            {
                var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(path, text);
                return new McaFilingDocument
                {
                    FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId, OriginalFileName = name, StoragePath = @"C:\fake\a.pdf",
                    FileHash = Guid.NewGuid().ToString("N"), ProcessingStatus = FilingDocumentProcessingStatus.Completed, FormType = formType, Category = category,
                    ClassificationMethod = "FilenameKeyword", UpdatedAt = DateTime.UtcNow, ExtractedTextPath = path, DuplicateOfDocumentId = duplicateOf
                };
            }

            var memorandum = Doc("Altered-MOA-3.pdf", "MoA", FilingCategory.Constitutional,
                "--- Page 1 (OCR) ---\nMEMORANDUM OF ASSOCIATION\n" + Lifex2014.Replace("--- Page 1 (native) ---", "--- Page 2 (OCR) ---"));
            db.McaFilingDocuments.Add(memorandum);
            await db.SaveChangesAsync();
            var certificate = Doc("Certificate-of-change-of-name.pdf", NameChangeCertificateLoader.CertificateFormType, FilingCategory.Constitutional, Lifex2014);
            db.McaFilingDocuments.Add(certificate);
            await db.SaveChangesAsync();
            db.McaFilingDocuments.AddRange(
                Doc("Certificate-copy.pdf", NameChangeCertificateLoader.CertificateFormType, FilingCategory.Constitutional, Lifex2014, duplicateOf: certificate.FilingDocumentId),
                Doc("Form CHG-1.pdf", "Form CHG-1", FilingCategory.Charge, Coastal1956));
            await db.SaveChangesAsync();
            (requestId, certificateId) = (request.RequestId, certificate.FilingDocumentId);
        }

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        await using (var db = Ctx())
        {
            var change = Assert.Single(await NameChangeCertificateLoader.LoadAsync(db, requestId, CancellationToken.None, cache));
            Assert.Equal((certificateId, 1, new DateOnly(2025, 4, 11)), (change.FilingDocumentId, change.Page, change.EffectiveDate));
        }

        // Read once per batch: a second load comes from the cache, even with the files gone.
        foreach (var file in Directory.GetFiles(_dir)) File.Delete(file);
        await using (var db = Ctx())
            Assert.Equal(certificateId, Assert.Single(await NameChangeCertificateLoader.LoadAsync(db, requestId, CancellationToken.None, cache)).FilingDocumentId);
    }
}
