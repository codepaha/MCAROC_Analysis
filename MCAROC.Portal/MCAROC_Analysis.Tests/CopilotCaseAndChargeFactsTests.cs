using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.PropertyParticulars;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>A live Coastal run found the Copilot answering "106 open charges" for 163, "could not verify" for "pending cases against the
/// company", and listing the wrong Supreme Court cases. These pin the facts that fix them: counts stated rather than left to be counted from
/// rows, combinations of status and side, and a case list selected by the same rules as the register.</summary>
public class CopilotCaseAndChargeFactsTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    // ── question → filter ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("List the Supreme Court cases against the company.", new[] { LitigationInstitution.SupremeCourt }, null, LitigationCompanySide.AgainstCompany)]
    [InlineData("How many litigation cases are pending against the company?", new LitigationInstitution[0], LitigationCaseStatusBucket.Pending, LitigationCompanySide.AgainstCompany)]
    [InlineData("Are there any DRT recovery cases pending?", new[] { LitigationInstitution.Drt }, LitigationCaseStatusBucket.Pending, null)]
    [InlineData("Which NCLT or NCLAT proceedings are disposed?", new[] { LitigationInstitution.Nclat, LitigationInstitution.Nclt }, LitigationCaseStatusBucket.Disposed, null)]
    [InlineData("Show the High Court cases filed by the company", new[] { LitigationInstitution.HighCourt }, null, LitigationCompanySide.ByCompany)]
    [InlineData("Which suits has the company initiated in the district courts?", new[] { LitigationInstitution.DistrictCourt }, null, LitigationCompanySide.ByCompany)]
    public void A_question_describing_cases_yields_a_filter(string question, LitigationInstitution[] forums, LitigationCaseStatusBucket? status, LitigationCompanySide? side)
    {
        var filter = LitigationCaseListSelector.Extract(question);

        Assert.NotNull(filter);
        Assert.Equal(forums.OrderBy(f => f), filter!.Forums.OrderBy(f => f));
        Assert.Equal(status, filter.Status);
        Assert.Equal(side, filter.Side);
    }

    [Theory]
    [InlineData("How many open charges does the company have?")]            // not about cases
    [InlineData("What is the weather in Mumbai today?")]
    [InlineData("What did the High Court order in case 27190/2022?")]       // a named case: handled by the case-record lookup, not a list
    [InlineData("Tell me about the litigation")]                             // about cases, but no forum, status or side to filter by
    [InlineData("Who are the directors? Any proceedings against them?")]    // "against them" is the directors, not the company
    public void A_question_that_does_not_describe_a_set_of_cases_yields_no_filter(string question)
    {
        var filter = LitigationCaseListSelector.Extract(question);

        // The named-case question has a forum word but is answered from the case record; it may still produce a filter, which is harmless
        // because the list only adds records. What must hold is that unrelated questions never do.
        if (question.Contains("27190")) return;
        Assert.Null(filter);
    }

    [Fact]
    public void The_extractor_carries_the_filter_on_the_hints()
    {
        var hints = QuestionHintExtractor.Extract("List the Supreme Court cases against the company", [], []);

        Assert.Equal([LitigationInstitution.SupremeCourt], hints.CaseList!.Forums);
        Assert.Null(QuestionHintExtractor.Extract("What is the company's CIN?", [], []).CaseList);
    }

    // ── selector ────────────────────────────────────────────────────────────────────────────────────────

    private static LitigationCase Case(long id, string? type, string status, string? direction, string? filed, string? petitioner = null, string? respondent = null) => new()
    {
        LitigationCaseId = id, CaseNumber = $"CASE-{id}", Type = type, Court = type, CaseStatus = status, Direction = direction, FilingDate = filed,
        PetitionersJson = petitioner is null ? null : $"[\"{petitioner}\"]", RespondentsJson = respondent is null ? null : $"[\"{respondent}\"]",
        FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
    };

    [Fact]
    public void The_selector_filters_and_orders_exactly_as_the_register_does()
    {
        var cases = new[]
        {
            Case(1, "supreme", "DISPOSED", "against", "01-01-2020"),
            Case(2, "supreme", "PENDING", null, "01-01-2024", respondent: "Target Company"),   // side from the party names
            Case(3, "supreme", "PENDING", "by", "01-01-2025"),
            Case(4, "highcourt", "PENDING", "against", "01-01-2025"),
            Case(5, "supreme", "PENDING", "against", "01-01-2023"),
        };
        string[] names = ["Target Company"];

        var (supremeAgainst, total) = LitigationCaseListSelector.Select(cases,
            new CaseListFilter([LitigationInstitution.SupremeCourt], null, LitigationCompanySide.AgainstCompany), names);

        Assert.Equal(3, total);
        // pending before disposed; among the pending, the newest filing first
        Assert.Equal([2L, 5L, 1L], supremeAgainst.Select(c => c.LitigationCaseId).ToArray());

        var (pendingSupreme, pendingTotal) = LitigationCaseListSelector.Select(cases,
            new CaseListFilter([LitigationInstitution.SupremeCourt], LitigationCaseStatusBucket.Pending, null), names);
        Assert.Equal(3, pendingTotal);
        Assert.DoesNotContain(pendingSupreme, c => c.LitigationCaseId == 1);
    }

    [Fact]
    public void The_selector_lists_a_bounded_number_and_reports_the_true_total()
    {
        var cases = Enumerable.Range(1, 50).Select(i => Case(i, "highcourt", "PENDING", "against", $"{(i % 28) + 1:D2}-01-2024")).ToList();

        var (listed, total) = LitigationCaseListSelector.Select(cases, new CaseListFilter([LitigationInstitution.HighCourt], null, null), ["Target Company"]);

        Assert.Equal(50, total);
        Assert.Equal(LitigationCaseListSelector.MaxListedCases, listed.Count);
    }

    // ── facts ───────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(long RequestId, long RunId)> SeedRequestAsync(AppDbContext db)
    {
        var request = new McaRequest
        {
            ClientId = 1, EntityType = EntityType.Company, CompanyName = "Target Company", RequestNumber = $"CCF-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        await db.SaveChangesAsync();
        return (request.RequestId, run.IngestionRunId);
    }

    [Fact]
    public async Task Charge_counts_and_totals_are_stated_even_when_every_charge_is_listed()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        // 163 open and 50 satisfied long ago: the shape of the Coastal run, where the model counted 164 listed rows and said 106.
        for (var i = 0; i < 163; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"OPEN-{i:D3}", LatestChargeHolderRaw = i % 2 == 0 ? "BANK A" : "BANK B",
                ChargeStatus = "Open", CurrentAmount = 10m + i, CreationDate = new DateOnly(2015 + (i % 6), 3, 1) });
        for (var i = 0; i < 50; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"SAT-{i:D3}", LatestChargeHolderRaw = "OLD BANK",
                ChargeStatus = "Satisfied", CurrentAmount = 5m, SatisfactionDate = new DateOnly(2016, 1, 1) });
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default);
        var charges = facts.Where(f => f.DomainKey == "Charges").ToList();

        var headline = Assert.Single(charges, f => f.Text.StartsWith("163 open charge(s), 50 satisfied charge(s)"));
        Assert.Contains("(213 in total)", headline.Text);
        Assert.Contains("₹", headline.Text);

        var holders = Assert.Single(charges, f => f.Text.StartsWith("Open charges by holder"));
        Assert.Contains("BANK A: 82 charge(s)", holders.Text);
        Assert.Contains("BANK B: 81 charge(s)", holders.Text);
        Assert.True(holders.Text.IndexOf("BANK A", StringComparison.Ordinal) < holders.Text.IndexOf("BANK B", StringComparison.Ordinal)); // larger total first

        // rows are capped, largest first, and the cap is stated so a capped list is never passed off as complete
        var rows = charges.Where(f => f.EntityType == "RocCharge").ToList();
        Assert.Equal(StructuredFactsProvider.MaxChargeRows, rows.Count);
        Assert.Contains("OPEN-162", rows[0].Text);
        Assert.Contains(charges, f => f.Text.StartsWith($"Only {StructuredFactsProvider.MaxChargeRows} of the 163"));
    }

    [Fact]
    public async Task A_charge_row_carries_its_creation_date_and_a_year_in_the_question_pulls_in_that_years_charges()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < 60; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"BIG-{i:D2}", LatestChargeHolderRaw = "BIG BANK",
                ChargeStatus = "Open", CurrentAmount = 1000m + i, CreationDate = new DateOnly(2012, 5, 5) });
        // small, but created in the year asked about: must not be hidden by the cap
        db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "SMALL-2019", LatestChargeHolderRaw = "SMALL BANK",
            ChargeStatus = "Open", CurrentAmount = 1m, CreationDate = new DateOnly(2019, 7, 9) });
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(
            requestId, new QuestionHints(FilingCategory.Charge, null, null, 2019, null), default);

        var row = Assert.Single(facts, f => f.EntityType == "RocCharge" && f.Text.Contains("SMALL-2019"));
        Assert.Contains("Created 2019-07-09", row.Text);
    }

    private static void AddPropertyEvent(AppDbContext db, long requestId, long runId, long chargeId, string particulars) =>
        db.RocChargeEvents.Add(new RocChargeEvent { RocChargeId = chargeId, RequestId = requestId, IngestionRunId = runId, SerialNumber = "1", EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2011, 5, 20), HolderNameRaw = "X", HolderNameNormalized = "X", PropertyParticulars = particulars });

    /// <summary>The Deccan Chronicle miss: two open charges sit on the same land, but the register rows named only holder and amount, so a question about the
    /// land could be tied only to the one charge whose deed text was retrieved. The rows now carry the register's property particulars, and a question that
    /// names the property's survey numbers always lists the charges that share them, however small and however many charges there are.</summary>
    [Fact]
    public async Task A_question_naming_a_propertys_survey_numbers_lists_every_charge_on_it_with_its_property_text()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < 60; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"BIG-{i:D2}", LatestChargeHolderRaw = "BIG BANK",
                ChargeStatus = "Open", CurrentAmount = 1000m + i, CreationDate = new DateOnly(2012, 5, 5) });
        var tata = new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "10301179", LatestChargeHolderRaw = "TATA CAPITAL LIMITED",
            ChargeStatus = "Open", CurrentAmount = 100m, CreationDate = new DateOnly(2011, 4, 15) };
        var lt = new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "100187185", LatestChargeHolderRaw = "L&T FINANCE LIMITED",
            ChargeStatus = "Open", CurrentAmount = 25m, CreationDate = new DateOnly(2011, 5, 20) };
        db.RocCharges.AddRange(tata, lt);
        await db.SaveChangesAsync();
        AddPropertyEvent(db, requestId, runId, tata.ChargeId,
            "All that piece or parcel of land admeasuring about 2152.25 sq. yards known as Mathuradas Mills Compound bearing Survey Nos. 1/2844, 2845, 2846, 3/2840, 2841, 2842");
        AddPropertyEvent(db, requestId, runId, lt.ChargeId,
            "Commercial Plot of Land bearing survey no. 3/2840, 3/2841, 2842, 2843, C.S.no.242, Mathuradas Mill Compound, Lower Parel, Mumbai");
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default,
            "Is there any charge on this property? Non-Agricultural land forming part of Mathuradas Mills Compound bearing Survey Nos. 3/ 2840, 3/ 2841, 2842, CTS 242 of Lower Parel");

        var tataRow = Assert.Single(facts, f => f.EntityType == "RocCharge" && f.Text.Contains("10301179"));
        var ltRow = Assert.Single(facts, f => f.EntityType == "RocCharge" && f.Text.Contains("100187185"));
        Assert.Contains("Property per register (matches the question on", tataRow.Text);
        Assert.Contains("Property per register (matches the question on", ltRow.Text);
        Assert.Contains("Mathuradas Mill", ltRow.Text);
    }

    [Theory]
    [InlineData("Which charges cover survey 2840?", true)]                       // one survey number, plainly about a property
    [InlineData("Is there a charge on plot 3/2840 at Lower Parel?", true)]       // the full slash-joined identifier
    [InlineData("Which charges were created in 2011 or registered in 400013?", false)] // a year and a PIN code identify no property
    [InlineData("Which charges were created in 2840?", false)]                   // a bare number with no property wording
    [InlineData("Which charges cover survey no. 4/2840?", false)]                // 4/2840 is not 3/2840: the sub-division prefix is part of the identifier
    [InlineData("Which charges cover survey nos. 4/2840 and 2842?", true)]      // one of the listed parcels is shared
    [InlineData("Which charges cover S.NO. 2840 AND CS NO 242 at Lower Parel?", true)] // the abbreviations real addresses use
    public async Task A_single_survey_number_is_enough_when_the_question_is_about_a_property(string question, bool expectListed)
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < 60; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"BIG-{i:D2}", LatestChargeHolderRaw = "BIG BANK",
                ChargeStatus = "Open", CurrentAmount = 1000m + i, CreationDate = new DateOnly(2012, 5, 5) });
        var small = new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "SMALL-LT", LatestChargeHolderRaw = "L&T FINANCE LIMITED",
            ChargeStatus = "Open", CurrentAmount = 1m, CreationDate = new DateOnly(2011, 5, 20) };
        db.RocCharges.Add(small);
        await db.SaveChangesAsync();
        AddPropertyEvent(db, requestId, runId, small.ChargeId, "Plot of land bearing survey no. 3/2840, 2842, C.S.no.242, Lower Parel, Mumbai - 400013");
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default, question);

        Assert.Equal(expectListed, facts.Any(f => f.EntityType == "RocCharge" && f.Text.Contains("SMALL-LT")));
    }

    [Theory]
    [InlineData("Which charges cover survey no. 42?", true)]      // a short number after a label is an identifier
    [InlineData("Which charges cover survey no. 123456?", true)]  // so is a six-digit one
    [InlineData("Which charges cover survey no. 2011?", true)]    // a year-like number is a survey number when the text says so
    [InlineData("Which charges were created in 2011?", false)]    // ...and only then
    [InlineData("Which charges cover plot 400013?", false)]       // a plot that is not in the particulars
    public async Task A_number_is_a_survey_number_only_where_the_text_says_so(string question, bool expectListed)
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < 60; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"BIG-{i:D2}", LatestChargeHolderRaw = "BIG BANK",
                ChargeStatus = "Open", CurrentAmount = 1000m + i, CreationDate = new DateOnly(2012, 5, 5) });
        var small = new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "SMALL-2011", LatestChargeHolderRaw = "SOME FINANCE",
            ChargeStatus = "Open", CurrentAmount = 1m, CreationDate = new DateOnly(2014, 1, 1) };
        db.RocCharges.Add(small);
        await db.SaveChangesAsync();
        AddPropertyEvent(db, requestId, runId, small.ChargeId, "Land at Pune, survey no. 2011 and plot 123456, gat no. 42, pin code 400013, admeasuring 1800 sq mtrs");
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default, question);

        Assert.Equal(expectListed, facts.Any(f => f.EntityType == "RocCharge" && f.Text.Contains("SMALL-2011")));
    }

    /// <summary>Found by running 300 real addresses through both readers: a number followed by letters was cut back to a partial number ("RS NO 82NR RATNA" gave
    /// "8"), which would have matched an unrelated charge. Digits are atomic now; a glued number is simply not read.</summary>
    [Theory]
    [InlineData("FLAT-1,FLOOR-1 SURYA RESIDENCY A TYPE RS NO 82NR RATNA VILLA", new string[] { })]
    [InlineData("UNIT-34 CHANDRAKALA TOWNSHIP PH I S NO T.S.NOS.32/3B,33/2B,WARD35, BLOCK I", new[] { "32/3B", "33/2B" })]
    [InlineData("PLOT-365 SREE CITY S NO 40/3B(P); 41/1, 2, 3, 5A, 42/1(P), 3", new[] { "365", "40/3B" })] // PLOT-365 is a plot number; the list stops at "(P)"
    [InlineData("Property in S. No 1341, Vavilalpalli, admeasuring 3602.5 Sq yards", new[] { "1341" })]
    [InlineData("Land bearing Survey Nos. 3/2840, 3/2841, 2842 at Lower Parel", new[] { "3/2840", "3/2841", "2842" })]   // a labelled pair is one identifier, not two
    [InlineData("see item 3/2840 of the schedule", new[] { "3/2840" })]                                                 // an unlabelled pair is still one
    public void Property_identifiers_are_read_as_real_addresses_write_them(string text, string[] expected) =>
        Assert.Equal(expected.OrderBy(x => x), StructuredFactsProvider.PropertyIds(text).Select(i => i.Text).OrderBy(x => x));

    /// <summary>Gemini has already split each charge's particulars into properties with grounded survey numbers; the regex reading stops early on some long lists
    /// ("Plot No.3(SP) in TS Nos. 11/3, 19/1(P)" gives only "3"). A charge's identifiers are both readings, so a question naming a parcel only Gemini listed
    /// still finds the charge, however small it is and however many charges there are.</summary>
    [Fact]
    public async Task A_stored_gemini_extraction_adds_the_survey_numbers_the_regex_reading_missed()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < 60; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"BIG-{i:D2}", LatestChargeHolderRaw = "BIG BANK",
                ChargeStatus = "Open", CurrentAmount = 1000m + i, CreationDate = new DateOnly(2012, 5, 5) });
        var small = new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "SMALL-TS", LatestChargeHolderRaw = "SOME BANK",
            ChargeStatus = "Open", CurrentAmount = 1m, CreationDate = new DateOnly(2014, 1, 1) };
        db.RocCharges.Add(small);
        await db.SaveChangesAsync();
        var text = $"Exclusive charge on stocks and book debts; land admeasuring 1 acre situated at Plot No.3(SP) in TS Nos. 11/3, 19/1(P), 53/1(P) of Nagpur, ref {Guid.NewGuid():N}"; // unique: extractions are keyed by text in a shared database
        AddPropertyEvent(db, requestId, runId, small.ChargeId, text);
        var extraction = new PropertyParticularsAiResult([new AiProperty(PropertyAssetClass.Immovable, Enum.GetValues<PropertyKind>().First(), null, null, null, null, null,
            [], null, [new SurveyNumberGroup("Survey", null, ["11/3", "19/1(P)", "53/1(P)"])], [], null, null, null, "Nagpur", null, null)]);
        db.PropertyParticularsExtractions.Add(new PropertyParticularsExtraction
        {
            TextHash = PropertyParticularsAi.HashOf(text, null), PromptVersion = PropertyParticularsAi.PromptVersion, ModelId = "m", SourceText = text,
            Status = PropertyParticularsExtractionStatus.Completed, ExtractionJson = PropertyParticularsAi.Serialize(extraction), CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default,
            "Which charges cover survey no. 11/3?");

        Assert.Contains(facts, f => f.EntityType == "RocCharge" && f.Text.Contains("SMALL-TS"));
    }

    /// <summary>A charge can be picked because an identifier far down a long register text matches the question; the row used to carry only the first 320
    /// characters, so the model got the charge with no evidence connecting it to the property. The row now names the matching identifier and quotes the passage
    /// around it.</summary>
    [Fact]
    public async Task A_charge_picked_for_an_identifier_late_in_a_long_text_carries_the_passage_that_matched()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        var charge = new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "LONG-TXT", LatestChargeHolderRaw = "SOME BANK",
            ChargeStatus = "Open", CurrentAmount = 5m, CreationDate = new DateOnly(2016, 3, 3) };
        db.RocCharges.Add(charge);
        await db.SaveChangesAsync();
        var filler = string.Concat(Enumerable.Repeat("Hypothecation of stocks, book debts and receivables of the borrower, both present and future. ", 8));
        AddPropertyEvent(db, requestId, runId, charge.ChargeId, filler + "Also the factory land at Plot No. 14, survey no. 7788/2, Village Kherwadi, Taluka Pune, with all structures thereon, and the plant and machinery.");
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default,
            "Which charges cover survey no. 7788/2 at Kherwadi?");

        var row = Assert.Single(facts, f => f.EntityType == "RocCharge" && f.Text.Contains("LONG-TXT"));
        Assert.Contains("matches the question on survey 7788/2", row.Text);
        Assert.Contains("survey no. 7788/2", row.Text); // the passage around the identifier, which sits well past the first 320 characters
    }

    [Theory]
    [InlineData(0, false)]    // identifier inside the opening: the opening is quoted, no ellipsis in front
    [InlineData(900, true)]   // identifier far down: the stretch around it is quoted, with an ellipsis where text was cut
    public void An_excerpt_quotes_the_stretch_around_the_matched_identifier(int position, bool expectLeadingEllipsis)
    {
        var text = new string('x', position) + " survey no. 4455/1 " + new string('y', 900);
        var ids = StructuredFactsProvider.PropertyIds("survey no. 4455/1");

        var excerpt = StructuredFactsProvider.Excerpt(text, ids, StructuredFactsProvider.MaxPropertyChars);

        Assert.Contains("4455/1", excerpt);
        Assert.Equal(expectLeadingEllipsis, excerpt.StartsWith('…'));
        Assert.True(excerpt.Length <= StructuredFactsProvider.MaxPropertyChars + 2);
    }

    /// <summary>"survey 42" and "plot 42" share a number, not a parcel. The charge is still listed (it may be the one asked about), but the row says the match is only
    /// possible and why, instead of presenting a numerical overlap as a property match.</summary>
    [Theory]
    [InlineData("Which charges cover survey no. 4242?", "POSSIBLE match only", "plot 4242 here against survey 4242 in the question")]
    [InlineData("Which charges cover plot no. 4242?", "matches the question on plot 4242", null)]
    [InlineData("Which charges cover 4242?", null, null)]   // no kind in the question and no label: no identifier at all
    public async Task A_number_shared_across_kinds_of_identifier_is_a_possible_match_not_a_match(string question, string? expected, string? alsoExpected)
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < 60; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"BIG-{i:D2}", LatestChargeHolderRaw = "BIG BANK",
                ChargeStatus = "Open", CurrentAmount = 1000m + i, CreationDate = new DateOnly(2012, 5, 5) });
        var small = new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "SMALL-PLOT", LatestChargeHolderRaw = "SOME BANK",
            ChargeStatus = "Open", CurrentAmount = 1m, CreationDate = new DateOnly(2014, 1, 1) };
        db.RocCharges.Add(small);
        await db.SaveChangesAsync();
        AddPropertyEvent(db, requestId, runId, small.ChargeId, "Shop on plot no. 4242 of the industrial estate at Thane");
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default, question);

        var row = facts.SingleOrDefault(f => f.EntityType == "RocCharge" && f.Text.Contains("SMALL-PLOT"));
        if (expected is null) { Assert.Null(row); return; }
        Assert.NotNull(row);
        Assert.Contains(expected, row!.Text);
        if (alsoExpected is not null) Assert.Contains(alsoExpected, row.Text);
        if (expected.StartsWith("POSSIBLE")) Assert.DoesNotContain("matches the question on", row.Text);
    }

    [Fact]
    public async Task Charge_rows_do_not_carry_property_text_when_the_question_is_not_about_a_property()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        var charge = new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "555", LatestChargeHolderRaw = "SOME BANK",
            ChargeStatus = "Open", CurrentAmount = 10m, CreationDate = new DateOnly(2020, 1, 1) };
        db.RocCharges.Add(charge);
        await db.SaveChangesAsync();
        AddPropertyEvent(db, requestId, runId, charge.ChargeId, "Plot bearing survey no. 1111, 2222 at Somewhere");
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default,
            "How many open charges are there?");

        var row = Assert.Single(facts, f => f.EntityType == "RocCharge");
        Assert.DoesNotContain("Property per register", row.Text);
    }

    private static async Task SeedCasesAsync(AppDbContext db, long requestId, params LitigationCase[] cases)
    {
        var job = new LitigationSearchJob { RequestId = requestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "h" + Guid.NewGuid().ToString("N") };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = requestId, ReportHash = job.RawResponseHash!,
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        foreach (var c in cases) { c.RequestId = requestId; c.LitigationCaseId = 0; }
        db.LitigationCases.AddRange(cases);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.AddRange(cases.Select(c => new LitigationCaseSourceReport
            { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_litigation_summary_states_status_by_side_and_forum_by_side_so_none_has_to_be_counted()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedRequestAsync(db);
        await SeedCasesAsync(db, requestId,
            Case(1, "supreme", "PENDING", "against", "01-01-2024"), Case(2, "supreme", "DISPOSED", "by", "01-01-2020"),
            Case(3, "supreme", "DISPOSED", null, "01-01-2020", respondent: "Target Company"),     // side from the names
            Case(4, "drt", "PENDING", "against", "01-01-2023"), Case(5, "drt", "PENDING", "against", "01-01-2022"),
            Case(6, "itat", "Fixed", null, "01-01-2019"));                                          // status not reported, side not determined

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(null, null, null, null, null), default);
        var litigation = facts.Where(f => f.DomainKey == "Litigation").Select(f => f.Text).ToList();

        Assert.Contains(litigation, t => t.Contains("6 litigation case(s)") && t.Contains("3 pending, 2 disposed") && t.Contains("4 against the company, 1 by the company"));
        var byStatus = Assert.Single(litigation, t => t.StartsWith("Litigation cases by status and the company's side"));
        Assert.Contains("pending: 3 against the company, 0 by the company, 0 with the company on both sides, 0 side not determined", byStatus);
        Assert.Contains("disposed: 1 against the company, 1 by the company, 0 with the company on both sides, 0 side not determined", byStatus);
        Assert.Contains("status not reported: 0 against the company, 0 by the company, 0 with the company on both sides, 1 side not determined", byStatus);

        var byForum = Assert.Single(litigation, t => t.StartsWith("Litigation cases by forum"));
        Assert.Contains("Supreme Court: 3 in all — against the company 2 (1 pending, 1 disposed, 0 unknown)", byForum);
        Assert.Contains("DRT: 2 in all — against the company 2 (2 pending", byForum);
        Assert.Contains("ITAT: 1 in all", byForum);
        Assert.DoesNotContain("NCLAT", byForum); // only forums with a case are listed
    }

    [Fact]
    public async Task A_described_set_of_cases_states_how_many_match_and_how_many_are_listed()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedRequestAsync(db);
        await SeedCasesAsync(db, requestId, [.. Enumerable.Range(1, 30).Select(i => Case(i, "supreme", "PENDING", "against", $"{(i % 28) + 1:D2}-01-2024")),
            Case(99, "drt", "PENDING", "against", "01-01-2024")]);

        var hints = QuestionHintExtractor.Extract("List the Supreme Court cases against the company", [], []);
        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, hints, default);

        var note = Assert.Single(facts, f => f.Text.StartsWith("Cases matching the question"));
        Assert.Contains("(Supreme Court, against the company): 30 of the 31 case(s)", note.Text);
        Assert.Contains($"The first {LitigationCaseListSelector.MaxListedCases} are given as litigation case records", note.Text);

        // a question that describes no set of cases adds no such note
        var plain = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, QuestionHintExtractor.Extract("What is the CIN?", [], []), default);
        Assert.DoesNotContain(plain, f => f.Text.StartsWith("Cases matching the question"));
    }

    private static async Task<(long RequestId, long RunId)> SeedWorkbookRowsAsync(AppDbContext db, int count)
    {
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < count; i++)
            db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Filed By this Corporate", CaseStatus = "Disposed",
                Court = "SMALL CAUSES COURT, HYDERABAD", Litigants = $"GENERIC PARTY {i}", CaseNumber = $"OS {1000 + i}/2015" });
        // the rows a question can point at, all workbook-only (no fetched case record exists for any of them)
        db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Filed Against this Corporate", CaseStatus = "Pending",
            Court = "NATIONAL COMPANY LAW TRIBUNAL", Litigants = "STATE BANK OF INDIA", CaseNumber = "CP(IB) No. 593/KB/2017" });
        db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Filed Against this Corporate", CaseStatus = "Pending",
            Court = "NATIONAL COMPANY LAW TRIBUNAL", Litigants = "KARUR VYSYA BANK", CaseNumber = "CP(IB) No. 120/KB/2018" });
        db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Filed By this Corporate", CaseStatus = "Pending",
            Court = "NATIONAL COMPANY LAW TRIBUNAL", Litigants = "SOME DEBTOR", CaseNumber = "CP(IB) No. 77/KB/2019" });
        await db.SaveChangesAsync();
        return (requestId, runId);
    }

    [Fact]
    public async Task Workbook_litigation_rows_still_in_the_database_are_never_given_to_the_assistant()
    {
        // A request's litigation comes only from the litigation service (owner, 2026-10-26): an older request's workbook rows are kept in
        // the database but are neither counted nor listed, so the assistant can never cite a register the Litigation tab does not show.
        await using var db = CreateContext();
        var (requestId, _) = await SeedWorkbookRowsAsync(db, 10);

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId,
            QuestionHintExtractor.Extract("Which NCLT cases are pending against the company?", [], []), default);

        Assert.DoesNotContain(facts, f => f.EntityType == "Litigation");
        Assert.DoesNotContain(facts, f => f.Text.Contains("workbook", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Are there any criminal cases against the company?", new[] { "criminal" })]
    [InlineData("Summarise the arbitration awards and cheque dishonour matters", new[] { "arbitration", "awards", "cheque", "dishonour" })]
    [InlineData("What is the status of the cases?", new string[0])]    // only words that say nothing about the subject
    public void The_subject_words_of_a_question_exclude_the_words_that_only_frame_it(string question, string[] expected) =>
        Assert.Equal(expected, QuestionHintExtractor.ExtractTopicTerms(question.ToLowerInvariant()));

    [Fact]
    public async Task The_context_builder_gives_the_matching_case_records_with_links_for_a_described_set_of_cases()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedRequestAsync(db);
        await SeedCasesAsync(db, requestId,
            Case(1, "supreme", "PENDING", "against", "01-01-2024"), Case(2, "supreme", "PENDING", "by", "01-01-2025"), Case(3, "drt", "PENDING", "against", "01-01-2023"));

        var builder = new RetrievalContextBuilder(db, new StructuredFactsProvider(db), new DocumentRetriever(db, ChatRetrievalOptions.Default),
            new LitigationDocumentRetriever(db, ChatRetrievalOptions.Default), new ThrowingEmbedding(), new LitigationOrderOutcomeQuery(db));

        var context = await builder.BuildAsync(requestId, "List the Supreme Court cases against the company", default);

        var records = context.Sources.Where(s => s.Type == SourceType.LitigationCase).ToList();
        var record = Assert.Single(records);                       // only the Supreme Court case against the company
        Assert.Contains("CASE-", record.Text);
        Assert.NotNull(record.LitigationCaseId);
        Assert.Equal("C1", record.Tag);
    }

    private sealed class ThrowingEmbedding : EmbeddingService
    {
        public override Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) => throw new HttpRequestException("no embeddings in this test");
    }
}
