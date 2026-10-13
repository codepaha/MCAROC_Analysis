using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#489 decision (a): a vendor case is the company's only once a party is shown to be the company, by the whole-name rule or a judgement. Every
/// other case is kept with its reason but shown and counted nowhere, and the pipeline does not move on while cases wait.</summary>
public class LitigationAttributionTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly string[] Coastal = ["Coastal Projects Limited"];
    private static readonly string[] CoastalFormer = ["COASTAL PROJECTS PRIVATE LIMITED"];

    // ---- rules ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_party_that_is_the_company_settles_the_case_without_a_judgement()
    {
        var a = CaseAttributionRules.Assess(["STATE OF TRIPURA", "MS COASTAL PROJECTS LTD"], Coastal, CoastalFormer);
        Assert.Equal(LitigationAttributionOutcome.Attributed, a.Outcome);
        Assert.Equal("exact", a.Rule);
        Assert.Equal("MS COASTAL PROJECTS LTD", a.Party);
    }

    [Fact]
    public void Another_body_named_after_the_company_is_not_the_company_without_a_judgement()
    {
        var a = CaseAttributionRules.Assess(["COASTAL PROJECTS LIMITED EMPLOYEES COOPERATIVE SOCIETY", "REGISTRAR OF SOCIETIES"], Coastal, CoastalFormer);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, a.Outcome);
        Assert.Equal("other-body", a.Rule);
    }

    [Fact]
    public void A_word_of_the_companys_own_name_never_makes_a_party_another_body()
    {
        var a = CaseAttributionRules.Assess(["ABC CREDIT LTD, HYDERABAD", "STATE BANK OF INDIA"], ["ABC Credit Limited"], []);
        Assert.True(a.NeedsJudgement);
        Assert.Equal(["ABC CREDIT LTD, HYDERABAD"], a.Candidates);
    }

    [Fact]
    public void A_party_the_rules_cannot_settle_is_judged_and_only_the_resembling_parties_are_asked_about()
    {
        var a = CaseAttributionRules.Assess(["COASTAL PROJECTS LTD., UDAIPUR", "UNION OF INDIA AND ORS", "BHAWANI SINGH TANWAR"], Coastal, CoastalFormer);
        Assert.True(a.NeedsJudgement);
        Assert.Equal(["COASTAL PROJECTS LTD., UDAIPUR"], a.Candidates);
    }

    [Fact]
    public void When_nothing_resembles_the_company_every_party_is_judged()
    {
        // Found through a Hindi or phonetic keyword: no Latin party shares a word with the name.
        var a = CaseAttributionRules.Assess(["कोस्टल प्रोजेक्ट्स लिमिटेड", "उत्तर प्रदेश राज्य"], Coastal, CoastalFormer);
        Assert.True(a.NeedsJudgement);
        Assert.Equal(2, a.Candidates.Count);
    }

    [Fact]
    public void A_record_without_parties_cannot_be_attributed() =>
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, CaseAttributionRules.Assess([], Coastal, CoastalFormer).Outcome);

    // ---- the judgement's answer -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_verdict_counts_only_for_a_party_quoted_as_asked()
    {
        var asked = new[] { "COASTAL PROJECTS LTD., UDAIPUR", "S SURENDRA" };
        var read = PartyAttributionJudge.Read(asked, """
            {"verdicts": [
              {"party": "coastal  projects ltd., udaipur", "verdict": "same", "reason": "The company with its town."},
              {"party": "S. SURENDRA", "verdict": "same", "reason": "misquoted"}
            ]}
            """);
        Assert.Equal(LitigationPartyVerdictKind.Same, read[0].Verdict);         // case and spacing do not matter
        Assert.Equal(LitigationPartyVerdictKind.Unsure, read[1].Verdict);       // a different quote never counts as the company
    }

    // ---- the service, end to end ------------------------------------------------------------------------------------------------------------

    private sealed class ScriptedJudge(Func<string, string?> answer) : ILitigationAiAnalysisClient
    {
        public int Calls { get; private set; }
        public Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct, string? modelId = null)
        {
            Assert.StartsWith(PartyAttributionJudge.PromptMarker, prompt);
            Calls++;
            var raw = answer(prompt);
            return Task.FromResult(raw is null ? new LitigationAiCallResult(false, "", "503 UNAVAILABLE") : new LitigationAiCallResult(true, raw, null));
        }
    }

    /// <summary>Judges every asked party: "UDAIPUR" ones the company, "SURENDRA" different, anything else unsure.</summary>
    private static string Answer(string prompt)
    {
        var lines = prompt.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2 && char.IsDigit(l[0]) && l.Contains(". ")).Select(l => l[(l.IndexOf(". ") + 2)..]);
        var verdicts = lines.Select(p => new
        {
            party = p,
            verdict = p.Contains("UDAIPUR") ? "same" : p.Contains("SURENDRA") ? "different" : "unsure",
            reason = p.Contains("UDAIPUR") ? "The company with its town." : "test"
        });
        return System.Text.Json.JsonSerializer.Serialize(new { verdicts });
    }

    private sealed record World(long RequestId, long SnapshotId, Dictionary<string, long> Cases, string Cin);

    private static async Task<World> SeedAsync(string? cin = null)
    {
        cin ??= "U45203TG" + Random.Shared.Next(1000, 9999) + "PLC" + Random.Shared.Next(100000, 999999);
        await using var db = Ctx();
        var client = new Client { ClientCode = "AT" + Guid.NewGuid().ToString("N")[..8], ClientName = "Attribution Test", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Coastal Projects Limited", Cin = cin,
            RequestNumber = $"AT-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, EntityType = "company", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow, Status = LitigationSearchJobStatus.Completed };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = Guid.NewGuid().ToString("N"), ReportFormat = BprReportFormat.Json,
            RawReportBytes = [1], RawReportByteLength = 1, RetrievedUtc = DateTime.UtcNow, Status = LitigationReportSnapshotStatus.Completed, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        string Parties(params string[] names) => System.Text.Json.JsonSerializer.Serialize(names.Select(n => new { name = n }));
        var cases = new Dictionary<string, LitigationCase>
        {
            ["exact"] = new() { PetitionersJson = Parties("1 MS COASTAL PROJECTS LTD"), RespondentsJson = Parties("STATE OF TRIPURA") },
            ["society"] = new() { PetitionersJson = Parties("COASTAL PROJECTS LIMITED EMPLOYEES COOPERATIVE SOCIETY"), RespondentsJson = Parties("REGISTRAR") },
            ["town"] = new() { PetitionersJson = Parties("COASTAL PROJECTS LTD., UDAIPUR"), RespondentsJson = Parties("UNION OF INDIA AND ORS") },
            ["person"] = new() { PetitionersJson = Parties("COASTAL PROJECTS LIMITED ERSTWHILE MANAGING DIRECTOR S SURENDRA"), RespondentsJson = Parties("IFCI LIMITED") },
            ["unclear"] = new() { PetitionersJson = Parties("COASTAL PROJECT CORPORATION"), RespondentsJson = Parties("STATE OF ODISHA") },
        };
        foreach (var c in cases.Values)
        {
            c.RequestId = request.RequestId; c.Court = "High Court"; c.CaseNumber = Guid.NewGuid().ToString("N")[..8];
            c.FirstSeenUtc = c.LastSeenUtc = DateTime.UtcNow;
            c.AttributionOutcome = LitigationAttributionOutcome.Pending; c.AttributionDecidedUtc = DateTime.UtcNow;   // as a new import leaves them
        }
        db.LitigationCases.AddRange(cases.Values);
        await db.SaveChangesAsync();
        foreach (var c in cases.Values)
            db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return new World(request.RequestId, snapshot.LitigationReportSnapshotId, cases.ToDictionary(k => k.Key, k => k.Value.LitigationCaseId), cin);
    }

    private static LitigationAttributionService Service(AppDbContext db, ILitigationAiAnalysisClient? judge)
    {
        var services = new ServiceCollection();
        if (judge is not null) services.AddSingleton(judge);
        return new LitigationAttributionService(db, services.BuildServiceProvider(), NullLogger<LitigationAttributionService>.Instance);
    }

    [Fact]
    public async Task Every_case_is_settled_and_only_the_companys_cases_are_in_the_set()
    {
        var w = await SeedAsync();
        var judge = new ScriptedJudge(Answer);
        long versionBefore;
        await using (var db = Ctx())
        {
            versionBefore = await db.Requests.Where(r => r.RequestId == w.RequestId).Select(r => r.LiveDataVersion).SingleAsync();
            var result = await Service(db, judge).AttributeRequestAsync(w.RequestId, default);
            Assert.Equal(5, result.Assessed);
            Assert.Equal(0, result.Pending);
        }

        await using var verify = Ctx();
        var cases = await verify.LitigationCases.AsNoTracking().Where(c => c.RequestId == w.RequestId).ToDictionaryAsync(c => c.LitigationCaseId);
        LitigationCase Of(string key) => cases[w.Cases[key]];
        Assert.Equal((LitigationAttributionOutcome.Attributed, "exact"), (Of("exact").AttributionOutcome!.Value, Of("exact").AttributionRule));
        Assert.Equal((LitigationAttributionOutcome.NotTheCompany, "other-body"), (Of("society").AttributionOutcome!.Value, Of("society").AttributionRule));
        Assert.Equal((LitigationAttributionOutcome.Attributed, "judged-same"), (Of("town").AttributionOutcome!.Value, Of("town").AttributionRule));
        Assert.Equal("The company with its town.", Of("town").AttributionReason);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, Of("person").AttributionOutcome);
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, Of("unclear").AttributionOutcome);

        // The one shared set: list, counts, report, charge links and case page all read it.
        var shown = await LitigationCaseSets.Legacy(verify, w.SnapshotId).ToListAsync();
        Assert.Equal(new[] { w.Cases["exact"], w.Cases["town"] }.Order(), shown.Order());
        Assert.True(await verify.Requests.Where(r => r.RequestId == w.RequestId).Select(r => r.LiveDataVersion).SingleAsync() > versionBefore);   // caches refresh
        Assert.Equal(1, judge.Calls);   // three party names, one batch
    }

    [Fact]
    public async Task A_party_already_judged_for_the_company_costs_nothing_again()
    {
        var first = await SeedAsync();
        await using (var db = Ctx()) await Service(db, new ScriptedJudge(Answer)).AttributeRequestAsync(first.RequestId, default);

        var second = await SeedAsync(first.Cin);   // a refresh or another request for the same company
        var judge = new ScriptedJudge(Answer);
        await using (var db = Ctx()) await Service(db, judge).AttributeRequestAsync(second.RequestId, default);
        Assert.Equal(0, judge.Calls);
        await using var verify = Ctx();
        Assert.Equal(LitigationAttributionOutcome.Attributed,
            await verify.LitigationCases.Where(c => c.LitigationCaseId == second.Cases["town"]).Select(c => c.AttributionOutcome).SingleAsync());
    }

    [Fact]
    public async Task When_the_judgement_fails_the_cases_wait_hidden_and_are_settled_by_a_later_pass()
    {
        var w = await SeedAsync();
        await using (var db = Ctx())
        {
            var result = await Service(db, new ScriptedJudge(_ => null)).AttributeRequestAsync(w.RequestId, default);
            Assert.Equal(3, result.Pending);
        }
        await using (var db = Ctx())
        {
            Assert.Equal([w.Cases["exact"]], await LitigationCaseSets.Legacy(db, w.SnapshotId).ToListAsync());   // the rule-settled one only
            Assert.Equal(3, await db.LitigationCases.Where(c => c.RequestId == w.RequestId).Where(LitigationAttribution.IsWaiting).CountAsync());
        }
        await using (var db = Ctx())
        {
            var retry = await Service(db, new ScriptedJudge(Answer)).AttributeRequestAsync(w.RequestId, default);
            Assert.Equal(3, retry.Assessed);
            Assert.Equal(0, retry.Pending);
        }
    }

    // ---- the pipeline -----------------------------------------------------------------------------------------------------------------------

    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private static StageVerdict Litigation(int waiting, DateTime? since) => PipelineDecider.Decide(new PipelineSnapshot
    {
        RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.AnalysisCompleted,
        LatestCompletedIngestionRunId = 10, LatestIngestionStatus = IngestionRunStatus.CompletedClean,
        Analysis = new(20, AnalysisRunStatus.Completed, null), CalcAssuranceMode = CalculationAssuranceMode.Off, LitigationConfigured = true,
        LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.Completed,
            AttributionOutstanding: waiting, AttributionWaitingSinceUtc: since)
    }, new PipelinePolicy { LitigationSearch = true }, Now).Stages[PipelineStage.Litigation];

    [Fact]
    public void The_litigation_stage_is_not_done_while_cases_wait_and_asks_for_attention_when_they_wait_too_long()
    {
        Assert.Equal(PipelineStageStateKind.Running, Litigation(3, Now.AddMinutes(-5)).State);
        Assert.Equal("ATTRIBUTING", Litigation(3, Now.AddMinutes(-5)).ReasonCode);
        Assert.Equal("ATTRIBUTION_STALLED", Litigation(3, Now.AddHours(-3)).ReasonCode);
        Assert.True(Litigation(0, null).IsDone);
    }
}
