using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.CompanyMaster;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>#497: a client asks for a company by a name it no longer has. Our master holds current names only; the reference tool's search reports
/// former names (context <c>oldName</c>). Found that way, the company is offered filled in (or chosen, once the owner switches
/// <c>Resolve:AutoSelectFormerName</c> on); and the company holding the name today is never chosen when another company held it before.</summary>
public sealed class FormerNameResolutionTests : IAsyncLifetime
{
    private const string BhartiAirtel = "L74899HR1995PLC095967";
    private readonly string _word = "ZF" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _serial = Random.Shared.Next(100_000, 999_999).ToString();
    private readonly List<long> _requests = [];

    private string Cin(int n) => $"U{n}0000DL2001PLC{_serial}";
    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        await db.IdentityResolutions.Where(r => r.RequestId != null && _requests.Contains(r.RequestId.Value)).ExecuteDeleteAsync();
        await db.Requests.Where(r => _requests.Contains(r.RequestId)).ExecuteDeleteAsync();
        await db.CompanyFormerNames.Where(f => f.Identifier.EndsWith(_serial) || f.FormerName.Contains(_word)).ExecuteDeleteAsync();
        await db.CompanyNameTokens.Where(t => t.Identifier.EndsWith(_serial)).ExecuteDeleteAsync();
        await db.CompanyMasterRecords.Where(r => r.Identifier.EndsWith(_serial)).ExecuteDeleteAsync();
    }

    // ── The rules (pure) ─────────────────────────────────────────────────────────────────────────────────

    private static MasterCandidate Row(string id, string name, string status = "Active", string? state = null) =>
        new(id, CompanyMasterRecordType.Company, name, Status: status, State: state);

    private static ResolutionDecision MasterDecision(string input, ResolverOptions o, params MasterCandidate[] master) =>
        CompanyNameResolver.Decide(input, CompanyNameResolver.Rank(input, ResolutionHints.None, master), o);

    private static FormerNameLookup Found(params FormerNameHit[] hits) => new(true, hits, []);
    private static FormerNameHit Hit(string id, string former, string current) => new(id, former, current, "Active", null, null, null);
    private static readonly ResolverOptions Suggest = new() { AutoSelectEnabled = true };
    private static readonly ResolverOptions Apply = new() { AutoSelectEnabled = true, AutoSelectFormerName = true };

    [Fact]
    public void A_name_only_a_former_company_held_is_offered_filled_in_until_automatic_selection_is_switched_on()
    {
        const string asked = "Bharti Tele-Ventures Limited";
        var rows = new Dictionary<string, MasterCandidate> { [BhartiAirtel] = Row(BhartiAirtel, "BHARTI AIRTEL LIMITED") };
        var lookup = Found(Hit(BhartiAirtel, "BHARTI TELE-VENTURES LIMITED", "BHARTI AIRTEL LIMITED"));

        var offered = FormerNameResolver.Decide(asked, ResolutionHints.None, MasterDecision(asked, Suggest), lookup, rows, Suggest);
        Assert.Equal((ResolutionStatus.NeedsConfirmation, ResolutionReasonCodes.FormerNameNeedsConfirmation, BhartiAirtel),
            (offered.Status, offered.ReasonCode, offered.RecommendedIdentifier));
        Assert.True(offered.AutoSelectEligible);
        Assert.Null(offered.ChosenIdentifier);
        Assert.Equal("BHARTI AIRTEL LIMITED", offered.Candidates[0].Candidate.Name);   // its name today, from the master
        Assert.Contains("registered as \"BHARTI TELE-VENTURES LIMITED\" before", offered.Candidates[0].Reasons[0]);

        var chosen = FormerNameResolver.Decide(asked, ResolutionHints.None, MasterDecision(asked, Apply), lookup, rows, Apply);
        Assert.Equal((ResolutionStatus.Resolved, ResolutionMethod.AutoSelected, BhartiAirtel, ResolutionReasonCodes.FormerName),
            (chosen.Status, chosen.Method, chosen.ChosenIdentifier, chosen.ReasonCode));
    }

    [Fact]
    public void An_inactive_company_a_disagreeing_hint_or_one_missing_from_the_master_is_only_offered()
    {
        const string asked = "OLD NAME TRADING LIMITED";
        var lookup = Found(Hit("U11111DL2001PLC000001", asked, "NEW NAME TRADING LIMITED"));

        var inactive = new Dictionary<string, MasterCandidate> { ["U11111DL2001PLC000001"] = Row("U11111DL2001PLC000001", "NEW NAME TRADING LIMITED", "Strike Off") };
        Assert.Equal(ResolutionStatus.NeedsConfirmation, FormerNameResolver.Decide(asked, ResolutionHints.None, MasterDecision(asked, Apply), lookup, inactive, Apply).Status);

        var elsewhere = new Dictionary<string, MasterCandidate> { ["U11111DL2001PLC000001"] = Row("U11111DL2001PLC000001", "NEW NAME TRADING LIMITED", state: "Delhi") };
        var wrongState = FormerNameResolver.Decide(asked, new ResolutionHints(State: "Karnataka"), MasterDecision(asked, Apply), lookup, elsewhere, Apply);
        Assert.Equal(ResolutionStatus.NeedsConfirmation, wrongState.Status);
        Assert.False(wrongState.AutoSelectEligible);

        var toolOnly = FormerNameResolver.Decide(asked, ResolutionHints.None, MasterDecision(asked, Apply), lookup, new Dictionary<string, MasterCandidate>(), Apply);
        Assert.Equal(ResolutionStatus.NeedsConfirmation, toolOnly.Status);
        Assert.True(toolOnly.Candidates[0].Candidate.IsToolOnly);
        Assert.Equal("NEW NAME TRADING LIMITED", toolOnly.Candidates[0].Candidate.Name);
    }

    /// <summary>The guard on #495's unique-exact rule: one company has the name today, another had it before. The client may mean either.</summary>
    [Fact]
    public void A_name_held_today_by_one_company_and_before_by_another_is_never_chosen_automatically()
    {
        const string asked = "SUNRISE HOLDINGS LIMITED";
        var holder = Row("U22222DL2001PLC000002", asked);
        var master = MasterDecision(asked, Apply, holder);
        Assert.Equal(ResolutionReasonCodes.UniqueExact, master.ReasonCode);  // without the guard, today's holder would be chosen

        var rows = new Dictionary<string, MasterCandidate> { ["U33333DL2001PLC000003"] = Row("U33333DL2001PLC000003", "DAWN CAPITAL LIMITED") };
        var decision = FormerNameResolver.Decide(asked, ResolutionHints.None, master, Found(Hit("U33333DL2001PLC000003", asked, "DAWN CAPITAL LIMITED")), rows, Apply);

        Assert.Equal((ResolutionStatus.Ambiguous, ResolutionReasonCodes.FormerNameConflict), (decision.Status, decision.ReasonCode));
        Assert.Null(decision.ChosenIdentifier);
        Assert.Equal(["U33333DL2001PLC000003", "U22222DL2001PLC000002"], decision.Candidates.Select(c => c.Candidate.Identifier));
    }

    [Fact]
    public void A_name_the_search_says_someone_holds_today_counts_even_when_our_master_is_stale()
    {
        const string asked = "SUNRISE HOLDINGS LIMITED";
        var lookup = new FormerNameLookup(true, [Hit("U33333DL2001PLC000003", asked, "DAWN CAPITAL LIMITED")], ["U44444DL2001PLC000004"]);
        var decision = FormerNameResolver.Decide(asked, ResolutionHints.None, MasterDecision(asked, Apply), lookup,
            new Dictionary<string, MasterCandidate> { ["U33333DL2001PLC000003"] = Row("U33333DL2001PLC000003", "DAWN CAPITAL LIMITED") }, Apply);

        Assert.Equal(ResolutionReasonCodes.FormerNameConflict, decision.ReasonCode);
        Assert.Null(decision.ChosenIdentifier);
    }

    [Fact]
    public void Several_companies_that_held_the_name_before_are_offered_together()
    {
        const string asked = "STAR TRADERS LIMITED";
        var lookup = Found(Hit("U55555DL2001PLC000005", asked, "ALPHA LIMITED"), Hit("U66666DL2001PLC000006", asked, "BETA LIMITED"));
        var decision = FormerNameResolver.Decide(asked, ResolutionHints.None, MasterDecision(asked, Apply), lookup, new Dictionary<string, MasterCandidate>(), Apply);

        Assert.Equal((ResolutionStatus.Ambiguous, ResolutionReasonCodes.FormerNameAmbiguous), (decision.Status, decision.ReasonCode));
        Assert.Equal(2, decision.Candidates.Count);
    }

    [Fact]
    public void Nothing_found_no_search_a_different_former_name_or_a_company_renamed_back_leaves_the_master_s_decision()
    {
        const string asked = "SUNRISE HOLDINGS LIMITED";
        var holder = Row("U22222DL2001PLC000002", asked);
        var master = MasterDecision(asked, Apply, holder);
        var empty = new Dictionary<string, MasterCandidate>();

        Assert.Same(master, FormerNameResolver.Decide(asked, ResolutionHints.None, master, FormerNameLookup.NotSearched, empty, Apply));
        Assert.Same(master, FormerNameResolver.Decide(asked, ResolutionHints.None, master, Found(), empty, Apply));
        Assert.Same(master, FormerNameResolver.Decide(asked, ResolutionHints.None, master, Found(Hit("U33333DL2001PLC000003", "SUNSET HOLDINGS LIMITED", "X LIMITED")), empty, Apply));
        Assert.Same(master, FormerNameResolver.Decide(asked, ResolutionHints.None, master, Found(Hit("U22222DL2001PLC000002", asked, asked)), empty, Apply));
    }

    // ── The search reply (as recorded) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_search_reply_is_read_with_the_field_names_the_tool_really_sends()
    {
        var hits = await FakeReferenceTool.NewClient(new FixtureReferenceTool("unlocked-stale-coastal-20260922")).SearchCompaniesAsync("tata services", 25, CancellationToken.None);

        var first = hits[0];
        Assert.Equal(("TATA SERVICES LIMITED", "legalName", "Active", "COMPANY"), (first.NameHint, first.Context, first.Status, first.CompanyType));
        Assert.Equal(("maharashtra", "400001", new DateOnly(1957, 9, 24)), (first.State, first.PinCode, first.IncorporationDate!.Value));
        // The recording also holds six "partProp" hits: partnerships and proprietorships, with no CIN. They are not companies we can resolve to,
        // so they are left out, and no former name can come from them.
        Assert.Equal(19, hits.Count);
        Assert.All(hits, h => Assert.False(string.IsNullOrWhiteSpace(h.Cin)));
        Assert.DoesNotContain(hits, h => h.Context == "partProp");
    }

    [Fact]
    public async Task An_old_name_hit_is_read_as_a_former_name()
    {
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 200, new
        {
            data = new[]
            {
                new { cin = BhartiAirtel.ToLowerInvariant(), name_hint = "BHARTI TELE-VENTURES LIMITED", context = "oldName", legal_name = "BHARTI AIRTEL LIMITED",
                      company_llp_status = "Active", entity_type = "COMPANY", incorporation_date = "1995-07-07" }
            }
        });

        var hit = Assert.Single(await FakeReferenceTool.NewClient(tool).SearchCompaniesAsync("Bharti Tele-Ventures Limited", 25, CancellationToken.None));
        Assert.True(hit.IsFormerNameMatch);
        Assert.Equal((BhartiAirtel, "BHARTI AIRTEL LIMITED", "BHARTI TELE-VENTURES LIMITED"), (hit.Cin, hit.LegalName, hit.NameHint));
    }

    // ── The store and the request (SQL) ──────────────────────────────────────────────────────────────────

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 25, 6, 0, 0, TimeSpan.Zero); }

    [Fact]
    public async Task A_former_name_found_by_a_search_is_stored_and_the_next_request_by_it_needs_no_search()
    {
        var former = $"{_word} TELECOM VENTURES LIMITED";
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 200, new
        {
            data = new[]
            {
                new { cin = Cin(1), name_hint = former, context = "oldName", legal_name = $"{_word} COMMUNICATIONS LIMITED", company_llp_status = "Active" },
                new { cin = Cin(2), name_hint = $"{_word} OTHER LIMITED", context = "legalName", legal_name = $"{_word} OTHER LIMITED", company_llp_status = "Active" },
            }
        });
        await using var db = CreateContext();
        var lookup = new FormerNameLookupService(db, FakeReferenceTool.NewClient(tool), new Clock(), NullLogger<FormerNameLookupService>.Instance);

        var first = await lookup.FindAsync(former.ToLowerInvariant(), CancellationToken.None);
        Assert.True(first.Searched);
        Assert.Equal(Cin(1), Assert.Single(first.FormerNames).Identifier);
        var stored = await db.CompanyFormerNames.AsNoTracking().SingleAsync(f => f.Identifier == Cin(1));
        Assert.Equal((former, FormerNameLookupService.SearchSource), (stored.FormerName, stored.Source));

        var again = await lookup.FindAsync(former, CancellationToken.None);
        Assert.Equal(Cin(1), Assert.Single(again.FormerNames).Identifier);
        Assert.Equal(1, tool.Count("getNameHints"));                   // answered from the store
    }

    [Fact]
    public async Task A_search_that_fails_leaves_resolution_to_the_master_as_before()
    {
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 503, new { error = "down" });
        await using var db = CreateContext();
        var lookup = new FormerNameLookupService(db, FakeReferenceTool.NewClient(tool), new Clock(), NullLogger<FormerNameLookupService>.Instance);

        Assert.False((await lookup.FindAsync($"{_word} ANYTHING LIMITED", CancellationToken.None)).Searched);
    }

    private sealed class StubLookup(FormerNameLookup result) : IFormerNameLookup
    {
        public Task<FormerNameLookup> FindAsync(string name, CancellationToken ct) => Task.FromResult(result);
    }

    private async Task<long> RequestAsync(string name)
    {
        await using var db = CreateContext();
        var request = new McaRequest
        {
            Client = new Client { ClientCode = "FN" + Guid.NewGuid().ToString("N")[..8], ClientName = "Former Name Bank", CreatedDate = DateTime.UtcNow },
            EntityType = EntityType.Company, CompanyName = name, RequestNumber = $"FN-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        _requests.Add(request.RequestId);
        return request.RequestId;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_request_by_a_former_name_resolves_to_the_company_as_it_is_registered_today(bool autoSelectFormerName)
    {
        var current = $"{_word} COMMUNICATIONS LIMITED";
        var former = $"{_word} TELECOM VENTURES LIMITED";
        await using (var seed = CreateContext())
        {
            seed.CompanyMasterRecords.Add(new CompanyMasterRecord { Identifier = Cin(1), RecordType = CompanyMasterRecordType.Company, Name = current, Status = "Active" });
            await seed.SaveChangesAsync();
        }
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);
            await CompanyNameTokenIndex.RebuildAsync(connection);
        }
        var requestId = await RequestAsync(former);
        var lookup = new StubLookup(new FormerNameLookup(true, [new FormerNameHit(Cin(1), former, current, "Active", null, null, null)], []));

        await using var db = CreateContext();
        var service = new IdentityResolutionService(db,
            Options.Create(new ResolverOptions { AutoSelectEnabled = true, AutoSelectFormerName = autoSelectFormerName }), lookup);
        var result = await service.ResolveForRequestAsync(requestId, former, null, ResolutionHints.None, "test", CancellationToken.None);

        await using var read = CreateContext();
        var request = await read.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);
        var audit = await read.IdentityResolutions.AsNoTracking().SingleAsync(r => r.IdentityResolutionId == result.IdentityResolutionId);
        if (autoSelectFormerName)
        {
            Assert.True(result.AppliedToRequest);
            Assert.Equal((Cin(1), ResolutionReasonCodes.FormerName), (request.Cin, audit.ReasonCode));
        }
        else
        {
            Assert.False(result.AppliedToRequest);
            Assert.Null(request.Cin);
            Assert.Equal((ResolutionReasonCodes.FormerNameNeedsConfirmation, Cin(1)), (audit.ReasonCode, audit.RecommendedIdentifier));
        }
        Assert.Equal(former, audit.InputName);                          // the name the client asked for stays on record
    }
}
