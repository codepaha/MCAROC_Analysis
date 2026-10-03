using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>#364 part 2: property passages quoted from charge documents are shown only if they occur, word for word, in the
/// document's own text — and carry the page they start on, worked out from the document's page markers.</summary>
public class ChargeInstrumentAiTests
{
    private const string Document =
        "--- Page 1 (native) ---\nDEED OF MORTGAGE made at Pune\n" +
        "--- Page 2 (OCR) ---\nTHE SCHEDULE ABOVE REFERRED TO\nFlat No. 305, B Wing, Sunrise Society,\nSurvey No. 12/3, Village Baner,\n" +
        "--- Page 3 (OCR) ---\nTaluka Haveli, District Pune 411045 admeasuring 650 sq ft.\nWitness: A. Person\n";

    private static string Json(params (string Kind, string Text)[] passages) =>
        System.Text.Json.JsonSerializer.Serialize(new { passages = passages.Select(p => new { kind = p.Kind, text = p.Text }) });

    [Fact]
    public void A_verbatim_passage_is_kept_with_the_page_it_starts_on_and_one_that_runs_over_a_page_reports_both()
    {
        var v = ChargeInstrumentAi.Validate(Json(("Schedule",
            "Flat No. 305, B Wing, Sunrise Society, Survey No. 12/3, Village Baner, Taluka Haveli, District Pune 411045 admeasuring 650 sq ft.")), Document);

        var p = Assert.Single(v.Result!.Passages);
        Assert.Equal((InstrumentPassageKind.Schedule, 2, 3), (p.Kind, p.Page, p.EndPage));
        Assert.Empty(v.Rejected);
    }

    /// <summary>Owner rule: flat 305 is not flat 315. A passage with any word the document does not contain is dropped whole.</summary>
    [Theory]
    [InlineData("Flat No. 315, B Wing, Sunrise Society")] // a different flat number
    [InlineData("Flat No. 305, B Wing, Sunrise Society, Survey No. 12/4")] // a different survey number
    [InlineData("a flat in Baner, Pune")] // a paraphrase
    public void A_passage_that_is_not_a_verbatim_quote_is_dropped(string invented)
    {
        var v = ChargeInstrumentAi.Validate(Json(("Schedule", invented), ("Schedule", "Survey No. 12/3, Village Baner")), Document);

        Assert.Equal("Survey No. 12/3, Village Baner", Assert.Single(v.Result!.Passages).Text);
        Assert.Contains(v.Rejected, r => r.Contains("not a verbatim quote"));
    }

    /// <summary>PR #385 review: a quote must not stop inside a longer identifier. "Flat No. 30" is not a quote of "Flat No. 305", and a
    /// quote that starts inside one, or stops at the slash, hyphen, dot or letter that continues it, is part of a different identifier.</summary>
    [Theory]
    [InlineData("Flat No. 305, B Wing, Sunrise Society", "Flat No. 30")]            // prefix of 305
    [InlineData("Flat No. 305, B Wing, Sunrise Society", "No. 305, B Wing, Sunrise Societ")] // ends inside a word
    [InlineData("Flat No. 305, B Wing, Sunrise Society", "at No. 305")]              // starts inside a word
    [InlineData("Survey No. 12/3, Village Baner", "Survey No. 12")]                  // slash continues the number
    [InlineData("Survey No. 12-3, Village Baner", "Survey No. 12")]                  // hyphen continues it
    [InlineData("Plot No. 30A, Sector 63", "Plot No. 30")]                           // suffix letter
    [InlineData("Total area 12.5 sq mtrs", "Total area 12")]                                     // decimal point continues it
    [InlineData("Unit No. A-305, Tower B", "05, Tower B")]                           // starts inside 305
    [InlineData("Unit No. A-305, Tower B", "305, Tower B")]                          // starts after the hyphen of A-305
    [InlineData("Sy No. 403/2A3, 404/B/2C", "Sy No. 403/2")]                         // ends inside the 2A3 parcel suffix
    [InlineData("Survey No. 12/3, Village Baner", "Survey No. 12/")]                 // ends on the slash itself
    [InlineData("Survey No. 12 / 3, Village Baner", "Survey No. 12")]                // spaced slash
    [InlineData("Survey No. 12/ 3, Village Baner", "Survey No. 12/")]                // space after the slash
    [InlineData("Survey No. 12/\n3, Village Baner", "Survey No. 12/")]               // line break after the slash
    [InlineData("Survey No. 12\n/3, Village Baner", "Survey No. 12")]                // line break before the slash
    [InlineData("Survey No. 12 / 3, Village Baner", "3, Village Baner")]             // starts after a spaced slash
    [InlineData("Plot No. 12 - 3, Sector 5", "Plot No. 12")]                                 // spaced hyphen between numbers
    [InlineData("Unit No. A-305, Tower B", "-305, Tower B")]                         // starts on the hyphen itself
    public void A_quote_that_cuts_an_identifier_is_not_found(string source, string cut)
    {
        var v = ChargeInstrumentAi.Validate(Json(("Schedule", cut)), source);

        Assert.True(v.IsAccepted);
        Assert.Empty(v.Result!.Passages);
        Assert.Contains(v.Rejected, r => r.Contains("not a verbatim quote"));
    }

    /// <summary>The same boundary rule must not reject honest quotes: ending at a comma, full stop or the end of the text, starting after
    /// punctuation, spanning pages and lines, and quoting a whole identifier in full.</summary>
    [Theory]
    [InlineData("Flat No. 305, B Wing, Sunrise Society", "Flat No. 305")]
    [InlineData("Flat No. 305, B Wing, Sunrise Society", "305, B Wing")]
    [InlineData("Flat No. 305. B Wing", "Flat No. 305")]
    [InlineData("Survey No. 12/3, Village Baner", "Survey No. 12/3")]
    [InlineData("Survey No. 12 / 3, Village Baner", "Survey No. 12 / 3")]
    [InlineData("Survey No. 12/\n3, Village Baner", "Survey No. 12/ 3")]
    [InlineData("Sunrise Society - B Wing, Pune", "Sunrise Society")]                 // a hyphen between words is not an identifier joiner
    [InlineData("Village Baner / Taluka Haveli", "Village Baner")]                   // a slash after a word and a space before a word
    [InlineData("Survey No. 12/3, Village Baner", "12/3, Village Baner")]
    [InlineData("Plot No. 30A, Sector 63", "Plot No. 30A")]
    [InlineData("Sy Nos. 403/2A3, 404/B/2C, and 404/A3", "Sy Nos. 403/2A3, 404/B/2C, and 404/A3")]
    [InlineData("Address: (Flat No. 305), B Wing", "Flat No. 305")]
    public void An_honest_quote_on_identifier_boundaries_is_kept(string source, string quote)
    {
        var v = ChargeInstrumentAi.Validate(Json(("Schedule", quote)), source);

        Assert.Equal(quote, Assert.Single(v.Result!.Passages).Text);
    }

    [Fact]
    public void A_later_whole_occurrence_is_found_when_an_earlier_one_cuts_an_identifier()
    {
        // "Flat No. 30" cuts 305 on its first appearance but is whole on its second.
        var v = ChargeInstrumentAi.Validate(Json(("Schedule", "Flat No. 30")), "Flat No. 305 and also Flat No. 30 in Wing B");

        Assert.Equal("Flat No. 30", Assert.Single(v.Result!.Passages).Text);
    }

    [Fact]
    public void A_null_entry_in_the_answer_is_a_malformed_answer_not_an_exception()
    {
        var v = ChargeInstrumentAi.Validate("""{"passages":[null,{"kind":"Schedule","text":"Village Baner"}]}""", Document);

        Assert.False(v.IsAccepted);
        Assert.Null(v.Result);
        Assert.Contains("passages[0] is null", v.FailureReason);
    }

    [Fact]
    public void Whitespace_and_case_do_not_matter_and_copied_page_markers_are_removed()
    {
        var v = ChargeInstrumentAi.Validate(Json(("Particulars",
            "survey no. 12/3,   VILLAGE BANER,\n--- Page 3 (OCR) ---\nTaluka Haveli")), Document);

        var p = Assert.Single(v.Result!.Passages);
        Assert.Equal("survey no. 12/3, VILLAGE BANER, Taluka Haveli", p.Text);
        Assert.Equal((2, 3), (p.Page, p.EndPage));
    }

    [Fact]
    public void The_same_passage_quoted_twice_is_kept_once_and_an_unsupported_kind_or_a_stub_is_dropped()
    {
        var v = ChargeInstrumentAi.Validate(Json(
            ("Schedule", "Village Baner"), ("Schedule", "village baner"), ("Mystery", "Taluka Haveli"), ("Schedule", "Pune")), Document);

        Assert.Equal("Village Baner", Assert.Single(v.Result!.Passages).Text);
        Assert.Equal(2, v.Rejected.Count);
    }

    /// <summary>Real run: the model quoted "the said Asset" and "one or more of the said Asset more particularly described hereunder" —
    /// words that point at the assets but name none. They are dropped; anything with a number, or long enough to be a description, stays.</summary>
    [Theory]
    [InlineData("the said Asset")]
    [InlineData("one or more of the said Asset more particularly described hereunder.")]
    [InlineData("the said goods and assets")]
    public void A_fragment_that_only_points_at_the_assets_is_dropped(string pointer)
    {
        var v = ChargeInstrumentAi.Validate(Json(("Schedule", pointer), ("Schedule", "Flat No. 305, B Wing")),
            $"{pointer}\nFlat No. 305, B Wing, Sunrise Society");

        Assert.Equal("Flat No. 305, B Wing", Assert.Single(v.Result!.Passages).Text);
        Assert.Contains(v.Rejected, r => r.Contains("without describing it"));
    }

    [Fact]
    public void A_text_without_page_markers_is_page_one_and_an_array_wrapped_answer_is_unwrapped()
    {
        var wrapped = "[" + Json(("Particulars", "Flat No. 305, B Wing")) + "]";

        var v = ChargeInstrumentAi.Validate(wrapped, "Flat No. 305, B Wing, Sunrise Society");

        Assert.Equal(1, Assert.Single(v.Result!.Passages).Page);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"other\":1}")]
    public void An_unreadable_answer_fails_without_a_result(string raw)
    {
        var v = ChargeInstrumentAi.Validate(raw, Document);

        Assert.False(v.IsAccepted);
        Assert.Null(v.Result);
    }

    [Fact]
    public void An_empty_list_is_a_valid_answer_for_a_document_with_no_property()
    {
        var v = ChargeInstrumentAi.Validate(Json(), Document);

        Assert.True(v.IsAccepted);
        Assert.Empty(v.Result!.Passages);
    }

    [Fact]
    public void The_prompt_carries_the_document_and_no_example_identifiers()
    {
        var prompt = ChargeInstrumentAi.BuildPrompt("DOC-BODY-TEXT");

        Assert.Contains("DOC-BODY-TEXT", prompt);
        Assert.Contains(ChargeInstrumentAi.PromptMarker, prompt);
        // A realistic example in a prompt gets echoed back by the model; the shape uses placeholders only.
        Assert.DoesNotContain("Flat No", prompt);
        Assert.DoesNotContain("Survey No", prompt);
    }
}

public sealed class ChargeInstrumentExtractionServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "instr-" + Guid.NewGuid().ToString("N"));

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

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

    private sealed class FakeClient(Func<string, PropertyParticularsAiCallResult> respond) : IPropertyParticularsAiClient
    {
        public readonly List<string> Prompts = [];
        public Task<PropertyParticularsAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(respond(prompt));
        }
    }

    private sealed record Seeded(McaRequest Request, McaFilingBatch Batch, McaFiling Filing);

    private static async Task<Seeded> SeedAsync(AppDbContext db)
    {
        var request = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Instr Co", RequestNumber = $"CI-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var batch = new McaFilingBatch { RequestId = request.RequestId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "SRN-1", NestedZipName = "n.zip" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();
        return new Seeded(request, batch, filing);
    }

    private async Task<McaFilingDocument> AddLinkedDocumentAsync(AppDbContext db, Seeded s, string text, string hash, string charge = "1001",
        TextExtractionMethod method = TextExtractionMethod.Native)
    {
        var textPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(textPath, text);
        var d = new McaFilingDocument
        {
            FilingId = s.Filing.FilingId, BatchId = s.Batch.BatchId, RequestId = s.Request.RequestId, OriginalFileName = "Deed.pdf", StoragePath = "gone.pdf",
            ExtractedTextPath = textPath, FileHash = hash, ProcessingStatus = FilingDocumentProcessingStatus.Completed, Category = FilingCategory.Charge,
            TextExtractionMethod = method, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(d);
        await db.SaveChangesAsync();
        db.ChargeDocumentLinks.Add(new ChargeDocumentLink
        {
            RequestId = s.Request.RequestId, BatchId = s.Batch.BatchId, RocChargeNumber = charge, FilingDocumentId = d.FilingDocumentId,
            Method = ChargeDocumentLinkMethod.EmbeddedAttachment
        });
        await db.SaveChangesAsync();
        return d;
    }

    private static ChargeInstrumentExtractionService NewService(AppDbContext db, IPropertyParticularsAiClient? client = null, bool active = true,
        ChargeInstrumentExtractionOptions? options = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(active
            ? new Dictionary<string, string?> { ["GoogleCloud:ProjectId"] = "p", ["GoogleCloud:CredentialsPath"] = "c" }
            : new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        if (client is not null) services.AddSingleton(client);
        return new ChargeInstrumentExtractionService(db, new ChargeInstrumentExtractionQueue(), Options.Create(options ?? new ChargeInstrumentExtractionOptions()),
            config, services.BuildServiceProvider(), NullLogger<ChargeInstrumentExtractionService>.Instance);
    }

    private const string DeedText = "--- Page 1 (native) ---\nMortgage deed\n--- Page 2 (OCR) ---\nFlat No. 305, B Wing, Sunrise Society, Village Baner\n";
    private const string Answer = """{"passages":[{"kind":"Schedule","text":"Flat No. 305, B Wing, Sunrise Society, Village Baner"},{"kind":"Schedule","text":"Flat No. 315, B Wing"}]}""";

    [Fact]
    public async Task A_linked_deed_is_extracted_grounded_and_shown_with_its_page_and_nothing_is_scheduled_when_inactive()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var doc = await AddLinkedDocumentAsync(db, s, DeedText, Guid.NewGuid().ToString("N"));

        Assert.Equal(0, await NewService(db, active: false).ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None));

        var client = new FakeClient(_ => new(true, Answer, null));
        var service = NewService(db, client);
        Assert.Equal(1, await service.ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None));
        var id = await db.ChargeInstrumentExtractions.Where(x => x.FilingDocumentId == doc.FilingDocumentId).Select(x => x.ChargeInstrumentExtractionId).SingleAsync();
        await service.ProcessAsync(id, CancellationToken.None);

        await using var verify = CreateContext();
        var passages = (await ChargeInstrumentExtractionService.LoadAsync(verify, [doc.FilingDocumentId], CancellationToken.None))[doc.FilingDocumentId].Passages;
        var p = Assert.Single(passages); // flat 315 is not in the deed: dropped
        Assert.Equal(("Flat No. 305, B Wing, Sunrise Society, Village Baner", 2), (p.Text, p.Page));
        Assert.Contains(DeedText.Split('\n')[3], client.Prompts.Single()); // the document's text went to the model
        Assert.Equal(ChargeInstrumentExtractionStatus.Completed, (await verify.ChargeInstrumentExtractions.SingleAsync(x => x.FilingDocumentId == doc.FilingDocumentId)).Status);
    }

    [Fact]
    public async Task Scheduling_is_idempotent_and_skips_documents_read_exactly_from_their_form_data()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var plain = await AddLinkedDocumentAsync(db, s, DeedText, Guid.NewGuid().ToString("N"));
        var xfaText = await AddLinkedDocumentAsync(db, s, "ChargeID: 1001", Guid.NewGuid().ToString("N"), method: TextExtractionMethod.Xfa);
        var withForm = await AddLinkedDocumentAsync(db, s, DeedText, Guid.NewGuid().ToString("N"));
        await XfaFormReader.WriteSidecarAsync((await db.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == withForm.FilingDocumentId)).ExtractedTextPath!,
            [new("F/ChargeID", "ChargeID", "1001")], CancellationToken.None);
        var service = NewService(db, new FakeClient(_ => new(true, Answer, null)));

        Assert.Equal(1, await service.ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None));
        Assert.Equal(0, await service.ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None));

        var scheduled = await db.ChargeInstrumentExtractions.AsNoTracking().Where(x => x.RequestId == s.Request.RequestId).Select(x => x.FilingDocumentId).ToListAsync();
        Assert.Equal([plain.FilingDocumentId], scheduled);
        Assert.DoesNotContain(xfaText.FilingDocumentId, scheduled);
        Assert.DoesNotContain(withForm.FilingDocumentId, scheduled);
    }

    /// <summary>A refreshed batch holds the same PDFs: the earlier result is copied and the model is not called again.</summary>
    [Fact]
    public async Task The_same_pdf_in_a_refreshed_batch_takes_the_earlier_result_without_a_model_call()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var hash = Guid.NewGuid().ToString("N");
        var first = await AddLinkedDocumentAsync(db, s, DeedText, hash);
        var client = new FakeClient(_ => new(true, Answer, null));
        var service = NewService(db, client);
        await service.ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None);
        await service.ProcessAsync(await db.ChargeInstrumentExtractions.Where(x => x.FilingDocumentId == first.FilingDocumentId).Select(x => x.ChargeInstrumentExtractionId).SingleAsync(), CancellationToken.None);

        var batch2 = new McaFilingBatch { RequestId = s.Request.RequestId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch2);
        await db.SaveChangesAsync();
        var filing2 = new McaFiling { BatchId = batch2.BatchId, RequestId = s.Request.RequestId, Srn = "SRN-1", NestedZipName = "n.zip" };
        db.McaFilings.Add(filing2);
        await db.SaveChangesAsync();
        var second = await AddLinkedDocumentAsync(db, new Seeded(s.Request, batch2, filing2), DeedText, hash);

        Assert.Equal(1, await service.ScheduleForBatchAsync(batch2.BatchId, CancellationToken.None));

        var copy = await db.ChargeInstrumentExtractions.AsNoTracking().SingleAsync(x => x.FilingDocumentId == second.FilingDocumentId);
        Assert.Equal(ChargeInstrumentExtractionStatus.Completed, copy.Status);
        Assert.NotNull(copy.ReusedFromExtractionId);
        Assert.Single(client.Prompts); // only the first extraction called the model
        Assert.Single((await ChargeInstrumentExtractionService.LoadAsync(db, [second.FilingDocumentId], CancellationToken.None))[second.FilingDocumentId].Passages);
    }

    [Fact]
    public async Task A_malformed_answer_is_retried_and_the_next_well_formed_answer_completes()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var doc = await AddLinkedDocumentAsync(db, s, DeedText, Guid.NewGuid().ToString("N"));
        var answers = new Queue<string>(["{\"passages\":[{\"kind\":\"Schedule\",\"text\":\"broken \"quote\" here\"}]}", Answer]);
        var client = new FakeClient(_ => new(true, answers.Dequeue(), null));
        var service = NewService(db, client);
        await service.ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None);
        var id = await db.ChargeInstrumentExtractions.Where(x => x.FilingDocumentId == doc.FilingDocumentId).Select(x => x.ChargeInstrumentExtractionId).SingleAsync();

        await service.ProcessAsync(id, CancellationToken.None);
        var afterFirst = await db.ChargeInstrumentExtractions.AsNoTracking().SingleAsync(x => x.ChargeInstrumentExtractionId == id);
        Assert.Equal(ChargeInstrumentExtractionStatus.Pending, afterFirst.Status); // not given up on
        Assert.Contains("Invalid JSON", afterFirst.FailureReason);

        await db.ChargeInstrumentExtractions.Where(x => x.ChargeInstrumentExtractionId == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.NextAttemptUtc, (DateTime?)null));
        await service.ProcessAsync(id, CancellationToken.None);

        var done = await db.ChargeInstrumentExtractions.AsNoTracking().SingleAsync(x => x.ChargeInstrumentExtractionId == id);
        Assert.Equal((ChargeInstrumentExtractionStatus.Completed, 2), (done.Status, done.AttemptCount));
        Assert.Single((await ChargeInstrumentExtractionService.LoadAsync(db, [doc.FilingDocumentId], CancellationToken.None))[doc.FilingDocumentId].Passages);
    }

    /// <summary>PR #385 review: an answer with a null entry used to throw out of validation and leave the claimed row InProgress with a live
    /// lease. Anything that goes wrong after the claim now reaches the fenced retry/failure path.</summary>
    [Fact]
    public async Task A_null_entry_answer_is_retried_a_valid_one_completes_and_bad_answers_exhaust_the_attempts_cleanly()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var doc = await AddLinkedDocumentAsync(db, s, DeedText, Guid.NewGuid().ToString("N"));
        // A null member, then an answer of the wrong shape, then a good one.
        var answers = new Queue<string>(["{\"passages\":[null]}", "{\"passages\":\"oops\"}", Answer]);
        var client = new FakeClient(_ => new(true, answers.Dequeue(), null));
        var service = NewService(db, client);
        await service.ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None);
        var id = await db.ChargeInstrumentExtractions.Where(x => x.FilingDocumentId == doc.FilingDocumentId).Select(x => x.ChargeInstrumentExtractionId).SingleAsync();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await db.ChargeInstrumentExtractions.Where(x => x.ChargeInstrumentExtractionId == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.NextAttemptUtc, (DateTime?)null));
            await service.ProcessAsync(id, CancellationToken.None);
            var row = await db.ChargeInstrumentExtractions.AsNoTracking().SingleAsync(x => x.ChargeInstrumentExtractionId == id);
            Assert.Null(row.LeaseToken); // never left claimed
            Assert.NotEqual(ChargeInstrumentExtractionStatus.InProgress, row.Status);
            Assert.Equal(attempt < 3 ? ChargeInstrumentExtractionStatus.Pending : ChargeInstrumentExtractionStatus.Completed, row.Status);
        }
        Assert.Single((await ChargeInstrumentExtractionService.LoadAsync(db, [doc.FilingDocumentId], CancellationToken.None))[doc.FilingDocumentId].Passages);
    }

    [Fact]
    public async Task A_document_that_cannot_be_read_after_the_claim_ends_failed_not_stranded_in_progress()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var doc = await AddLinkedDocumentAsync(db, s, DeedText, Guid.NewGuid().ToString("N"));
        var service = NewService(db, new FakeClient(_ => new(true, Answer, null)), options: new ChargeInstrumentExtractionOptions { MaxAttempts = 1 });
        await service.ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None);
        var id = await db.ChargeInstrumentExtractions.Where(x => x.FilingDocumentId == doc.FilingDocumentId).Select(x => x.ChargeInstrumentExtractionId).SingleAsync();
        // The text file is a directory now: reading it throws an IOException after the row has been claimed.
        var path = (await db.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == doc.FilingDocumentId)).ExtractedTextPath!;
        File.Delete(path);
        Directory.CreateDirectory(path);

        await service.ProcessAsync(id, CancellationToken.None);

        var row = await db.ChargeInstrumentExtractions.AsNoTracking().SingleAsync(x => x.ChargeInstrumentExtractionId == id);
        Assert.Equal((ChargeInstrumentExtractionStatus.Failed, null), (row.Status, row.LeaseToken));
        Assert.False(string.IsNullOrEmpty(row.FailureReason));
    }

    [Fact]
    public async Task A_failed_call_is_retried_then_fails_and_a_document_over_the_limit_is_not_sent()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var doc = await AddLinkedDocumentAsync(db, s, DeedText, Guid.NewGuid().ToString("N"));
        var big = await AddLinkedDocumentAsync(db, s, new string('x', 300), Guid.NewGuid().ToString("N"));
        var client = new FakeClient(_ => new(false, "", "boom"));
        var service = NewService(db, client, options: new ChargeInstrumentExtractionOptions { MaxAttempts = 1, MaxDocumentChars = 150 });
        await service.ScheduleForBatchAsync(s.Batch.BatchId, CancellationToken.None);

        foreach (var id in await db.ChargeInstrumentExtractions.Where(x => x.RequestId == s.Request.RequestId).Select(x => x.ChargeInstrumentExtractionId).ToListAsync())
            await service.ProcessAsync(id, CancellationToken.None);

        var rows = await db.ChargeInstrumentExtractions.AsNoTracking().Where(x => x.RequestId == s.Request.RequestId).ToListAsync();
        Assert.All(rows, r => Assert.Equal(ChargeInstrumentExtractionStatus.Failed, r.Status));
        Assert.Contains("longer than", rows.Single(r => r.FilingDocumentId == big.FilingDocumentId).FailureReason);
        Assert.DoesNotContain(client.Prompts, p => p.Contains(new string('x', 300))); // never sent
        Assert.Equal("boom", rows.Single(r => r.FilingDocumentId == doc.FilingDocumentId).FailureReason);
    }
}
