using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#364: charge e-forms (Form 8 / CHG-1) read field by field from their XFA data (#369) and linked to the charge
/// register — by the charge ID a modification form states, or for a creation form by its instrument date and amount when
/// exactly one charge matches. A form is never attached to a guessed charge.</summary>
public class ChargeFormsTests
{
    // The "Name: value" lines XfaFormReader writes for a Form 8 (field names as MCA's Form8 XFA schema uses them).
    private const string CreationForm = """
        --- Page 1 (native) ---
        CIN: U45203OR1995PLC003982
        ChargeType: CRTN
        ChrgHldrName: Others
        OptionalName: Example Bank Limited
        InstrumentDesc: Deed of Hypothecation dated 07/09/2015
        InstrumentCrtModDate: 2015-09-07
        AmtSecured: 4492800000.00
        NewPropParticlars: Flat No. 305, B Wing, Sunrise Society,
        NewPropParticlars: Survey No. 12/3, Village Baner, Pune 411045
        PropOwnCmp: YES
        PropRegisteredName: Mr. A. Director and Mrs. B. Director
        """;

    private const string ModificationForm = """
        --- Page 1 (native) ---
        ChargeID: 010332396
        InstrumentDesc: Supplemental deed dated 11/01/2016
        InstrumentCrtModDate: 2016-01-11
        AmtSecured: 4492800000.00
        PropParticlars: Extension of the exclusive security (as detailed in Schedule IV of DOH)
        PropOwnCmp: NO
        PropRegisteredName: NIL
        """;

    [Fact]
    public void Read_TakesEveryFieldAsFiled()
    {
        var form = ChargeForms.Read(5, CreationForm)!;

        Assert.Null(form.ChargeId);
        Assert.Equal("Deed of Hypothecation dated 07/09/2015", form.InstrumentDescription);
        Assert.Equal(new DateOnly(2015, 9, 7), form.InstrumentDate);
        Assert.Equal(449.28m, form.AmountSecuredCrore);
        Assert.Equal("Example Bank Limited", form.HolderName); // "Others" is a placeholder code, the name is in OptionalName
        Assert.Equal("Flat No. 305, B Wing, Sunrise Society, Survey No. 12/3, Village Baner, Pune 411045", form.PropertyParticulars);
        // Item 16(a) asks whether any property is NOT registered in the company's name: YES → third-party.
        Assert.False(form.OwnedByCompany);
        Assert.Equal("Mr. A. Director and Mrs. B. Director", form.RegisteredOwner);
    }

    [Fact]
    public void Read_OlderForm_NormalisesTheChargeId_ReadsPropParticlars_AndOwnership()
    {
        var form = ChargeForms.Read(6, ModificationForm)!;

        Assert.Equal("10332396", form.ChargeId);
        Assert.Equal("Extension of the exclusive security (as detailed in Schedule IV of DOH)", form.PropertyParticulars);
        Assert.True(form.OwnedByCompany); // NO → nothing is outside the company's name
        Assert.Null(form.RegisteredOwner); // "NIL"
    }

    [Fact]
    public void Read_IgnoresTextThatIsNotAChargeForm()
    {
        Assert.Null(ChargeForms.Read(7, "--- Page 1 (native) ---\nFormId: Form23\nResolution: RESL\n"));
        Assert.Null(ChargeForms.Read(8, "--- Page 1 (native) ---\nSchedule I: all that flat"));
    }

    private static RocCharge Charge(long id, string number, params (ChargeEventType Type, DateOnly Date, decimal Crore)[] events) => new()
    {
        ChargeId = id, RocChargeNumber = number,
        Events = events.Select(e => new RocChargeEvent { EventType = e.Type, EventDate = e.Date, ChargeAmount = e.Crore }).ToList()
    };

    [Fact]
    public void Link_ByChargeId_AndCreationFormsByDateAndAmount()
    {
        var a = Charge(1, "10332396", (ChargeEventType.Creation, new DateOnly(2012, 1, 11), 300m));
        var b = Charge(2, "100080144", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 449.28m));
        var forms = new[] { ChargeForms.Read(5, CreationForm)!, ChargeForms.Read(6, ModificationForm)! };

        var linked = ChargeForms.Link(forms, [a, b]);

        Assert.Equal(ChargeFormLinkBasis.ChargeId, Assert.Single(linked[1]).Basis);
        Assert.Equal(6, linked[1][0].Form.FilingDocumentId);
        Assert.Equal(ChargeFormLinkBasis.CreationDateAndAmount, Assert.Single(linked[2]).Basis);
        Assert.Equal(5, linked[2][0].Form.FilingDocumentId);
    }

    [Fact]
    public void Link_NeverGuesses_WhenTwoChargesMatchOrNoneDoes()
    {
        var form = ChargeForms.Read(5, CreationForm)!;
        var twins = new[]
        {
            Charge(1, "1001", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 449.28m)),
            Charge(2, "1002", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 449.28m))
        };
        var otherAmount = new[] { Charge(3, "1003", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 449.29m)) };
        var onlyModified = new[] { Charge(4, "1004", (ChargeEventType.Modification, new DateOnly(2015, 9, 7), 449.28m)) };

        Assert.Empty(ChargeForms.Link([form], twins));
        Assert.Empty(ChargeForms.Link([form], otherAmount));
        Assert.Empty(ChargeForms.Link([form], onlyModified));
        Assert.Empty(ChargeForms.Link([ChargeForms.Read(6, ModificationForm)!], otherAmount)); // its charge ID isn't in the register
    }
}

/// <summary>#364: <see cref="ChargeForms.LoadAsync"/> against real SQL — only the authoritative batch's XFA-extracted
/// documents are read.</summary>
public sealed class ChargeFormsLoadTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "charge-forms-" + Guid.NewGuid().ToString("N"));

    private static MCAROC_Analysis.Data.AppDbContext CreateContext() =>
        new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MCAROC_Analysis.Data.AppDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
        Directory.CreateDirectory(_dir);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingBackfill() : ChargeFormBackfill(null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<ChargeFormBackfill>.Instance)
    {
        public readonly List<long> Requested = [];
        public override void Request(long batchId) => Requested.Add(batchId);
    }

    [Fact]
    public async Task LoadAsync_ReadsSavedFormFields_AndBackfillsDocumentsThatHaveNone()
    {
        await using var db = CreateContext();
        var request = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Forms Co", RequestNumber = $"CF-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var batch = new McaFilingBatch { RequestId = request.RequestId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "SRN-1", NestedZipName = "n.zip" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        const string Datasets = """
            <xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/"><xfa:data><Form8_Dtls><Form8>
              <InstrumentDesc>Deed of Hypothecation</InstrumentDesc><InstrumentCrtModDate>2015-09-07</InstrumentCrtModDate>
              <AmtSecured>4492800000.00</AmtSecured><NewPropParticlars>Plot No. 45, Sector 63, Noida</NewPropParticlars>
              <PropOwnCmp>YES</PropOwnCmp><PropRegisteredName>Mr. A. Director</PropRegisteredName>
            </Form8></Form8_Dtls></xfa:data></xfa:datasets>
            """;
        // A Form 8 that renders visible text AND embeds its form data (most of them do), extracted before fields were saved.
        var pdfPath = Path.Combine(_dir, "form8.pdf");
        await File.WriteAllBytesAsync(pdfPath, XfaFormReaderTests.MultiPagePdf(["Form 8 Particulars of the charge, rendered page text for the viewer."], Datasets));
        var textPath = Path.Combine(_dir, "form8.txt");
        await File.WriteAllTextAsync(textPath, "--- Page 1 (native) ---\nForm 8 Particulars of the charge");
        var doc = new McaFilingDocument { FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId, OriginalFileName = "Form 8.pdf",
            StoragePath = pdfPath, ExtractedTextPath = textPath, FileHash = Guid.NewGuid().ToString("N"), ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            Category = FilingCategory.Charge, TextExtractionMethod = TextExtractionMethod.Native, UpdatedAt = DateTime.UtcNow };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var charge = new RocCharge { ChargeId = 42, RequestId = request.RequestId, RocChargeNumber = "100080144",
            Events = [new RocChargeEvent { EventType = ChargeEventType.Creation, EventDate = new DateOnly(2015, 9, 7), ChargeAmount = 449.28m }] };
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });

        // 1. No saved fields yet: nothing is read from the PDF on the page load; the batch is handed to the backfill.
        var recording = new RecordingBackfill();
        Assert.Empty(await ChargeForms.LoadAsync(db, request.RequestId, [charge], CancellationToken.None, cache, recording));
        Assert.Equal([batch.BatchId], recording.Requested);

        // 2. The backfill reads the PDF's form data and saves it beside the text.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddScoped(_ => CreateContext()).BuildServiceProvider();
        var backfill = new ChargeFormBackfill(services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), cache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChargeFormBackfill>.Instance);
        Assert.Equal(1, await backfill.RunAsync(batch.BatchId, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_dir, "form8.xfa.json")));

        // 3. The next load links it — and asks for nothing more.
        recording.Requested.Clear();
        var form = Assert.Single((await ChargeForms.LoadAsync(db, request.RequestId, [charge], CancellationToken.None, cache, recording))[42]);
        Assert.Empty(recording.Requested);
        Assert.Equal(doc.FilingDocumentId, form.Form.FilingDocumentId);
        Assert.Equal("Plot No. 45, Sector 63, Noida", form.Form.PropertyParticulars);
        Assert.False(form.Form.OwnedByCompany);
        Assert.Equal("Mr. A. Director", form.Form.RegisteredOwner);
    }

    [Fact]
    public async Task Extraction_SavesFormFieldsBesideTheText()
    {
        var pdfPath = Path.Combine(_dir, "fresh.pdf");
        await File.WriteAllBytesAsync(pdfPath, XfaFormReaderTests.MultiPagePdf(["Rendered Form 8 page text for the viewer, which is comfortably longer than the native-text threshold."],
            """<xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/"><xfa:data><F><ChargeID>10332396</ChargeID></F></xfa:data></xfa:datasets>"""));

        var result = await new MCAROC_Analysis.Services.McaFilings.PdfTextExtractor(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MCAROC_Analysis.Services.McaFilings.PdfTextExtractor>.Instance, "")
            .ExtractAsync(pdfPath, _dir, CancellationToken.None);

        Assert.Equal(TextExtractionMethod.Native, result.Method); // real page text is kept as it is
        Assert.Equal("10332396", Assert.Single(result.XfaFields!).Value);
    }
}
