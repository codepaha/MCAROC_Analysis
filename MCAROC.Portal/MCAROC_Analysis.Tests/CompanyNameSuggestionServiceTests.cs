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

    [Fact]
    public async Task Without_an_AI_client_the_search_goes_ahead_on_the_rule_made_keywords()
    {
        await using var db = CreateContext();
        Assert.Same(CompanyNameSuggestions.None, await Service(db, null).GetAsync("Hero FinCorp Limited", [], null, default));
    }
}
