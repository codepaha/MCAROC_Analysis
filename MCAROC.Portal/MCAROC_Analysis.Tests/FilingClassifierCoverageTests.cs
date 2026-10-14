using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#499: the classifier's coverage of the documents a banker asks for. Every file name below is from the dev corpus (six companies, 2026-10-14), where
/// these were unclassified, placed only by their folder, or mislabelled. Measured there: unclassified 492 → 112 of 2,053.</summary>
public class FilingClassifierCoverageTests
{
    private static ClassificationResult Name(string fileName, string? text = null) =>
        FilingClassifier.Classify("Incorporation and Other Documents", "Other Documents Attachment", fileName, text);

    [Theory]
    [InlineData("Certificate of Registration of Charge-12032019.pdf", FilingCategory.Charge, "Certificate of Registration of Charge")]
    [InlineData("Certificate of Satisfaction of Charge-25102018.pdf", FilingCategory.Charge, "Certificate of Satisfaction of Charge")]
    [InlineData("0b1e2d3c4f5a6b7c8d9e0f1a2b3c4d5ev1.DUP1-DOH-2.pdf", FilingCategory.Charge, "Instrument of Charge")]
    [InlineData("Form_CHG-1-12032019.pdf", FilingCategory.Charge, "Form CHG-1")]
    [InlineData("433152664-Form-PAS-5-3.pdf", FilingCategory.Compliance, "List of Allottees (PAS-5)")]
    [InlineData("268449450listofallottee-1.pdf", FilingCategory.Compliance, "List of Allottees (PAS-5)")]
    [InlineData("List of share holders, debenture holders-2.pdf", FilingCategory.Compliance, "List of Shareholders / Directors")]
    [InlineData("Form SH-7-01022021.pdf", FilingCategory.Compliance, "Form SH-7")]
    [InlineData("Form DPT-3-30062022.pdf", FilingCategory.Compliance, "Form DPT-3")]
    [InlineData("Form MSME FORM I-02052024_signed.pdf", FilingCategory.Compliance, "Form MSME-1")]
    [InlineData("Declaration of the appointee director, Managing director, in Form No. DIR-2;-12032019.pdf", FilingCategory.Compliance, "Director Appointment / Cessation (DIR-12 attachment)")]
    [InlineData("Evidence of cessation;-25102018.pdf", FilingCategory.Compliance, "Director Appointment / Cessation (DIR-12 attachment)")]
    [InlineData("Copy of financial staements duly authenticated as per section 134 (including boards report, auditors report and other documents)-1.pdf", FilingCategory.Financial, "Financial Statements")]
    [InlineData("Directors report as per section 134(3)-1.pdf", FilingCategory.Financial, "Directors' Report")]
    [InlineData("Form AOC-4 CFS-1.pdf", FilingCategory.Financial, "Form AOC-4")]
    [InlineData("AOC-2-3.pdf", FilingCategory.Financial, "Form AOC-2")]
    [InlineData("5fea84a15e53c3c078660417981ea728v1-Form23AC-201214 for the FY ending on-310314.pdf", FilingCategory.Financial, "Form 23AC/23ACA (XBRL)")]
    [InlineData("82ab66d59991d4479f0bd6654077ce96v1-Form66-171114 for the FY ending on-310314.pdf", FilingCategory.Compliance, "Form 66 (Compliance Certificate)")]
    [InlineData("EGM-SR-Amendment-of-AOA-1.pdf", FilingCategory.Compliance, "Shareholder Resolution")]   // a resolution altering the AoA, not the AoA
    [InlineData("475056662-BR-allotment-six-sense-2000ccd-1.pdf", FilingCategory.Compliance, "Board Resolution")]   // was labelled PAS-3
    [InlineData("Certificate of Incorporation pursuant to change of name.pdf", FilingCategory.Constitutional, "Certificate of Incorporation on Change of Name")]
    [InlineData("523174379-aoasubcribersheet-1.pdf", FilingCategory.Constitutional, "AoA")]
    [InlineData("Order of condonation of delay-1.pdf", FilingCategory.Compliance, "Condonation of Delay Order")]
    [InlineData("CP-12-Valuation-Report-1.pdf", FilingCategory.Compliance, "Valuation Report")]
    public void A_document_type_is_read_from_its_file_name(string fileName, FilingCategory category, string formType)
    {
        var r = Name(fileName);
        Assert.Equal((category, formType), (r.Category, r.FormType));
        Assert.Equal("FilenameKeyword", r.Method);
    }

    [Theory]
    [InlineData("xxxv1-Form PAS-3-171016.pdf", "Form PAS-3")]
    [InlineData("xxxv1-Form MGT-14.pdf", "Form MGT-14")]
    [InlineData("xxxv1-MoA - Memorandum of Association-190614.pdf", "MoA")]
    [InlineData("57684e71010d815a8aaa3b89b7a1f3f1v1-Certificate of Incorporation.pdf", "Certificate of Incorporation")]
    public void Types_that_were_right_stay_right(string fileName, string formType) => Assert.Equal(formType, Name(fileName).FormType);

    [Fact]
    public void A_short_code_inside_a_word_is_not_that_code() =>
        Assert.NotEqual("MoA", Name("Namoa Infra Board Note.pdf").FormType);

    [Fact]
    public void A_resolution_that_mentions_the_memorandum_in_its_body_is_a_resolution_not_an_MoA()
    {
        var text = "CERTIFIED TRUE COPY OF THE SPECIAL RESOLUTION PASSED AT THE EXTRA-ORDINARY GENERAL MEETING ... " + new string('x', 800)
            + " RESOLVED THAT the Memorandum of Association of the Company be altered ...";
        var r = Name("Optional Attachment-(1).pdf", text);
        Assert.Equal("Shareholder Resolution", r.FormType);
        Assert.Equal("TextHeader", r.Method);
    }

    [Fact]
    public void An_MoA_is_recognised_from_the_top_of_its_text()
    {
        var r = Name("Optional Attachment-(2).pdf", "THE COMPANIES ACT, 2013\nCOMPANY LIMITED BY SHARES\nMEMORANDUM OF ASSOCIATION\nOF COASTAL PROJECTS LIMITED");
        Assert.Equal("MoA", r.FormType);
    }

    [Fact]
    public void A_charge_certificate_attachment_is_recognised_from_its_title()
    {
        var r = Name("Optional Attachment-(1).pdf", "Government of India\nMinistry of Corporate Affairs\nCertificate of Registration of Charge\n[Pursuant to section 77(3)...]");
        Assert.Equal((FilingCategory.Charge, "Certificate of Registration of Charge"), (r.Category, r.FormType));
    }

    [Fact]
    public void Wording_far_into_the_body_does_not_decide_the_type()
    {
        var body = new string('x', FilingClassifier.TitleRegionChars + 100) + " certificate of registration of charge";
        Assert.NotEqual("TextHeader", Name("Optional Attachment-(1).pdf", body).Method);
    }
}

/// <summary>#499: stored documents are classified again when the rules change, labels only.</summary>
public class FilingReclassifierTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Stored_documents_get_the_current_labels_on_document_and_chunks_and_nothing_else_changes()
    {
        long docId, inProgressId;
        var textPath = Path.Combine(Path.GetTempPath(), $"reclassify-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(textPath, "Ministry of Corporate Affairs\nCertificate of Registration of Charge\n");
        try
        {
            await using (var db = Ctx())
            {
                var client = new Client { ClientCode = "RC" + Guid.NewGuid().ToString("N")[..8], ClientName = "Reclassify", CreatedDate = DateTime.UtcNow };
                var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "Reclassify Co", RequestNumber = $"RC-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
                var source = new RequestDocument { Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip", StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow };
                db.RequestDocuments.Add(source);
                await db.SaveChangesAsync();
                var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = source.DocumentId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow };
                db.McaFilingBatches.Add(batch);
                await db.SaveChangesAsync();
                var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "S" + Guid.NewGuid().ToString("N")[..8], NestedZipName = "1.zip", OuterCategoryFolder = "Some Folder" };
                db.McaFilings.Add(filing);
                await db.SaveChangesAsync();
                McaFilingDocument Doc(FilingDocumentProcessingStatus status) => new()
                {
                    FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId, OriginalFileName = "Optional Attachment-(1).pdf",
                    StoragePath = @"C:\fake\a.pdf", FileHash = Guid.NewGuid().ToString("N"), ExtractedTextPath = textPath, ProcessingStatus = status,
                    Category = FilingCategory.Unclassified, ClassificationMethod = "NoMatch", AiExtractionStatus = AiExtractionStatus.NotApplicable,
                    ManualReviewRequired = true, ManualReviewReason = "Could not classify this document.", UpdatedAt = DateTime.UtcNow
                };
                var done = Doc(FilingDocumentProcessingStatus.Completed);
                var running = Doc(FilingDocumentProcessingStatus.TextExtracting);
                db.McaFilingDocuments.AddRange(done, running);
                await db.SaveChangesAsync();
                db.DocumentChunks.Add(new DocumentChunk
                {
                    RequestId = request.RequestId, FilingDocumentId = done.FilingDocumentId, FilingId = filing.FilingId, BatchId = batch.BatchId, Srn = filing.Srn,
                    Category = FilingCategory.Unclassified, ChunkText = "Certificate of Registration of Charge", ChunkIndex = 0, PageNumber = 1, CreatedDate = DateTime.UtcNow,
                    Embedding = new Microsoft.Data.SqlTypes.SqlVector<float>(new float[768])
                });
                await db.SaveChangesAsync();
                docId = done.FilingDocumentId;
                inProgressId = running.FilingDocumentId;
            }

            await using (var db = Ctx())
                while ((await new FilingReclassifier(db, NullLogger<FilingReclassifier>.Instance).ReclassifyBatchAsync(500, default)).Seen > 0) { }

            await using var verify = Ctx();
            var doc = await verify.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == docId);
            Assert.Equal((FilingCategory.Charge, "Certificate of Registration of Charge"), (doc.Category, doc.FormType));
            Assert.Equal(FilingClassifier.Version, doc.ClassifierVersion);
            Assert.False(doc.ManualReviewRequired);
            Assert.Equal(AiExtractionStatus.NotApplicable, doc.AiExtractionStatus);   // no extraction is set off by a new label
            var chunk = await verify.DocumentChunks.AsNoTracking().SingleAsync(c => c.FilingDocumentId == docId);
            Assert.Equal((FilingCategory.Charge, "Certificate of Registration of Charge"), (chunk.Category, chunk.FormType));
            var stillRunning = await verify.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == inProgressId);
            Assert.Equal(0, stillRunning.ClassifierVersion);                                 // left to the processor
        }
        finally { File.Delete(textPath); }
    }
}
