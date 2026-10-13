using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#489: the one AI call per company name — checked, cached for every later search, and never in the way when it fails.</summary>
public class CompanyNameSuggestionServiceTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class ScriptedClient(params LitigationAiCallResult[] answers) : ILitigationAiAnalysisClient
    {
        public int Calls { get; private set; }
        public Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct, string? modelId = null)
        {
            Assert.StartsWith(CompanyNameSuggestionService.PromptMarker, prompt);
            return Task.FromResult(answers[Math.Min(Calls++, answers.Length - 1)]);
        }
    }

    /// <summary>Holds its answer until released, counting the calls that reached it.</summary>
    private sealed class GatedClient(string answer) : ILitigationAiAnalysisClient
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct, string? modelId = null)
        {
            Interlocked.Increment(ref _calls);
            FirstCallStarted.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new LitigationAiCallResult(true, answer, null);
        }
    }

    [Fact]
    public async Task Two_searches_for_the_same_new_name_at_once_pay_for_one_call_and_both_get_its_answer()
    {
        var name = "Bharat Petroleum Corporation " + Guid.NewGuid().ToString("N")[..6] + " Limited";
        var client = new GatedClient("""{"spellings": ["भारत पेट्रोलियम"], "expansions": []}""");
        await using var db1 = CreateContext();
        await using var db2 = CreateContext();

        var first = Service(db1, client).GetAsync(name, ["Bharat Petroleum"], null, default);
        await client.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));       // the first holds the name's lock, mid-call
        var second = Service(db2, client).GetAsync(name, ["Bharat Petroleum"], null, default);
        await Task.Delay(500);
        Assert.False(second.IsCompleted);                                              // waiting for the first, not calling
        client.Release.SetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, client.Calls);
        Assert.All(results, r => Assert.Equal("भारत पेट्रोलियम", Assert.Single(r.HindiSpellings).Value));
    }

    private static CompanyNameSuggestionService Service(AppDbContext db, ILitigationAiAnalysisClient? client)
    {
        var services = new ServiceCollection();
        if (client is not null) services.AddSingleton(client);
        return new CompanyNameSuggestionService(db, services.BuildServiceProvider(), NullLogger<CompanyNameSuggestionService>.Instance);
    }

    [Fact]
    public async Task Checked_suggestions_are_kept_and_the_second_search_for_the_name_makes_no_call()
    {
        // A unique name per run: the cache is shared by every test run on this database.
        var name = "HDFC Bank " + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant() + " Limited";
        var answer = """
            {"spellings": ["एचडीएफसी बैंक", "इंडियन ऑयल"],
             "expansions": [{"acronym": "HDFC", "full_form": "Housing Development Finance Corporation"}, {"acronym": "HDFC", "full_form": "Hindustan Finance"}]}
            """;
        var client = new ScriptedClient(new LitigationAiCallResult(true, answer, null));

        await using (var db = CreateContext())
        {
            var first = await Service(db, client).GetAsync(name, ["HDFC Bank"], null, default);
            Assert.Equal(["एचडीएफसी बैंक"], first.HindiSpellings.Select(s => s.Value).ToList());          // the different company is refused
            Assert.Equal("Housing Development Finance Corporation", Assert.Single(first.Expansions).FullForm);   // initials must spell HDFC
        }
        await using (var db = CreateContext())
        {
            var second = await Service(db, client).GetAsync(name, ["HDFC Bank"], null, default);
            Assert.Single(second.HindiSpellings);
            Assert.Single(second.Expansions);
        }
        Assert.Equal(1, client.Calls);

        await using var verify = CreateContext();
        var row = await verify.LitigationNameSpellings.AsNoTracking().SingleAsync(r => r.NameKey == name.ToUpperInvariant());
        Assert.Contains("इंडियन ऑयल", row.RejectedJson);   // kept for audit
    }

    [Fact]
    public async Task A_failed_call_gives_no_suggestions_and_is_not_cached()
    {
        var name = "Altigreen Propulsion Labs " + Guid.NewGuid().ToString("N")[..6] + " Private Limited";
        var client = new ScriptedClient(new LitigationAiCallResult(false, "", "503 UNAVAILABLE"),
            new LitigationAiCallResult(true, """{"spellings": [], "expansions": []}""", null));
        await using (var db = CreateContext())
            Assert.Same(CompanyNameSuggestions.None, await Service(db, client).GetAsync(name, [], null, default));
        await using (var db = CreateContext())
            await Service(db, client).GetAsync(name, [], null, default);
        Assert.Equal(2, client.Calls);   // the failure was tried again
    }

    [Theory]
    [InlineData("Bharat Petroleum Corporation Limited", "Bharat Petroleum Corp Old Limited",
        new[] { "Bharat Petroleum Corporation Limited", "Bharat Petroleum Corp Old Limited", "Bharat Petroleum Corporation", "Bharat Petroleum Corporation Ltd" })]
    [InlineData("Altigreen Propulsion Labs Private Limited", null,
        new[] { "Altigreen Propulsion Labs Private Limited", "Altigreen Propulsion Labs", "Altigreen Propulsion Labs Ltd" })]
    public async Task By_default_searches_use_exactly_the_keywords_they_used_before_and_make_no_call(string name, string? former, string[] before)
    {
        // Review of #498: with generated keywords off, a search must send exactly what it sent before this PR (written out here, not taken from the
        // planner, so a change to the planner is caught) and make no AI call. "Pvt Ltd" is a generated form and is not among them.
        var client = new ScriptedClient(new LitigationAiCallResult(true, """{"spellings": ["भारत पेट्रोलियम"], "expansions": []}""", null));
        await using var db = CreateContext();
        var suggester = new LitigationKeywordSuggester(Service(db, client));
        var keywords = await suggester.SuggestAsync(name, former is null ? [] : [former], null, default);

        Assert.Equal(before, keywords.Select(k => k.Value).ToArray());
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Switched_on_searches_use_the_generated_keywords()
    {
        var name = "Bharat Petroleum Corporation " + Guid.NewGuid().ToString("N")[..6] + " Limited";
        var client = new ScriptedClient(new LitigationAiCallResult(true, """{"spellings": [], "expansions": []}""", null));
        await using var db = CreateContext();
        var suggester = new LitigationKeywordSuggester(Service(db, client),
            Microsoft.Extensions.Options.Options.Create(new LitigationKeywordOptions { UseGeneratedKeywords = true }));
        var keywords = await suggester.SuggestAsync(name, [], null, default);

        Assert.Contains(keywords, k => k.Source == LitigationKeywordSource.GeneratedNameForm);
        Assert.Equal(1, client.Calls);

        var privateCo = await suggester.SuggestAsync("Altigreen Propulsion Labs " + Guid.NewGuid().ToString("N")[..6] + " Private Limited", [], null, default);
        Assert.Contains(privateCo, k => k.Value.EndsWith(" Pvt Ltd") && k.Source == LitigationKeywordSource.GeneratedNameForm);
    }

    [Fact]
    public async Task Without_an_AI_client_the_search_goes_ahead_on_the_rule_made_keywords()
    {
        await using var db = CreateContext();
        Assert.Same(CompanyNameSuggestions.None, await Service(db, null).GetAsync("Hero FinCorp Limited", [], null, default));
    }
}
