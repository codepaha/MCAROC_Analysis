using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#497 PR C: a case whose party is a name the company has used is the company's for the time it used the name. Filed while it had the name, or after
/// it left a name nobody else took: the company's. Another company has the name and the dates do not settle it: held out (owner, 2026-10-25), judged per
/// case, and counted only if the judgement says it is the company's.</summary>
public class FormerNameAttributionTests : IAsyncLifetime
{
    private static readonly DateOnly Left = new(2014, 6, 19);
    private static NameHolder Other => new("U63090TG2001PTC000777", "EAST COAST CARRIERS PRIVATE LIMITED", "Telangana");

    private static NameAtFiling Former(params NameHolder[] others) => new("EAST COAST CARRIERS PVT LTD", true, Left, null, others);

    [Fact]
    public void A_former_name_case_filed_while_the_company_had_the_name_is_its_own_whoever_has_the_name_now()
    {
        var check = FormerNameCaseRules.Check(Former(Other), new DateOnly(2012, 3, 1));
        Assert.Equal(FormerNameCaseDecision.Keep, check.Decision);
        Assert.Equal("A party is the company's former name (EAST COAST CARRIERS PVT LTD), which it used until 19 Jun 2014; the case was filed on 1 Mar 2012.", check.Reason);
        Assert.False(FormerNameCaseRules.MayBeAnothersName(Former(), new DateOnly(2014, 6, 19)));   // the day it changed is still its name
    }

    [Fact]
    public void A_former_name_case_filed_after_the_change_is_the_companys_when_no_other_company_has_the_name()
    {
        var check = FormerNameCaseRules.Check(Former(), new DateOnly(2016, 5, 5));
        Assert.Equal((FormerNameCaseDecision.CarriedOn, "exact-former-name-carried-on"), (check.Decision, check.Rule));
        Assert.Contains("after the change, and no other company has that name", check.Reason);

        // With no filing date and nobody else with the name, the whole-name rule stands as before.
        Assert.Equal(FormerNameCaseDecision.Keep, FormerNameCaseRules.Check(Former(), null).Decision);
    }

    [Fact]
    public void A_former_name_another_company_has_today_is_judged_when_the_dates_do_not_show_it_was_the_companys()
    {
        var after = FormerNameCaseRules.Check(Former(Other), new DateOnly(2016, 5, 5));
        Assert.Equal(FormerNameCaseDecision.NeedsJudgement, after.Decision);
        Assert.Contains("which another company has today (EAST COAST CARRIERS PRIVATE LIMITED (U63090TG2001PTC000777))", after.Reason);
        Assert.Contains("filed on 5 May 2016, after the company left the name on 19 Jun 2014", after.Reason);

        Assert.Equal(FormerNameCaseDecision.NeedsJudgement, FormerNameCaseRules.Check(Former(Other), null).Decision);
        Assert.Equal(FormerNameCaseDecision.NeedsJudgement, FormerNameCaseRules.Check(Former(Other) with { UsedUntil = null }, new DateOnly(2012, 1, 1)).Decision);
    }

    [Fact]
    public void The_current_name_before_the_company_took_it_is_judged_only_when_another_company_had_it()
    {
        var current = new NameAtFiling("COASTAL PROJECTS LIMITED", false, null, new DateOnly(2010, 8, 11), [Other with { Name = "COASTAL PROJECTS LIMITED" }]);
        Assert.Equal(FormerNameCaseDecision.NeedsJudgement, FormerNameCaseRules.Check(current, new DateOnly(2008, 1, 1)).Decision);
        Assert.Equal(FormerNameCaseDecision.Keep, FormerNameCaseRules.Check(current, new DateOnly(2011, 1, 1)).Decision);
        Assert.Equal(FormerNameCaseDecision.Keep, FormerNameCaseRules.Check(current, null).Decision);
        Assert.Equal(FormerNameCaseDecision.Keep, FormerNameCaseRules.Check(current with { Others = [] }, new DateOnly(2008, 1, 1)).Decision);
    }

    private static NameChange Change(string from, string to, DateOnly? date) => new(from, to, date, NameChangeCheck.HistoryOnly, null, date, null);

    [Fact]
    public void A_name_used_twice_is_the_companys_up_to_the_last_day_it_used_it_and_an_undated_change_leaves_it_unknown()
    {
        IReadOnlyList<NameChange> changes =
        [
            Change("ALPHA LIMITED", "BETA LIMITED", new DateOnly(2010, 1, 1)),
            Change("BETA LIMITED", "ALPHA LIMITED", new DateOnly(2015, 1, 1)),
            Change("ALPHA LIMITED", "GAMMA LIMITED", new DateOnly(2020, 1, 1)),
        ];
        Assert.Equal(new DateOnly(2020, 1, 1), FormerNameCaseRules.UsedUntil(changes, "Alpha Ltd"));
        Assert.Equal(new DateOnly(2015, 1, 1), FormerNameCaseRules.UsedUntil(changes, "BETA LIMITED"));
        Assert.Equal(new DateOnly(2020, 1, 1), FormerNameCaseRules.CurrentSince(changes, new DateOnly(2001, 1, 1)));
        Assert.Equal(new DateOnly(2001, 1, 1), FormerNameCaseRules.CurrentSince([], new DateOnly(2001, 1, 1)));

        IReadOnlyList<NameChange> undated = [Change("ALPHA LIMITED", "BETA LIMITED", null)];
        Assert.Null(FormerNameCaseRules.UsedUntil(undated, "ALPHA LIMITED"));
        Assert.Null(FormerNameCaseRules.CurrentSince(undated, new DateOnly(2001, 1, 1)));
    }

    [Fact]
    public void The_judgement_counts_only_answers_for_cases_asked_and_anything_else_is_unsure()
    {
        FormerNameCaseQuestion Q(long id) => new(id, "EAST COAST CARRIERS PVT LTD", Former(Other), "High Court", "WP 1/2016", "05-05-2016", "Hyderabad, Telangana", ["EAST COAST CARRIERS PVT LTD"], ["STATE"]);
        var asked = new[] { Q(11), Q(12), Q(13) };

        var prompt = FormerNameCaseJudge.BuildPrompt(new AttributionSubject("Coastal Projects Limited", [], "U45203OR1995PLC003982", "Bhubaneswar, Odisha"),
            [Change("EAST COAST CARRIERS PVT LTD", "Coastal Projects Limited", Left)], asked);
        Assert.StartsWith(FormerNameCaseJudge.PromptMarker, prompt);
        Assert.Contains("Renamed from EAST COAST CARRIERS PVT LTD to Coastal Projects Limited on 19 Jun 2014", prompt);
        Assert.Contains("C3. High Court, case WP 1/2016, filed 05-05-2016, Hyderabad, Telangana.", prompt);
        Assert.Contains("The name now belongs to: EAST COAST CARRIERS PRIVATE LIMITED (U63090TG2001PTC000777, Telangana).", prompt);

        var read = FormerNameCaseJudge.Read(asked, """{"cases": [{"id": "C1", "verdict": "company", "reason": "Filed in the company's state."}, {"id": "C2", "verdict": "other", "reason": "x"}, {"id": "C9", "verdict": "company"}]}""");
        Assert.Equal([LitigationPartyVerdictKind.Same, LitigationPartyVerdictKind.Different, LitigationPartyVerdictKind.Unsure], read.Select(r => r.Verdict));
        Assert.Equal("Filed in the company's state.", read[0].Reason);
        Assert.All(FormerNameCaseJudge.Read(asked, "not json"), r => Assert.Equal(LitigationPartyVerdictKind.Unsure, r.Verdict));
    }

    // ---- the service, end to end --------------------------------------------------------------------------------------------------------------

    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Answers the shared-name judgement by case number: "OTHER" cases the other company's, "MINE" the company's; the party judgement (for any
    /// other case) is not expected.</summary>
    private sealed class ScriptedJudge(bool fail = false) : ILitigationAiAnalysisClient
    {
        public int Calls { get; private set; }
        public Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct, string? modelId = null)
        {
            Assert.StartsWith(FormerNameCaseJudge.PromptMarker, prompt);
            Calls++;
            if (fail) return Task.FromResult(new LitigationAiCallResult(false, "", "503 UNAVAILABLE"));
            var cases = prompt.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2 && l[0] == 'C' && char.IsDigit(l[1]) && l.Contains(". "))
                .Select(l => new { id = l[..l.IndexOf('.')], verdict = l.Contains("OTHER") ? "other" : l.Contains("MINE") ? "company" : "unsure", reason = "scripted" });
            return Task.FromResult(new LitigationAiCallResult(true, System.Text.Json.JsonSerializer.Serialize(new { cases }), null));
        }
    }

    private sealed record World(long RequestId, string Cin, Dictionary<string, long> Cases);

    /// <summary>Coastal as its name history has it: "EAST COAST CARRIERS PVT LTD" until 19 Jun 2014 (a name another company has today) and "COASTAL PROJECTS
    /// PRIVATE LIMITED" until 11 Aug 2010 (a name nobody has now).</summary>
    private static async Task<World> SeedAsync()
    {
        var word = new string(Enumerable.Range(0, 7).Select(_ => (char)Random.Shared.Next('A', 'Z' + 1)).ToArray());
        var cin = "U45203OR" + Random.Shared.Next(1000, 9999) + "PLC" + Random.Shared.Next(100000, 999999);
        var otherCin = "U63090TG" + Random.Shared.Next(1000, 9999) + "PTC" + Random.Shared.Next(100000, 999999);
        await using var db = Ctx();
        var client = new Client { ClientCode = "FN" + Guid.NewGuid().ToString("N")[..8], ClientName = "Former names", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = $"{word} PROJECTS LIMITED", Cin = cin,
            RequestNumber = $"FN-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        db.CompanyNameHistories.AddRange(
            new CompanyNameHistory { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, DisplayOrder = 1, PreviousName = $"{word} PROJECTS PRIVATE LIMITED", TillDate = new DateOnly(2010, 8, 11) },
            new CompanyNameHistory { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, DisplayOrder = 2, PreviousName = "EAST COAST CARRIERS PVT LTD", TillDate = Left });
        // The name is another company's today (the master holds current names only).
        var otherName = "EAST COAST CARRIERS PRIVATE LIMITED";
        db.CompanyMasterRecords.Add(new CompanyMasterRecord
        {
            Identifier = otherCin, RecordType = CompanyMasterRecordType.Company, Name = otherName, Status = "Active", State = "Telangana",
            NameNormalized = CompanyNameNormalizer.Normalize(otherName).NameNormalized, NameCore = CompanyNameNormalizer.Normalize(otherName).NameCore
        });

        string Parties(params string[] names) => System.Text.Json.JsonSerializer.Serialize(names.Select(n => new { name = n }));
        var cases = new Dictionary<string, LitigationCase>
        {
            ["before"] = new() { CaseNumber = "OS 1/2012", FilingDate = "01-03-2012", PetitionersJson = Parties("EAST COAST CARRIERS PVT LTD"), RespondentsJson = Parties("STATE BANK OF INDIA") },
            ["after-other"] = new() { CaseNumber = "WP OTHER 2/2016", FilingDate = "05-05-2016", PetitionersJson = Parties("M/S EAST COAST CARRIERS PRIVATE LIMITED"), RespondentsJson = Parties("STATE OF TELANGANA") },
            ["undated-mine"] = new() { CaseNumber = "CS MINE 3", PetitionersJson = Parties("EAST COAST CARRIERS PVT. LTD."), RespondentsJson = Parties("IFCI LIMITED") },
            ["carried-on"] = new() { CaseNumber = "CA 4/2018", FilingDate = "10-01-2018", PetitionersJson = Parties($"{word} PROJECTS PVT LTD"), RespondentsJson = Parties("UNION OF INDIA") },
            ["current"] = new() { CaseNumber = "AA 5/2019", FilingDate = "02-02-2019", PetitionersJson = Parties($"{word} PROJECTS LIMITED"), RespondentsJson = Parties("NHAI") },
        };
        foreach (var c in cases.Values)
        {
            c.RequestId = request.RequestId; c.Court = "High Court"; c.State = "Telangana";
            c.FirstSeenUtc = c.LastSeenUtc = DateTime.UtcNow;
            c.AttributionOutcome = LitigationAttributionOutcome.Pending; c.AttributionDecidedUtc = DateTime.UtcNow;
        }
        db.LitigationCases.AddRange(cases.Values);
        await db.SaveChangesAsync();
        return new World(request.RequestId, cin, cases.ToDictionary(k => k.Key, k => k.Value.LitigationCaseId));
    }

    private static LitigationAttributionService Service(AppDbContext db, ILitigationAiAnalysisClient? judge)
    {
        var services = new ServiceCollection();
        if (judge is not null) services.AddSingleton(judge);
        return new LitigationAttributionService(db, services.BuildServiceProvider(), NullLogger<LitigationAttributionService>.Instance);
    }

    private static async Task<Dictionary<string, LitigationCase>> CasesAsync(World w)
    {
        await using var db = Ctx();
        var rows = await db.LitigationCases.AsNoTracking().Where(c => c.RequestId == w.RequestId).ToDictionaryAsync(c => c.LitigationCaseId);
        return w.Cases.ToDictionary(k => k.Key, k => rows[k.Value]);
    }

    [Fact]
    public async Task Each_case_under_a_former_name_is_the_companys_only_for_the_time_it_had_the_name()
    {
        var w = await SeedAsync();
        var judge = new ScriptedJudge();
        await using (var db = Ctx())
        {
            var result = await Service(db, judge).AttributeRequestAsync(w.RequestId, default);
            Assert.Equal((5, 0), (result.Assessed, result.Pending));
            Assert.Equal(2, result.Judged);
        }
        Assert.Equal(1, judge.Calls);   // the two held cases, one batch

        var cases = await CasesAsync(w);
        Assert.Equal((LitigationAttributionOutcome.Attributed, "exact-former-name"), (cases["before"].AttributionOutcome!.Value, cases["before"].AttributionRule));
        Assert.Contains("which it used until 19 Jun 2014; the case was filed on 1 Mar 2012", cases["before"].AttributionReason);
        Assert.Equal((LitigationAttributionOutcome.NotTheCompany, "judged-shared-name-other"), (cases["after-other"].AttributionOutcome!.Value, cases["after-other"].AttributionRule));
        Assert.Equal((LitigationAttributionOutcome.Attributed, "judged-shared-name-company"), (cases["undated-mine"].AttributionOutcome!.Value, cases["undated-mine"].AttributionRule));
        Assert.Equal((LitigationAttributionOutcome.Attributed, "exact-former-name-carried-on"), (cases["carried-on"].AttributionOutcome!.Value, cases["carried-on"].AttributionRule));
        Assert.Equal((LitigationAttributionOutcome.Attributed, "exact"), (cases["current"].AttributionOutcome!.Value, cases["current"].AttributionRule));
    }

    [Fact]
    public async Task A_held_case_is_not_counted_until_judged_and_a_judgement_is_never_asked_twice()
    {
        var w = await SeedAsync();
        await using (var db = Ctx())
        {
            var result = await Service(db, new ScriptedJudge(fail: true)).AttributeRequestAsync(w.RequestId, default);
            Assert.Equal(2, result.Pending);
        }
        var waiting = await CasesAsync(w);
        Assert.Equal(LitigationAttributionOutcome.Pending, waiting["after-other"].AttributionOutcome);
        Assert.False(LitigationAttribution.Shown(waiting["after-other"].AttributionOutcome));
        Assert.Contains("which another company has today", waiting["after-other"].AttributionReason);
        Assert.Equal(LitigationAttributionOutcome.Attributed, waiting["before"].AttributionOutcome);   // the rest are settled meanwhile

        var judge = new ScriptedJudge();
        await using (var db = Ctx()) await Service(db, judge).AttributeRequestAsync(w.RequestId, default);
        Assert.Equal(1, judge.Calls);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, (await CasesAsync(w))["after-other"].AttributionOutcome);

        // Assessed again (a refresh imports the same cases as new), the stored judgements answer: no call.
        await using (var db = Ctx())
        {
            await db.LitigationCases.Where(c => c.RequestId == w.RequestId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AttributionOutcome, LitigationAttributionOutcome.Pending));
            var again = new ScriptedJudge();
            await Service(db, again).AttributeRequestAsync(w.RequestId, default);
            Assert.Equal(0, again.Calls);
        }
        Assert.Equal(LitigationAttributionOutcome.Attributed, (await CasesAsync(w))["undated-mine"].AttributionOutcome);
    }

    [Fact]
    public async Task Without_an_ai_client_a_held_case_waits_and_the_others_are_settled()
    {
        var w = await SeedAsync();
        await using (var db = Ctx())
            Assert.Equal(2, (await Service(db, null).AttributeRequestAsync(w.RequestId, default)).Pending);
        var cases = await CasesAsync(w);
        Assert.Equal([LitigationAttributionOutcome.Pending, LitigationAttributionOutcome.Pending],
            new[] { cases["after-other"].AttributionOutcome!.Value, cases["undated-mine"].AttributionOutcome!.Value });
        Assert.Equal(LitigationAttributionOutcome.Attributed, cases["carried-on"].AttributionOutcome);
    }
}
