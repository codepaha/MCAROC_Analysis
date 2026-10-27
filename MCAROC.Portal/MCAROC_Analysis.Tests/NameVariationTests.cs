using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Name variations added for a company on the Litigation tab: the attribution rule, keyword planning, the analysis reuse that keeps an
/// added name from re-analysing every case, and adding/removing a variation (search re-run, cases sent back to attribution).</summary>
public sealed class NameVariationTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── attribution ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_party_that_is_exactly_a_variation_is_the_company()
    {
        var a = CaseAttributionRules.Assess(["Nupur Bansal", "GODREJ PROPS LTD."], ["Godrej Properties Limited"], [], ["Godrej Props Ltd"]);

        Assert.Equal(LitigationAttributionOutcome.Attributed, a.Outcome);
        Assert.Equal(CaseAttributionRules.NameVariationRule, a.Rule);
        Assert.Equal("Godrej Props Ltd", a.MatchedName);
    }

    /// <summary>"Laxmi Vatika Limited" contains "Vatika Limited" but is another company: the variation does not decide it.</summary>
    [Fact]
    public void A_party_that_only_contains_a_variation_is_not_decided_by_it()
    {
        var a = CaseAttributionRules.Assess(["Laxmi Vatika Limited", "Ram Kumar"], ["Vatika Group Private Limited"], [], ["Vatika Limited"]);

        Assert.NotEqual(CaseAttributionRules.NameVariationRule, a.Rule);
        Assert.Null(CaseAttributionRules.MatchingVariation("Laxmi Vatika Limited", ["Vatika Limited"]));
    }

    // ── keywords ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_variation_is_searched_after_the_registered_and_former_names_with_who_added_it()
    {
        var keywords = LitigationKeywordPlanner.Build("Godrej Properties Limited", ["Godrej Estate Developers Private Limited"],
            [new ApprovedLitigationAlias("Godrej Props Ltd", LitigationKeywordSource.AddedNameVariation, "Added by mcaroc@ct.com on 27 Oct 2026"),
             new ApprovedLitigationAlias("GODREJ PROPERTIES LIMITED", LitigationKeywordSource.AddedNameVariation, "duplicate")]);

        Assert.Equal(LitigationKeywordSource.LegalName, keywords[0].Source);
        Assert.Equal(LitigationKeywordSource.HistoricalLegalName, keywords[1].Source);
        var added = Assert.Single(keywords, k => k.Source == LitigationKeywordSource.AddedNameVariation);
        Assert.Equal(2, keywords.ToList().IndexOf(added));                       // ahead of every generated form, so no cap drops it
        Assert.Equal("Added by mcaroc@ct.com on 27 Oct 2026", added.Reason);
    }

    // ── analysis reuse ──────────────────────────────────────────────────────────────────────────────────

    private static CaseAnalysisInput Input(string? respondents, params string[] targetNames) =>
        LitigationCaseAnalysisInputBuilder.Build(new LitigationCase
        {
            LitigationCaseId = 1, CaseNumber = "CS 1/2020", Court = "Bombay High Court", CaseStatus = "Pending",
            PetitionersJson = "[\"Nupur Bansal\"]", RespondentsJson = respondents ?? "[\"Godrej Properties Limited\"]", Orders = []
        }, new Dictionary<long, LitigationOrderDocument>(), "Godrej Properties Limited", targetNames, new DateOnly(2026, 10, 27));

    /// <summary>Every searched name is in the evidence, so without this an added name variation would re-analyse every case (755 for Godrej).</summary>
    [Fact]
    public void An_added_name_that_is_not_a_party_keeps_the_earlier_analysis()
    {
        var before = Input(null, "Godrej Properties Limited");
        var after = Input(null, "Godrej Properties Limited", "Godrej Props Ltd");

        Assert.NotEqual(before.EvidenceHash, after.EvidenceHash);
        Assert.True(LitigationCaseAnalysisInputBuilder.SameEvidence(before.EvidenceJson, after));
    }

    [Fact]
    public void An_added_name_that_is_a_party_of_the_case_is_analysed_again()
    {
        var before = Input("[\"Godrej Props Ltd\"]", "Godrej Properties Limited");
        var after = Input("[\"Godrej Props Ltd\"]", "Godrej Properties Limited", "Godrej Props Ltd");

        Assert.False(LitigationCaseAnalysisInputBuilder.SameEvidence(before.EvidenceJson, after));
    }

    [Fact]
    public void Any_other_change_to_the_evidence_is_analysed_again()
    {
        var before = Input(null, "Godrej Properties Limited");
        var after = Input("[\"Godrej Properties Limited\", \"Godrej Projects Development Limited\"]", "Godrej Properties Limited", "Godrej Props Ltd");

        Assert.False(LitigationCaseAnalysisInputBuilder.SameEvidence(before.EvidenceJson, after));
        Assert.False(LitigationCaseAnalysisInputBuilder.SameEvidence(null, after));
    }

    // ── adding and removing ─────────────────────────────────────────────────────────────────────────────

    private static readonly BprLitigationOptions Bpr = new() { BaseUrl = "http://203.0.113.7:2087/", Id = "app-id", SecretKey = "secret" };

    private static NameVariationService Service(AppDbContext db)
    {
        var admission = new PaidCallAdmissionService(db, new StaticOptionsMonitor(new PipelineOptions()), TimeProvider.System, NullLogger<PaidCallAdmissionService>.Instance);
        var search = new LitigationSearchJobService(db, null!, new LitigationSearchQueue(), null!, null!, Options.Create(Bpr), NullLogger<LitigationSearchJobService>.Instance);
        var analysis = new LitigationAiAnalysisOrchestrator(db, null!, new LitigationAiAnalysisQueue(), Options.Create(new LitigationAiAnalysisOptions()),
            NullLogger<LitigationAiAnalysisOrchestrator>.Instance);
        var start = new LitigationStartService(db, admission, search, new LitigationSearchQueue(), analysis, Options.Create(Bpr));
        return new NameVariationService(db, start, NullLogger<NameVariationService>.Instance, Options.Create(Bpr));
    }

    /// <summary>A request (unique CIN) with one case set aside whose party is exactly "<paramref name="setAsideParty"/>", and one attributed case.</summary>
    private static async Task<(McaRequest Request, long SetAside, long Attributed)> SeedAsync(AppDbContext db, string setAsideParty)
    {
        var client = new Client { ClientCode = "NV" + Guid.NewGuid().ToString("N")[..8], ClientName = "Name Variation Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Vatika Group Private Limited",
            Cin = $"U{Random.Shared.Next(10000, 99999)}HR2006PTC{Random.Shared.Next(100000, 999999)}", RequestNumber = $"NV-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var setAside = new LitigationCase { RequestId = request.RequestId, CaseNumber = "CS 7/2021", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow,
            PetitionersJson = "[\"Ram Kumar\"]", RespondentsJson = $"[\"{setAsideParty}\"]", AttributionOutcome = LitigationAttributionOutcome.NotTheCompany,
            AttributionRule = "whole-name-mismatch", AttributionDecidedUtc = DateTime.UtcNow };
        var attributed = new LitigationCase { RequestId = request.RequestId, CaseNumber = "CS 8/2021", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow,
            PetitionersJson = "[\"Vatika Group Private Limited\"]", RespondentsJson = "[\"Shyam Lal\"]", AttributionOutcome = LitigationAttributionOutcome.Attributed,
            AttributionRule = "exact-name", AttributionDecidedUtc = DateTime.UtcNow };
        db.LitigationCases.AddRange(setAside, attributed);
        await db.SaveChangesAsync();
        return (request, setAside.LitigationCaseId, attributed.LitigationCaseId);
    }

    [Fact]
    public async Task Adding_a_variation_stores_it_for_the_company_reruns_the_search_with_it_and_reassesses_the_cases_it_names()
    {
        await using var db = CreateContext();
        var (request, setAside, attributed) = await SeedAsync(db, "Vatika Limited");

        var outcome = await Service(db).AddAsync(request.RequestId, ["  Vatika   Limited ", "India Limited", "vatika group private limited", new string('x', 201)], "mcaroc@ct.com", default);

        Assert.Equal(1, outcome.AddedCount);
        Assert.Equal(NameVariationAddStatus.TooGeneric, outcome.Results.Single(r => r.Name == "India Limited").Status);
        Assert.Equal(NameVariationAddStatus.AlreadySearched, outcome.Results.Single(r => r.Name == "vatika group private limited").Status);
        Assert.Equal(NameVariationAddStatus.Invalid, outcome.Results.Single(r => r.Name.Length > 200).Status);
        var added = outcome.Results.Single(r => r.Status == NameVariationAddStatus.Added);
        Assert.Equal("Vatika Limited", added.Name);                                // spaces collapsed
        Assert.NotNull(added.Warning);                                             // one distinctive word: other companies' cases may come back

        var stored = await db.CompanyNameVariations.AsNoTracking().SingleAsync(v => v.Identifier == request.Cin);
        Assert.Equal("mcaroc@ct.com", stored.AddedBy);
        Assert.True(outcome.Search!.Started);
        var keywords = LitigationKeyword.ParseJson(await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == request.RequestId).Select(j => j.KeywordsJson).SingleAsync());
        Assert.Contains(keywords, k => k.Value == "Vatika Limited" && k.Source == LitigationKeywordSource.AddedNameVariation);

        Assert.Equal(LitigationAttributionOutcome.Pending, (await db.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == setAside)).AttributionOutcome);
        Assert.Equal(LitigationAttributionOutcome.Attributed, (await db.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == attributed)).AttributionOutcome);
        Assert.Empty(await NameVariationService.UnsearchedAsync(db, request, default));
    }

    [Fact]
    public async Task A_variation_added_again_is_already_searched()
    {
        await using var db = CreateContext();
        var (request, _, _) = await SeedAsync(db, "Someone Else Limited");
        await Service(db).AddAsync(request.RequestId, ["Vatika Limited"], "mcaroc@ct.com", default);

        var again = await Service(db).AddAsync(request.RequestId, ["VATIKA LTD."], "mcaroc@ct.com", default);

        Assert.Equal(0, again.AddedCount);
        Assert.Contains("name variation", Assert.Single(again.Results).Message);
        Assert.Null(again.Search);
    }

    [Fact]
    public async Task Removing_a_variation_stops_it_and_sends_back_the_cases_it_alone_decided()
    {
        await using var db = CreateContext();
        var (request, setAside, attributed) = await SeedAsync(db, "Vatika Limited");
        await Service(db).AddAsync(request.RequestId, ["Vatika Limited"], "mcaroc@ct.com", default);
        var variation = await db.CompanyNameVariations.AsNoTracking().SingleAsync(v => v.Identifier == request.Cin);
        // The attribution worker settled the case on the variation.
        await db.LitigationCases.Where(c => c.LitigationCaseId == setAside).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.AttributionOutcome, LitigationAttributionOutcome.Attributed)
            .SetProperty(c => c.AttributionRule, CaseAttributionRules.NameVariationRule)
            .SetProperty(c => c.AttributionParty, "Vatika Limited"));

        // A new web request, a new context (the add's context still tracks the cases as it left them).
        await using var removeDb = CreateContext();
        Assert.True(await Service(removeDb).RemoveAsync(request.RequestId, variation.CompanyNameVariationId, "mcaroc@ct.com", default));

        var removed = await db.CompanyNameVariations.AsNoTracking().SingleAsync(v => v.CompanyNameVariationId == variation.CompanyNameVariationId);
        Assert.NotNull(removed.RemovedUtc);
        Assert.Equal("mcaroc@ct.com", removed.RemovedBy);
        Assert.Empty(await LitigationCompanyNames.ActiveVariationsAsync(db, request.Cin, default));
        Assert.Equal(LitigationAttributionOutcome.Pending, (await db.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == setAside)).AttributionOutcome);
        Assert.Equal(LitigationAttributionOutcome.Attributed, (await db.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == attributed)).AttributionOutcome);
        Assert.False(await Service(db).RemoveAsync(request.RequestId, variation.CompanyNameVariationId, "mcaroc@ct.com", default));   // already removed
    }

    [Fact]
    public async Task A_variation_is_one_of_the_company_names_every_reader_uses()
    {
        await using var db = CreateContext();
        var (request, _, _) = await SeedAsync(db, "Someone Else Limited");
        await Service(db).AddAsync(request.RequestId, ["Vatika Limited"], "mcaroc@ct.com", default);

        var names = await LitigationCompanyNames.ForRequestAsync(db, request.RequestId, default);

        Assert.Equal(["Vatika Group Private Limited", "Vatika Limited"], names);
        Assert.Equal(names, await ChargeLitigationService.CompanyNamesAsync(db, request.RequestId, default));
    }

    [Fact]
    public async Task A_request_without_a_CIN_cannot_add_a_variation()
    {
        await using var db = CreateContext();
        var (request, _, _) = await SeedAsync(db, "Someone Else Limited");
        await db.Requests.Where(r => r.RequestId == request.RequestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Cin, (string?)null));

        var outcome = await Service(db).AddAsync(request.RequestId, ["Vatika Limited"], "mcaroc@ct.com", default);

        Assert.NotNull(outcome.Problem);
        Assert.Empty(outcome.Results);
    }

    private sealed class StaticOptionsMonitor(PipelineOptions value) : IOptionsMonitor<PipelineOptions>
    {
        public PipelineOptions CurrentValue => value;
        public PipelineOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PipelineOptions, string?> listener) => null;
    }
}
