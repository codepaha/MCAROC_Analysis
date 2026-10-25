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
        await db.CompanyNameSearches.Where(x => x.NameNormalized.Contains(_word)).ExecuteDeleteAsync();
        await db.CompanyNameTokens.Where(t => t.Identifier.EndsWith(_serial)).ExecuteDeleteAsync();
        await db.CompanyMasterRecords.Where(r => r.Identifier.EndsWith(_serial)).ExecuteDeleteAsync();
    }

    // ── The rules (pure) ─────────────────────────────────────────────────────────────────────────────────

    private static MasterCandidate Row(string id, string name, string status = "Active", string? state = null) =>
        new(id, CompanyMasterRecordType.Company, name, Status: status, State: state);

    private static ResolutionDecision MasterDecision(string input, ResolverOptions o, params MasterCandidate[] master) =>
        CompanyNameResolver.Decide(input, CompanyNameResolver.Rank(input, ResolutionHints.None, master), o);

    private static FormerNameLookup Found(params FormerNameHit[] hits) => new(true, true, hits, []);
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
        var lookup = new FormerNameLookup(true, true, [Hit("U33333DL2001PLC000003", asked, "DAWN CAPITAL LIMITED")],
            [new CompanyHolder("U44444DL2001PLC000004", asked, "Active", "Delhi", null, null)]);
        var decision = FormerNameResolver.Decide(asked, ResolutionHints.None, MasterDecision(asked, Apply), lookup,
            new Dictionary<string, MasterCandidate> { ["U33333DL2001PLC000003"] = Row("U33333DL2001PLC000003", "DAWN CAPITAL LIMITED") }, Apply);

        Assert.Equal(ResolutionReasonCodes.FormerNameConflict, decision.ReasonCode);
        Assert.Null(decision.ChosenIdentifier);
        // Review of #533: the holder only the search knows about is one of the choices, marked as such.
        var holder = Assert.Single(decision.Candidates, c => c.Candidate.Identifier == "U44444DL2001PLC000004");
        Assert.True(holder.Candidate.IsToolOnly);
        Assert.Equal(asked, holder.Candidate.Name);
        Assert.Contains("has this name today, by the reference tool's search", holder.Reasons);
        Assert.Contains(decision.Candidates, c => c.Candidate.Identifier == "U33333DL2001PLC000003");
    }

    /// <summary>Review of #533: a search not read to its end cannot show that no other company had the name, or has it today.</summary>
    [Fact]
    public void A_search_not_read_to_its_end_never_lets_a_former_name_be_chosen_automatically()
    {
        const string asked = "Bharti Tele-Ventures Limited";
        var rows = new Dictionary<string, MasterCandidate> { [BhartiAirtel] = Row(BhartiAirtel, "BHARTI AIRTEL LIMITED") };
        var partial = new FormerNameLookup(true, false, [Hit(BhartiAirtel, "BHARTI TELE-VENTURES LIMITED", "BHARTI AIRTEL LIMITED")], []);

        var decision = FormerNameResolver.Decide(asked, ResolutionHints.None, MasterDecision(asked, Apply), partial, rows, Apply);

        Assert.Equal((ResolutionStatus.NeedsConfirmation, ResolutionReasonCodes.FormerNameNeedsConfirmation), (decision.Status, decision.ReasonCode));
        Assert.False(decision.AutoSelectEligible);
        Assert.Contains(decision.Candidates[0].Reasons, r => r.Contains("could not be read to its end"));
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

    /// <summary>Review of #533 (a0104fd): a search not read to its end that shows no former holder still cannot rule one out on the pages it did not
    /// read, so the master's own automatic pick (here #495's unique exact match) is offered instead of chosen. A complete search changes nothing.</summary>
    [Fact]
    public void An_automatic_pick_waits_for_confirmation_when_the_former_name_search_was_not_read_to_its_end()
    {
        const string asked = "SUNRISE HOLDINGS LIMITED";
        var master = MasterDecision(asked, Apply, Row("U22222DL2001PLC000002", asked));
        Assert.Equal((ResolutionStatus.Resolved, ResolutionReasonCodes.UniqueExact), (master.Status, master.ReasonCode));
        var empty = new Dictionary<string, MasterCandidate>();

        var partial = FormerNameResolver.Decide(asked, ResolutionHints.None, master, new FormerNameLookup(true, false, [], []), empty, Apply);
        Assert.Equal((ResolutionStatus.NeedsConfirmation, ResolutionReasonCodes.FormerNameCheckIncomplete, "U22222DL2001PLC000002"),
            (partial.Status, partial.ReasonCode, partial.RecommendedIdentifier));
        Assert.Null(partial.ChosenIdentifier);
        Assert.False(partial.AutoSelectEligible);

        Assert.Same(master, FormerNameResolver.Decide(asked, ResolutionHints.None, master, new FormerNameLookup(true, true, [], []), empty, Apply));
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

    private static Dictionary<string, object?> Company(string cin, string name, string context, string? hint = null) => new()
    {
        ["cin"] = cin, ["bid"] = "b-" + cin, ["name_hint"] = hint ?? name, ["context"] = context, ["legal_name"] = name, ["company_llp_status"] = "Active"
    };

    private static Dictionary<string, object?> Proprietorship(int n) => new()
    {
        ["cin"] = null, ["bid"] = $"p-{n}", ["name_hint"] = $"FIRM {n}", ["context"] = "partProp", ["legal_name"] = $"FIRM {n}", ["entity_type"] = "PROPRIETORSHIP"
    };

    /// <summary>Seeds the former holder A in the master (Active), so only the guards stand between the request and choosing it.</summary>
    private async Task<(string Former, string CurrentOfA)> SeedFormerHolderAsync()
    {
        var currentOfA = $"{_word} COMMUNICATIONS LIMITED";
        await using (var seed = CreateContext())
        {
            seed.CompanyMasterRecords.Add(new CompanyMasterRecord { Identifier = Cin(1), RecordType = CompanyMasterRecordType.Company, Name = currentOfA, Status = "Active" });
            await seed.SaveChangesAsync();
        }
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);
        await CompanyNameTokenIndex.RebuildAsync(connection);
        return ($"{_word} TELECOM VENTURES LIMITED", currentOfA);
    }

    private async Task<(IdentityResolutionResult Result, IdentityResolution Audit, McaRequest Request)> ResolveTwiceAsync(FixtureReferenceTool tool, string asked, int run)
    {
        var requestId = await RequestAsync(asked);
        await using var db = CreateContext();
        var lookup = new FormerNameLookupService(db, FakeReferenceTool.NewClient(tool), new Clock(), NullLogger<FormerNameLookupService>.Instance);
        var service = new IdentityResolutionService(db, Options.Create(new ResolverOptions { AutoSelectEnabled = true, AutoSelectFormerName = true }), lookup);
        var result = await service.ResolveForRequestAsync(requestId, asked, null, ResolutionHints.None, $"run-{run}", CancellationToken.None);
        await using var read = CreateContext();
        return (result, await read.IdentityResolutions.AsNoTracking().SingleAsync(r => r.IdentityResolutionId == result.IdentityResolutionId),
            await read.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId));
    }

    /// <summary>Review of #533: the first page is full of other rows (companies and proprietorships) with former holder A among them; the company holding
    /// the name today (B, known only to the search) is on page 2. Read to the end, the search finds the conflict; and a repeat request, answered from the
    /// stored search, keeps it. Neither request chooses A, applies anything, or becomes spend-trusted.</summary>
    [Fact]
    public async Task A_current_holder_on_a_later_page_is_found_and_the_stored_search_keeps_the_conflict()
    {
        var (former, currentOfA) = await SeedFormerHolderAsync();
        var page1 = Enumerable.Range(10, 20).Select(n => Company(Cin(n), $"{_word} UNRELATED {n} LIMITED", "legalName"))
            .Concat(Enumerable.Range(1, 4).Select(Proprietorship))
            .Append(Company(Cin(1), currentOfA, "oldName", former)).ToList();
        Assert.Equal(FormerNameLookupService.PageSize, page1.Count);
        var page2 = new[] { Company(Cin(2), former, "legalName") };
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922").WithReplies("getNameHints", 200, new { data = page1 }, new { data = page2 });

        for (var run = 1; run <= 2; run++)
        {
            var (result, audit, request) = await ResolveTwiceAsync(tool, former, run);
            Assert.Equal((ResolutionStatus.Ambiguous, ResolutionReasonCodes.FormerNameConflict), (audit.Status, audit.ReasonCode));
            Assert.False(result.AppliedToRequest);
            Assert.Null(request.Cin);
            Assert.Null(audit.ChosenIdentifier);
            Assert.Contains(Cin(2), audit.CandidatesJson);              // B is one of the choices
            Assert.Contains(Cin(1), audit.CandidatesJson);              // and so is A
        }
        Assert.Equal(2, tool.Count("getNameHints"));                    // two pages, once: the repeat came from the stored search
    }

    /// <summary>Review of #533: when the tool returns the same full page whatever the offset, the search cannot be read to its end. A former holder is
    /// then only offered, never chosen; and the stored search remembers that it was incomplete.</summary>
    [Fact]
    public async Task A_search_whose_paging_does_not_work_is_incomplete_and_never_chooses_automatically()
    {
        var (former, currentOfA) = await SeedFormerHolderAsync();
        var fullPage = Enumerable.Range(10, 24).Select(n => Company(Cin(n), $"{_word} UNRELATED {n} LIMITED", "legalName"))
            .Append(Company(Cin(1), currentOfA, "oldName", former)).ToList();
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 200, new { data = fullPage });

        for (var run = 1; run <= 2; run++)
        {
            var (result, audit, request) = await ResolveTwiceAsync(tool, former, run);
            Assert.Equal((ResolutionStatus.NeedsConfirmation, ResolutionReasonCodes.FormerNameNeedsConfirmation), (audit.Status, audit.ReasonCode));
            Assert.False(audit.AutoSelectEligible);
            Assert.False(result.AppliedToRequest);
            Assert.Null(request.Cin);
        }
        await using var db = CreateContext();
        Assert.False((await db.CompanyNameSearches.AsNoTracking().SingleAsync(s => s.NameNormalized == CompanyNameNormalizer.Normalize(former).NameNormalized)).Complete);
    }

    /// <summary>Review of #533 (a0104fd), end to end: the master has an exact, unique, Active holder of the name (which #495 alone would choose), and the
    /// tool returns the same full page for every offset with no former holder in it. The page is not ranked past the exact matches (the exact
    /// holder comes after near names), so the search is incomplete and the request is not applied.</summary>
    [Fact]
    public async Task A_unique_current_name_is_not_chosen_when_the_former_name_search_could_not_be_completed()
    {
        var holderName = $"{_word} HOLDINGS LIMITED";
        await using (var seed = CreateContext())
        {
            seed.CompanyMasterRecords.Add(new CompanyMasterRecord { Identifier = Cin(3), RecordType = CompanyMasterRecordType.Company, Name = holderName, Status = "Active" });
            await seed.SaveChangesAsync();
        }
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);
            await CompanyNameTokenIndex.RebuildAsync(connection);
        }
        var fullPage = Enumerable.Range(10, 24).Select(n => Company(Cin(n), $"{_word} UNRELATED {n} LIMITED", "legalName"))
            .Append(Company(Cin(3), holderName, "legalName")).ToList();
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 200, new { data = fullPage });

        var (result, audit, request) = await ResolveTwiceAsync(tool, holderName, 1);

        Assert.Equal((ResolutionStatus.NeedsConfirmation, ResolutionReasonCodes.FormerNameCheckIncomplete, Cin(3)),
            (audit.Status, audit.ReasonCode, audit.RecommendedIdentifier));
        Assert.False(result.AppliedToRequest);
        Assert.Null(request.Cin);
    }

    /// <summary>The #514 round (2026-10-25): "TATA SERVICES LIMITED", name only. The tool returned its 25 best matches with the exact name first and near names
    /// after ("TATA INDUSTRIES LIMITED", the company's own former name "TATA SERVICES PRIVATE LIMITED", ...), and the same 25 again for the next page.
    /// Ranked past the exact matches, the page holds every exact match there is: the check is complete, nothing conflicts, and the master's unique exact
    /// match is chosen, with one search call (no second page).</summary>
    [Fact]
    public async Task A_page_ranked_past_the_exact_matches_completes_the_check_and_the_unique_name_is_chosen()
    {
        var holderName = $"{_word} SERVICES LIMITED";
        await using (var seed = CreateContext())
        {
            seed.CompanyMasterRecords.Add(new CompanyMasterRecord { Identifier = Cin(3), RecordType = CompanyMasterRecordType.Company, Name = holderName, Status = "Active" });
            await seed.SaveChangesAsync();
        }
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);
            await CompanyNameTokenIndex.RebuildAsync(connection);
        }
        var ranked = new List<Dictionary<string, object?>> { Company(Cin(3), holderName, "legalName"), Company(Cin(11), $"{_word} INDUSTRIES LIMITED", "legalName"),
                Company(Cin(3), holderName, "oldName", $"{_word} SERVICES PRIVATE LIMITED") }
            .Concat(Enumerable.Range(12, 19).Select(n => Company(Cin(n), $"{_word} UNRELATED {n} SERVICES LIMITED", "legalName")))
            .Concat(Enumerable.Range(1, 3).Select(Proprietorship)).ToList();
        Assert.Equal(25, ranked.Count);
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 200, new { data = ranked });

        var (result, audit, request) = await ResolveTwiceAsync(tool, holderName, 1);

        Assert.Equal((ResolutionStatus.Resolved, ResolutionReasonCodes.UniqueExact, Cin(3)), (audit.Status, audit.ReasonCode, audit.ChosenIdentifier));
        Assert.True(result.AppliedToRequest);
        Assert.Equal(Cin(3), request.Cin);
        Assert.Equal(1, tool.Count("getNameHints"));
    }

    /// <summary>A search stored as incomplete is not reused: the next request searches again (free), so a search whose page is now read as ranked, or that
    /// the tool now answers in full, settles it.</summary>
    [Fact]
    public async Task A_stored_incomplete_search_is_searched_again()
    {
        var name = $"{_word} RETRY LIMITED";
        var mixed = Enumerable.Range(10, 24).Select(n => Company(Cin(n), $"{_word} UNRELATED {n} LIMITED", "legalName"))
            .Append(Company(Cin(4), name, "legalName")).ToList();
        var first = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 200, new { data = mixed });
        await using (var db = CreateContext())
            Assert.False((await new FormerNameLookupService(db, FakeReferenceTool.NewClient(first), new Clock(), NullLogger<FormerNameLookupService>.Instance)
                .FindAsync(name, CancellationToken.None)).Complete);

        var ranked = new[] { Company(Cin(4), name, "legalName") }.Concat(Enumerable.Range(10, 24).Select(n => Company(Cin(n), $"{_word} UNRELATED {n} LIMITED", "legalName"))).ToList();
        var second = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 200, new { data = ranked });
        await using (var db = CreateContext())
            Assert.True((await new FormerNameLookupService(db, FakeReferenceTool.NewClient(second), new Clock(), NullLogger<FormerNameLookupService>.Instance)
                .FindAsync(name, CancellationToken.None)).Complete);
        Assert.Equal(1, second.Count("getNameHints"));

        // Complete now, it is reused: no further call.
        var third = new FixtureReferenceTool("unlocked-stale-coastal-20260922").With("getNameHints", 200, new { data = mixed });
        await using (var db = CreateContext())
            Assert.True((await new FormerNameLookupService(db, FakeReferenceTool.NewClient(third), new Clock(), NullLogger<FormerNameLookupService>.Instance)
                .FindAsync(name, CancellationToken.None)).Complete);
        Assert.Equal(0, third.Count("getNameHints"));
    }

    [Theory]
    [InlineData("TATA SERVICES LIMITED", new[] { "TATA SERVICES LIMITED", "TATA INDUSTRIES LIMITED", "TATA SERVICES PRIVATE LIMITED" }, true)]   // exact first, then near
    [InlineData("TATA SERVICES LTD", new[] { "Tata Services Limited", "TATA SERVICES LIMITED", "TATA SONS PRIVATE LIMITED" }, true)]          // several exact, spelt differently
    [InlineData("HDFC LIMITED", new[] { "HDFC BANK LIMITED", "HDFC CREDILA FINANCIAL SERVICES LIMITED" }, true)]                              // no exact match at all
    [InlineData("ALPHA LIMITED", new[] { "ALPHA TRADING LIMITED", "ALPHA LIMITED" }, false)]                                                 // an exact match below a near name
    [InlineData("ALPHA LIMITED", new[] { "ALPHA LIMITED", "ALPHA LTD" }, false)]                                                              // nothing but exact matches
    [InlineData("ALPHA LIMITED", new string[0], false)]
    public void A_page_is_ranked_past_the_exact_matches_only_when_they_all_come_first_and_a_near_name_follows(string query, string[] rows, bool ranked) =>
        Assert.Equal(ranked, MCAROC_Analysis.Services.AutoFetch.ReferenceToolClient.RankedPastExactMatches(query, rows));

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
        var lookup = new StubLookup(new FormerNameLookup(true, true, [new FormerNameHit(Cin(1), former, current, "Active", null, null, null)], []));

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
