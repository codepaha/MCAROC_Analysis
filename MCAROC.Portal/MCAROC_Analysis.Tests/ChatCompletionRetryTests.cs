using MCAROC_Analysis.Services.Chat;

namespace MCAROC_Analysis.Tests;

/// <summary>A live Coastal run found the Copilot answering "I could not verify this from the uploaded records." to questions whose sources were
/// retrieved perfectly (all eight chunks of the MGT-7 forms, for "show the latest annual return"). The fixed message is also what the service shows
/// when the model's reply is not the JSON asked for or cites no tag, so a formatting slip by the small model looked like missing evidence. One retry,
/// only for that case, never when the model honestly says the evidence is insufficient.</summary>
public class ChatCompletionRetryTests
{
    private const string Good = """{ "answer": "Filed on 27 April 2018.", "citedTags": ["D1"], "insufficientEvidence": false }""";
    private const string Fixed = "I could not verify this from the uploaded records.";

    private static readonly RetrievedSource Chunk = new("D1", SourceType.DocumentChunk, "Form MGT-7 ...", "Form MGT-7.pdf · Page 1",
        RelevanceScore: 0.2, ChunkId: 9, DocumentName: "Form MGT-7.pdf", PageNumber: 1, DocumentId: 4);

    private sealed class ScriptedModel(params string[] replies) : ChatCompletionService
    {
        private readonly Queue<string> _replies = new(replies);
        public List<string> Prompts { get; } = [];
        protected override Task<string> GenerateRawAsync(string prompt, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : "");
        }
    }

    private static Task<ChatCompletionResult> AskAsync(ScriptedModel model) =>
        model.CompleteAsync("Test Co", new RetrievalContext([Chunk], "Complete"), [], "Show the latest annual return", default);

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("""{ "answer": "Filed on 27 April 2018.", "citedTags": [], "insufficientEvidence": false }""")]           // an answer with no citation
    [InlineData("""{ "answer": "Filed on 27 April 2018.", "citedTags": ["Z9"], "insufficientEvidence": false }""")]       // cites a tag that does not exist
    [InlineData("""{ "answer": "", "citedTags": ["D1"], "insufficientEvidence": false }""")]                              // a citation but no answer
    [InlineData("""{ "answer": "   ", "citedTags": ["D1"], "insufficientEvidence": false }""")]
    [InlineData("""{ "citedTags": ["D1"], "insufficientEvidence": false }""")]                                          // no answer at all
    public async Task An_unusable_reply_is_retried_once_and_the_second_reply_is_used(string firstReply)
    {
        var model = new ScriptedModel(firstReply, Good);

        var result = await AskAsync(model);

        Assert.False(result.InsufficientEvidence);
        Assert.Equal("Filed on 27 April 2018.", result.Answer);
        Assert.Single(result.CitedSources);
        Assert.Equal(2, model.Prompts.Count);
        Assert.Contains("Your previous reply could not be used", model.Prompts[1]);
        Assert.DoesNotContain("Your previous reply could not be used", model.Prompts[0]);
    }

    [Fact]
    public async Task A_reply_that_is_unusable_twice_ends_in_the_fixed_message_after_exactly_two_calls()
    {
        var model = new ScriptedModel("not json", "still not json", Good);

        var result = await AskAsync(model);

        Assert.True(result.InsufficientEvidence);
        Assert.Equal(Fixed, result.Answer);
        Assert.Empty(result.CitedSources);
        Assert.Equal(2, model.Prompts.Count); // never a loop
    }

    [Fact]
    public async Task The_model_saying_the_evidence_is_insufficient_is_never_retried()
    {
        var model = new ScriptedModel("""{ "answer": "", "citedTags": [], "insufficientEvidence": true }""", Good);

        var result = await AskAsync(model);

        Assert.True(result.InsufficientEvidence);
        Assert.Equal(Fixed, result.Answer);
        Assert.Single(model.Prompts);
    }

    [Fact]
    public async Task The_fixed_message_written_as_a_supported_answer_is_the_model_declining_and_is_never_shown_with_citations()
    {
        var model = new ScriptedModel("""{ "answer": "I could not verify this from the uploaded records.", "citedTags": ["D1"], "insufficientEvidence": false }""", Good);

        var result = await AskAsync(model);

        Assert.True(result.InsufficientEvidence);
        Assert.Equal(Fixed, result.Answer);
        Assert.Empty(result.CitedSources);
        Assert.Single(model.Prompts); // the model declined: not retried
    }

    [Fact]
    public async Task The_retry_regenerates_from_the_evidence_and_never_repeats_the_uncited_answer_back_to_the_model()
    {
        // an unsupported answer re-sent "for citing" could come back with a valid tag and look sourced
        const string unsupported = "There are 167 pending cases against the company.";
        var model = new ScriptedModel($$"""{ "answer": "{{unsupported}}", "citedTags": [], "insufficientEvidence": false }""", Good);

        await AskAsync(model);

        Assert.Equal(2, model.Prompts.Count);
        Assert.DoesNotContain(unsupported, model.Prompts[1]);
        Assert.DoesNotContain("167", model.Prompts[1]);
        Assert.Contains("Answer again from the [FACT]/[SOURCE] blocks above only", model.Prompts[1]);
        Assert.Contains("tag of every block that supports each claim", model.Prompts[1]);
    }

    [Fact]
    public void The_prompt_says_plainly_that_an_answer_without_citations_is_discarded()
    {
        var model = new ScriptedModel(Good);

        AskAsync(model).GetAwaiter().GetResult();

        Assert.Contains("citedTags MUST list at least one tag", model.Prompts[0]);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<ChatCompletionService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class LoggingModel(CapturingLogger logger, params string[] replies) : ChatCompletionService(logger)
    {
        private readonly Queue<string> _replies = new(replies);
        protected override Task<string> GenerateRawAsync(string prompt, CancellationToken ct) => Task.FromResult(_replies.Dequeue());
    }

    [Fact]
    public async Task The_log_says_what_went_wrong_and_how_long_the_reply_was_but_never_quotes_it()
    {
        // a reply that quotes the client's records
        const string secret = "Rajeev Dhingra owes Rs 4222,18,97,043 to Karur Vysya Bank";
        var logger = new CapturingLogger();
        var model = new LoggingModel(logger,
            $$"""{ "answer": "{{secret}}", "citedTags": [], "insufficientEvidence": false }""",
            $$"""{ "answer": "{{secret}}", "citedTags": [], "insufficientEvidence": false }""");

        await model.CompleteAsync("Test Co", new RetrievalContext([Chunk], "Complete"), [], "q", default);

        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, m =>
        {
            Assert.DoesNotContain("Rajeev", m);
            Assert.DoesNotContain("Karur", m);
            Assert.Contains("Uncited", m);
            Assert.Contains("characters", m);
        });
    }

    [Fact]
    public void The_answering_model_defaults_to_the_built_in_one_and_can_be_named()
    {
        Assert.Equal(ChatCompletionService.ModelId, new ScriptedModel().Model);
        // a configured model is recorded with every answer; the constructor that reads it needs credentials, so check the seam directly
        Assert.Equal("gemini-2.5-flash-lite", ChatCompletionService.ModelId);
    }

    [Fact]
    public async Task A_usable_first_reply_costs_one_call()
    {
        var model = new ScriptedModel(Good);

        var result = await AskAsync(model);

        Assert.Equal("Filed on 27 April 2018.", result.Answer);
        Assert.Single(model.Prompts);
    }

    [Theory]
    [InlineData(Good, ChatCompletionService.ChatReplyOutcome.Valid)]
    [InlineData("""{ "answer": "", "citedTags": [], "insufficientEvidence": true }""", ChatCompletionService.ChatReplyOutcome.ModelSaidInsufficient)]
    [InlineData("", ChatCompletionService.ChatReplyOutcome.Empty)]
    [InlineData("   ", ChatCompletionService.ChatReplyOutcome.Empty)]
    [InlineData("{ broken", ChatCompletionService.ChatReplyOutcome.Unparseable)]
    [InlineData("""{ "answer": "x", "citedTags": [], "insufficientEvidence": false }""", ChatCompletionService.ChatReplyOutcome.Uncited)]
    [InlineData("""{ "answer": "", "citedTags": ["D1"], "insufficientEvidence": false }""", ChatCompletionService.ChatReplyOutcome.BlankAnswer)]
    [InlineData("""{ "citedTags": ["D1"], "insufficientEvidence": false }""", ChatCompletionService.ChatReplyOutcome.BlankAnswer)]
    [InlineData("""{ "answer": "I could not verify this from the uploaded records.", "citedTags": ["D1"], "insufficientEvidence": false }""", ChatCompletionService.ChatReplyOutcome.ModelSaidInsufficient)]
    public void A_reply_is_classified_by_why_it_was_or_was_not_usable(string reply, ChatCompletionService.ChatReplyOutcome expected) =>
        Assert.Equal(expected, ChatCompletionService.ValidateWithOutcome(reply, [Chunk]).Outcome);
}
