using System.Security.Cryptography;
using System.Text;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Tests;

/// <summary>#377: every filing document tied to its charge — open or satisfied — by exact evidence only.</summary>
public class ChargeDocumentLinkerTests
{
    private static RocCharge Charge(long id, string number, params (ChargeEventType Type, DateOnly Date, decimal Crore)[] events) => new()
    {
        ChargeId = id, RocChargeNumber = number,
        Events = events.Select(e => new RocChargeEvent { EventType = e.Type, EventDate = e.Date, ChargeAmount = e.Crore }).ToList()
    };

    private static List<XfaField> Fields(params (string Name, string Value)[] fields) => fields.Select(f => new XfaField("F/" + f.Name, f.Name, f.Value)).ToList();

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void A_satisfaction_form_links_by_the_charge_id_it_states_and_an_id_outside_the_register_links_nothing()
    {
        var charges = new[] { Charge(1, "010215822") };
        var docs = new[]
        {
            new ChargeLinkInput(10, "H10", "Form 17.pdf", Fields(("ChargeID", "10215822"), ("SatisfactionDate", "2019-03-31"))),
            new ChargeLinkInput(11, "H11", "Form 17.pdf", Fields(("ChargeID", "99999999")))
        };

        var link = Assert.Single(ChargeDocumentLinker.Compute(docs, charges));
        Assert.Equal(("10215822", 10L, ChargeDocumentLinkMethod.FormChargeId), (link.ChargeNumber, link.FilingDocumentId, link.Method));
    }

    [Fact]
    public void A_creation_form_links_by_a_unique_date_and_amount_but_a_modification_without_an_id_does_not()
    {
        var charges = new[] { Charge(1, "100080144", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 449.28m)) };
        (string, string)[] same = [("InstrumentDesc", "Deed of Hypothecation"), ("InstrumentCrtModDate", "2015-09-07"), ("AmtSecured", "4492800000.00")];
        var docs = new[]
        {
            new ChargeLinkInput(10, "H10", "Form 8.pdf", Fields([("ChargeType", "CRTN"), .. same])),
            new ChargeLinkInput(11, "H11", "Form 8.pdf", Fields([("ChargeType", "MDFN"), .. same]))
        };

        var link = Assert.Single(ChargeDocumentLinker.Compute(docs, charges));
        Assert.Equal((10L, ChargeDocumentLinkMethod.CreationDateAndAmount), (link.FilingDocumentId, link.Method));
    }

    [Fact]
    public void Attachments_link_through_the_form_that_embeds_them_and_a_shared_one_links_to_every_such_charge()
    {
        var charges = new[] { Charge(1, "1001"), Charge(2, "1002") };
        var docs = new[]
        {
            new ChargeLinkInput(10, "HF1", "Form 8.pdf", Fields(("ChargeID", "1001")), [new("deed.pdf", Hash("deed")), new("mra.pdf", Hash("mra"))]),
            new ChargeLinkInput(11, "HF2", "Form 8.pdf", Fields(("ChargeID", "1002")), [new("mra.pdf", Hash("mra"))]),
            new ChargeLinkInput(20, Hash("deed").ToLowerInvariant(), "Instrument(s) of creation or modification of charge.pdf"),
            new ChargeLinkInput(21, Hash("mra"), "Optional Attachment-(1).pdf"),
            new ChargeLinkInput(22, Hash("unrelated"), "Optional Attachment-(2).pdf")
        };

        var links = ChargeDocumentLinker.Compute(docs, charges).Where(l => l.Method == ChargeDocumentLinkMethod.EmbeddedAttachment).ToList();

        Assert.Equal(
            [("1001", 20L, (long?)10L), ("1001", 21L, 10L), ("1002", 21L, 11L)],
            links.Select(l => (l.ChargeNumber, l.FilingDocumentId, l.LinkedFromDocumentId)).ToList());
    }

    [Fact]
    public void An_attachment_of_a_form_that_is_not_linked_stays_unlinked()
    {
        var charges = new[] { Charge(1, "1001", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 10m)), Charge(2, "1002", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 10m)) };
        var docs = new[]
        {
            // Two charges match the creation date and amount: ambiguous, so neither the form nor its deed is linked.
            new ChargeLinkInput(10, "HF", "Form 8.pdf", Fields(("ChargeType", "CRTN"), ("InstrumentDesc", "Deed"), ("InstrumentCrtModDate", "2015-09-07"), ("AmtSecured", "100000000")),
                [new("deed.pdf", Hash("deed"))]),
            new ChargeLinkInput(20, Hash("deed"), "deed.pdf")
        };

        Assert.Empty(ChargeDocumentLinker.Compute(docs, charges));
    }

    [Fact]
    public void A_file_name_links_only_when_the_documents_own_form_data_names_no_other_charge()
    {
        var charges = new[] { Charge(1, "10215822"), Charge(2, "10590447") };
        var docs = new[]
        {
            new ChargeLinkInput(10, "H10", "d826v1-Form CHG-1-200415-ChargeId-10215822.pdf"),
            new ChargeLinkInput(11, "H11", "b0acv1-Form CHG-1-150915-ChargeId-10215822.pdf", Fields(("ChargeID", "10590447")))
        };

        Assert.Equal(
            [("10215822", 10L, ChargeDocumentLinkMethod.FileName), ("10590447", 11L, ChargeDocumentLinkMethod.FormChargeId)],
            ChargeDocumentLinker.Compute(docs, charges).Select(l => (l.ChargeNumber, l.FilingDocumentId, l.Method)).ToList());
    }

    /// <summary>PR #378 review: a creation form matched to charge 1001 by its date and amount, whose file name names charge 1002,
    /// must not also link to 1002 — nor may its embedded deed. Form evidence wins; the contradictory name links nothing.</summary>
    [Fact]
    public void A_file_name_naming_another_charge_never_adds_a_link_to_a_form_matched_by_date_and_amount_or_to_its_attachments()
    {
        var charges = new[]
        {
            Charge(1, "1001", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 10m)),
            Charge(2, "1002")
        };
        var docs = new[]
        {
            new ChargeLinkInput(10, "HF", "Form 8-ChargeId-1002.pdf",
                Fields(("ChargeType", "CRTN"), ("InstrumentDesc", "Deed"), ("InstrumentCrtModDate", "2015-09-07"), ("AmtSecured", "100000000")),
                [new("deed.pdf", Hash("deed"))]),
            new ChargeLinkInput(20, Hash("deed"), "deed.pdf")
        };

        Assert.Equal(
            [("1001", 10L, ChargeDocumentLinkMethod.CreationDateAndAmount), ("1001", 20L, ChargeDocumentLinkMethod.EmbeddedAttachment)],
            ChargeDocumentLinker.Compute(docs, charges).Select(l => (l.ChargeNumber, l.FilingDocumentId, l.Method)).ToList());
    }

    [Fact]
    public void A_file_name_that_agrees_with_the_form_evidence_adds_nothing_and_a_filename_only_form_still_links()
    {
        var charges = new[] { Charge(1, "1001", (ChargeEventType.Creation, new DateOnly(2015, 9, 7), 10m)), Charge(2, "1002") };
        var docs = new[]
        {
            // Agrees with its own date-and-amount match: one link, by the stronger evidence.
            new ChargeLinkInput(10, "H10", "Form 8-ChargeId-1001.pdf",
                Fields(("ChargeType", "CRTN"), ("InstrumentDesc", "Deed"), ("InstrumentCrtModDate", "2015-09-07"), ("AmtSecured", "100000000"))),
            // No form data to contradict it: the name alone links it.
            new ChargeLinkInput(11, "H11", "Optional Attachment-ChargeId-1002.pdf")
        };

        Assert.Equal(
            [("1001", 10L, ChargeDocumentLinkMethod.CreationDateAndAmount), ("1002", 11L, ChargeDocumentLinkMethod.FileName)],
            ChargeDocumentLinker.Compute(docs, charges).Select(l => (l.ChargeNumber, l.FilingDocumentId, l.Method)).ToList());
    }

    [Theory]
    [InlineData("6367d1ef14743470be398e84293cf983v1-Form CHG-1-131015.pdf", "Form CHG-1-131015")]
    [InlineData("693f11218eff5222ed57c9ded67fc1d9v1.DUP0-Form CHG-1-131015.pdf", "Form CHG-1-131015")]
    [InlineData("Charge Documents/Form 17.pdf", "Form 17")]
    public void DisplayName_drops_the_export_prefix_and_extension(string original, string expected) =>
        Assert.Equal(expected, ChargeDocumentLinker.DisplayName(original));

    [Fact]
    public void EmbeddedFiles_hashes_equal_the_attachment_bytes()
    {
        var pdf = XfaFormReaderTests.MultiPagePdf(["Form 8 rendered page text, long enough to be native text."], null, [("deed.pdf", "DEED-BYTES"), ("mra.pdf", "MRA")]);
        using var document = PdfDocument.Open(pdf);

        Assert.Equal(new[] { Hash("DEED-BYTES"), Hash("MRA") }.Order(), EmbeddedFiles.Read(document).Select(f => f.Sha256).Order());
    }
}

public sealed class ChargeDocumentLinkBuilderTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "charge-doc-links-" + Guid.NewGuid().ToString("N"));

    private static MCAROC_Analysis.Data.AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MCAROC_Analysis.Data.AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

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

    private sealed class RecordingBuilder() : ChargeDocumentLinkBuilder(null!, null!, NullLogger<ChargeDocumentLinkBuilder>.Instance)
    {
        public readonly List<long> Requested = [];
        public override void Request(long batchId) => Requested.Add(batchId);
    }

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task Builds_links_for_open_and_satisfied_charges_and_rebuilds_only_when_the_evidence_changes()
    {
        await using var db = CreateContext();
        var request = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Links Co", RequestNumber = $"CL-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        RocCharge Charge(string number, string status, ChargeEventType type, DateOnly date, decimal crore) => new()
        {
            RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, RocChargeNumber = number, ChargeStatus = status,
            Events = [new RocChargeEvent { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, SerialNumber = "1", EventType = type, EventDate = date, ChargeAmount = crore }]
        };
        db.RocCharges.AddRange(
            Charge("100080144", "Open", ChargeEventType.Creation, new DateOnly(2015, 9, 7), 449.28m),
            Charge("10215822", "Satisfied", ChargeEventType.Satisfaction, new DateOnly(2019, 3, 31), 25m));
        var batch = new McaFilingBatch { RequestId = request.RequestId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "SRN-1", NestedZipName = "n.zip" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        McaFilingDocument Doc(string name, string hash, string? storage = null, string? text = null) => new()
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId, OriginalFileName = name, StoragePath = storage ?? "",
            ExtractedTextPath = text, FileHash = hash, ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            Category = FilingCategory.Charge, TextExtractionMethod = TextExtractionMethod.Native, UpdatedAt = DateTime.UtcNow
        };

        // A creation Form 8 extracted before form data was saved: the builder's backfill reads its XFA fields and the deed
        // embedded in it from the PDF.
        var form8Pdf = Path.Combine(_dir, "form8.pdf");
        await File.WriteAllBytesAsync(form8Pdf, XfaFormReaderTests.MultiPagePdf(["Form 8 rendered page text, long enough to be native text."],
            """<xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/"><xfa:data><F><ChargeType>CRTN</ChargeType><InstrumentDesc>Deed of Hypothecation</InstrumentDesc><InstrumentCrtModDate>2015-09-07</InstrumentCrtModDate><AmtSecured>4492800000.00</AmtSecured></F></xfa:data></xfa:datasets>""",
            [("deed.pdf", "DEED-BYTES")]));
        var form8Text = Path.Combine(_dir, "form8.txt");
        await File.WriteAllTextAsync(form8Text, "--- Page 1 (native) ---\nForm 8");
        var form8 = Doc("a8b5851ca387b0b9bc2874914c81fb22v1-Form 8.pdf", Guid.NewGuid().ToString("N"), form8Pdf, form8Text);
        var deed = Doc("e2038489e97159590903527feb448e16v1-Instrument(s) of creation or modification of charge.pdf", Hash("DEED-BYTES"));
        // A satisfaction form whose fields were saved at extraction.
        var form17Text = Path.Combine(_dir, "form17.txt");
        await File.WriteAllTextAsync(form17Text, "--- Page 1 (native) ---\nChargeID: 10215822");
        await XfaFormReader.WriteSidecarAsync(form17Text, [new("F/ChargeID", "ChargeID", "10215822")], CancellationToken.None);
        await EmbeddedFiles.WriteSidecarAsync(form17Text, [], CancellationToken.None);
        var form17 = Doc("Form 17.pdf", Guid.NewGuid().ToString("N"), text: form17Text);
        db.McaFilingDocuments.AddRange(form8, deed, form17);
        await db.SaveChangesAsync();

        var scopes = new ServiceCollection().AddScoped(_ => CreateContext()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var builder = new ChargeDocumentLinkBuilder(scopes, new ChargeFormBackfill(scopes, NullLogger<ChargeFormBackfill>.Instance), NullLogger<ChargeDocumentLinkBuilder>.Instance);
        Assert.Equal(3, await builder.RunAsync(batch.BatchId, CancellationToken.None));

        var recording = new RecordingBuilder();
        var documents = await ChargeDocumentLinker.LoadAsync(db, request.RequestId, run.IngestionRunId, CancellationToken.None, recording);
        Assert.Empty(recording.Requested); // built from the current evidence: nothing to rebuild
        Assert.Equal(
            [(form8.FilingDocumentId, ChargeDocumentLinkMethod.CreationDateAndAmount, (long?)null, "Form 8"),
             (deed.FilingDocumentId, ChargeDocumentLinkMethod.EmbeddedAttachment, form8.FilingDocumentId, "Instrument(s) of creation or modification of charge")],
            documents["100080144"].Select(d => (d.FilingDocumentId, d.Method, d.LinkedFromDocumentId, d.DisplayName)).ToList());
        Assert.Equal(form17.FilingDocumentId, Assert.Single(documents["10215822"]).FilingDocumentId);

        // A new document in the batch changes the evidence: the next load asks for a rebuild.
        db.McaFilingDocuments.Add(Doc("Optional Attachment.pdf", Guid.NewGuid().ToString("N")));
        await db.SaveChangesAsync();
        await using var fresh = CreateContext();
        await ChargeDocumentLinker.LoadAsync(fresh, request.RequestId, run.IngestionRunId, CancellationToken.None, recording);
        Assert.Equal([batch.BatchId], recording.Requested);
    }
}
