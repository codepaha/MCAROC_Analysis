using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Charge-to-case links: each signal's Strong bar, its negatives, and the caveats the summary reports.
/// The linker is DB-free (same style as LitigationOrderAddressMatcherTests); the last tests drive the loader.</summary>
public class ChargeLitigationLinkerTests : IAsyncLifetime
{
    private const string Mortgage = "Equitable mortgage on the land and building at Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, 751012";

    private const string OrderNamingProperty = """
        --- Page 1 (native) ---
        IN THE HIGH COURT OF ORISSA AT CUTTACK
        W.P.(C) No. 11227 of 2019

        --- Page 2 (native) ---
        ORDER
        The property situated at Plot No. A-36, Nayapalli, Bhubaneswar is under attachment.
        """;

    private const string VehicleCharge = "Hypothecation of the truck Tata Prima bearing registration No. OD02AB1234";

    /// <summary>The loader tests' link: a vehicle under the charge named in the order (the signal that still reads order text).</summary>
    private const string OrderNamingVehicle = """
        --- Page 1 (native) ---
        IN THE HIGH COURT OF ORISSA AT CUTTACK
        --- Page 2 (native) ---
        ORDER
        The truck bearing registration OD 02 AB 1234 shall not be transferred.
        """;

    private const string OrderNotNamingProperty = """
        --- Page 1 (native) ---
        ORDER
        Heard learned counsel. The matter is adjourned to the next date.
        """;

    private static RocCharge Charge(long id, string number, string holder, string? particulars = Mortgage, string propertyType = "Immovable property",
        string? securityTypesJson = null) => new()
    {
        ChargeId = id, RocChargeNumber = number, LatestChargeHolderRaw = holder, LatestChargeHolderNormalized = holder.ToUpperInvariant(),
        ChargeStatus = "Open", SatisfactionDate = null, LatestSecurityTypesJson = securityTypesJson,
        Events = [new RocChargeEvent { ChargeEventId = id, PropertyType = propertyType, PropertyParticulars = particulars }]
    };

    private static LitigationCase Case(long id, string number, string? court = "High Court of Orissa", string? act = null,
        string? petitioners = null, string? respondents = "[\"Coastal Projects Limited\"]", long orderId = 0, string? orderType = "Order") => new()
    {
        LitigationCaseId = id, CaseNumber = number, Court = court, Act = act, CaseStatus = "Pending",
        PetitionersJson = petitioners, RespondentsJson = respondents,
        Orders = orderId == 0 ? [] : [new LitigationCaseOrder { LitigationCaseOrderId = orderId, LitigationCaseId = id, OrderDate = "15-10-2019", OrderType = orderType }]
    };

    private static Dictionary<long, LitigationOrderDocument> Docs(long orderId, string? text, FilingDocumentProcessingStatus status = FilingDocumentProcessingStatus.TextExtracted) =>
        new() { [orderId] = new LitigationOrderDocument { LitigationCaseOrderId = orderId, ExtractedText = text, TextExtractionStatus = status } };

    private static readonly string[] CompanyNames = ["Coastal Projects Limited"];

    private static ChargeLitigationSummary Build(IReadOnlyList<RocCharge> charges, IReadOnlyList<LitigationCase> cases,
        IReadOnlyDictionary<long, LitigationOrderDocument>? docs = null, IReadOnlyCollection<string>? companyNames = null) =>
        ChargeLitigationLinker.Build(charges, cases, docs ?? new Dictionary<long, LitigationOrderDocument>(), companyNames ?? CompanyNames);

    // Ã¢â€â‚¬Ã¢â€â‚¬ signal: charged immovable property named in an order Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
    /// <summary>#554: the order's words are no longer matched to the charged property — the properties an order names are read and compared
    /// field by field instead. Even an order that does name the charged plot links nothing here.</summary>
    [Fact]
    public void Order_text_is_not_word_matched_to_the_charged_property()
    {
        var summary = Build([Charge(1, "CHG-1", "State Bank of India")], [Case(10, "WP(C) 11227/2019", orderId: 500)], Docs(500, OrderNamingProperty));

        Assert.False(summary.HasAny);
        Assert.Equal(1, summary.OrdersScanned);
    }

    /// <summary>The Godrej false positives (req 25, 2026-10-26): 426 "order names the charged property" links on 284 cases, from day numbers of
    /// dates ("10", "20") read as plot numbers and words such as DATED, PRESENT and the company's own name read as localities.</summary>
    [Fact]
    public void Dates_and_common_words_never_link_an_order_to_a_charged_property()
    {
        const string charge = "Exclusive charge through registered mortgage on land and building constructed/ to be constructed for projects - godrej two measuring "
            + "with all present and future FSI, and immovable property viz land and building at sub-divided plot no.5 of plot no.75a and bearing old survey no.75a, "
            + "dated 10 and 20 of the deed, Vikhroli, Mumbai";
        const string order = """
            --- Page 1 (native) ---
            DATED THIS THE 10TH DAY OF MARCH 2020. PRESENT: the Hon'ble Member. Godrej Projects Development Limited v. Vaibhav Saha.
            The matter is listed on 20.04.2020 and that the counter affidavit be filed.
            """;
        var summary = Build([Charge(1, "100496822", "HDFC Bank Limited", particulars: charge)], [Case(10, "CA 12195/2024", orderId: 500)], Docs(500, order),
            ["Godrej Properties Limited"]);

        Assert.False(summary.HasAny);
    }

    /// <summary>The Coastal false positives: the company's registered office ("Plot 304/O, Road No. 78, Jubilee Hills, Hyderabad") printed in a
    /// cause title is a party's address, not the charged property in dispute.</summary>
    [Fact]
    public void The_company_address_in_a_cause_title_is_not_the_charged_property()
    {
        const string charge = "Equitable mortgage of the office premises at Plot No. 304-O, Road No. 78, Film Nagar, Jubilee Hills, Hyderabad 500033";
        const string order = """
            --- Page 1 (native) ---
            K T Govinde Gowda v. M/s Coastal Projects Ltd, rep. by its Managing Director, also at: Plot No.304/O Road, No.78, Filmnagar Jubilee Hills,
            Hyderabad-500 033. The respondents are directed to file their reply.
            """;
        var summary = Build([Charge(1, "90080336", "State Bank of India", particulars: charge)], [Case(10, "WP 1085/2021", orderId: 500)], Docs(500, order));

        Assert.False(summary.HasAny);
    }

    [Fact]
    public void Movable_only_charge_has_no_address_to_match()
    {
        var charge = Charge(1, "CHG-1", "State Bank of India", particulars: "Hypothecation of stock and book debts", propertyType: "Movable property (not being pledge)");
        var summary = Build([charge], [Case(10, "WP 1/2019", orderId: 500)], Docs(500, OrderNamingProperty));

        Assert.Empty(summary.Links);
    }

    [Fact]
    public void Orders_without_extracted_text_are_counted_not_guessed_at()
    {
        var cases = new[] { Case(10, "WP 1/2019", orderId: 500), Case(11, "WP 2/2019", orderId: 501) };
        var docs = new Dictionary<long, LitigationOrderDocument>
        {
            [500] = new() { LitigationCaseOrderId = 500, ExtractedText = OrderNamingProperty, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted },
            [501] = new() { LitigationCaseOrderId = 501, ExtractedText = null, TextExtractionStatus = FilingDocumentProcessingStatus.Discovered }
        };

        var summary = Build([Charge(1, "CHG-1", "State Bank of India")], cases, docs);

        Assert.Equal(1, summary.OrdersScanned);
        Assert.Equal(1, summary.OrdersWithoutText);
        Assert.Empty(summary.Links);
    }

    [Fact]
    public void Nclt_cases_are_not_scanned_and_the_summary_says_so()
    {
        var nclt = Case(10, "CP(IB) 55/2019", court: "NCLT Mumbai Bench", orderId: 500);
        var summary = Build([Charge(1, "CHG-1", "State Bank of India")], [nclt], Docs(500, OrderNamingProperty));

        Assert.Empty(summary.Links);
        Assert.True(summary.NcltOrdersSkipped);
    }

    // Ã¢â€â‚¬Ã¢â€â‚¬ signal: the lender is litigating a recovery-type case Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
    [Fact]
    public void Lender_as_petitioner_in_a_sarfaesi_case_links_every_charge_it_holds()
    {
        var charges = new[] { Charge(1, "CHG-1", "State Bank of India", particulars: null, propertyType: "Book debts", securityTypesJson: "[\"BookDebts\"]"),
                              Charge(2, "CHG-2", "State Bank of India Ltd.", particulars: null), Charge(3, "CHG-3", "HDFC Bank Limited", particulars: null) };
        var sarfaesi = Case(10, "SA 99/2021", court: "Debts Recovery Tribunal", act: "SARFAESI Act 2002", petitioners: "[\"State Bank of India\"]");

        var summary = Build(charges, [sarfaesi]);

        Assert.Equal([1L, 2L], summary.Links.Select(l => l.ChargeId).OrderBy(x => x).ToArray());
        Assert.All(summary.Links, l => Assert.Equal(ChargeLitigationSignal.LenderRecoveryCase, l.Signal));
        Assert.All(summary.Links, l => Assert.Contains("Indirect", l.Explanation));
        Assert.Contains("book debts", summary.ForCharge(1).Single().Explanation);
        Assert.Empty(summary.ForCharge(3));
        Assert.Equal(1, summary.LinkedCaseCount);
    }

    [Fact]
    public void Lender_in_a_non_recovery_case_is_not_linked()
    {
        var civil = Case(10, "CS 5/2020", court: "City Civil Court", act: "Indian Contract Act", petitioners: "[\"State Bank of India\"]");
        Assert.Empty(Build([Charge(1, "CHG-1", "State Bank of India", particulars: null)], [civil]).Links);
    }

    [Fact]
    public void Recovery_case_between_other_parties_is_not_linked()
    {
        var other = Case(10, "SA 7/2021", court: "Debts Recovery Tribunal", act: "SARFAESI Act", petitioners: "[\"Axis Bank Limited\"]");
        Assert.Empty(Build([Charge(1, "CHG-1", "State Bank of India", particulars: null)], [other]).Links);
    }

    /// <summary>A borrower suing the bank, with the company a co-respondent (Coastal: "K T Govinde Gowda v. IDBI Bank, Coastal Projects"), is not
    /// the lender enforcing against the company.</summary>
    [Fact]
    public void Lender_on_the_same_side_as_the_company_is_not_linked()
    {
        var suit = Case(10, "SA 4/2021", court: "Debts Recovery Tribunal", act: "SARFAESI Act", petitioners: "[\"K T Govinde Gowda\"]",
            respondents: "[\"State Bank of India\", \"Coastal Projects Limited\"]");
        Assert.Empty(Build([Charge(1, "CHG-1", "State Bank of India", particulars: null)], [suit]).Links);
    }

    /// <summary>The lender's recovery case against another borrower ("SBI v. East Coast Carriers and others") is not a case against the company.</summary>
    [Fact]
    public void Lender_case_against_someone_else_is_not_linked()
    {
        var other = Case(10, "OA 12/2019", court: "Debts Recovery Tribunal", act: "RDB Act", petitioners: "[\"State Bank of India\"]",
            respondents: "[\"M/s. East Coast Carriers Pvt Ltd\"]");
        Assert.Empty(Build([Charge(1, "CHG-1", "State Bank of India", particulars: null)], [other]).Links);
    }

    [Fact]
    public void Without_the_company_names_no_lender_link_is_guessed()
    {
        var sarfaesi = Case(10, "SA 99/2021", court: "Debts Recovery Tribunal", act: "SARFAESI Act 2002", petitioners: "[\"State Bank of India\"]");
        Assert.Empty(Build([Charge(1, "CHG-1", "State Bank of India", particulars: null)], [sarfaesi], companyNames: []).Links);
    }

    [Fact]
    public void Holder_name_too_short_to_trust_is_not_matched()
    {
        var sarfaesi = Case(10, "SA 99/2021", court: "Debts Recovery Tribunal", act: "SARFAESI", petitioners: "[\"SBI Mumbai\"]");
        Assert.Empty(Build([Charge(1, "CHG-1", "SBI", particulars: null)], [sarfaesi]).Links); // "SBI" normalises to 3 characters
    }

    [Fact]
    public void Both_signals_on_one_charge_are_both_kept_and_counted_separately()
    {
        var sarfaesi = Case(11, "SA 99/2021", court: "Debts Recovery Tribunal", act: "SARFAESI", petitioners: "[\"State Bank of India\"]");
        var withOrder = Case(10, "WP 1/2019", orderId: 500);
        const string vehicleOrder = "--- Page 1 (native) ---\nThe Bank seized the truck bearing registration OD 02 AB 1234.";
        var summary = Build([Charge(1, "CHG-1", "State Bank of India", particulars: VehicleCharge, propertyType: "Movable property (not being pledge)")],
            [withOrder, sarfaesi], Docs(500, vehicleOrder));

        Assert.Equal(2, summary.ForCharge(1).Count);
        Assert.Equal(1, summary.CasesFor(ChargeLitigationSignal.MovableIdentifier));
        Assert.Equal(1, summary.CasesFor(ChargeLitigationSignal.LenderRecoveryCase));
        Assert.Equal(2, summary.LinkedCaseCount);
    }

    [Fact]
    public void No_open_charges_means_nothing_to_compare()
    {
        var summary = Build([], [Case(10, "WP 1/2019", orderId: 500)], Docs(500, OrderNamingProperty));
        Assert.False(summary.HasAny);
    }

    // Ã¢â€â‚¬Ã¢â€â‚¬ fast path Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
    private const string MortgageA36 = "Equitable mortgage on the land and building at Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, 751012";

    [Fact]
    public void Strong_only_fast_path_finds_exactly_the_strong_results_of_the_full_matcher()
    {
        RocCharge Mortgage(long id, string address) => Charge(id, $"CHG-{id}", "State Bank of India", particulars: address);
        var charges = new[]
        {
            Mortgage(1, MortgageA36),
            Mortgage(2, "Equitable mortgage of land at Arazi No. 428 & 429, Village Bhauti, Kanpur, Uttar Pradesh, 209305"),
            Mortgage(3, "Equitable mortgage on the land and building at 8-2-293/82/F-B-1/F, filmnagar, Hyderabad"),
            Mortgage(4, "Mortgage of Plot No. 55, Sector 9, Rohini, Delhi, 110085"),
            Mortgage(5, "Mortgage over the factory premises (no plot number) in Pune")
        };
        var pool = LitigationOrderAddressMatcher.BuildAddressPool(null, null, charges);

        string[] orderTexts =
        [
            OrderNamingProperty,                                                                               // strong on charge 1
            "--- Page 1 (native) ---\nLand at Arazi No. 428, Village Bhauti, Kanpur 209305 was sold.",          // strong on charge 2
            "--- Page 1 (native) ---\nLand at Arazi No. 428, Village Bhauti, Kanpur 208001 was sold.",          // PIN conflict: none
            "--- Page 1 (native) ---\nThe flat at 8-2-293/82/F-B-1/F, Filmnagar, Hyderabad is attached.",        // strong on charge 3
            "--- Page 1 (native) ---\nPlot No. 55 was mentioned with nothing else about where.",                 // plot only: not Strong
            OrderNotNamingProperty,
            "--- Page 1 (native) ---\nPlot No. A-36\nNayapalli, Bhubaneswar is the property.\n\n--- Page 2 (native) ---\nSee Plot 55, Sector 9, Rohini, Delhi 110085."
        ];
        var cases = orderTexts.Select((t, i) => Case(100 + i, $"WP {i}/2020", orderId: 900 + i)).ToList();
        var docs = orderTexts.Select((t, i) => (Id: 900L + i, Text: t)).ToDictionary(x => x.Id,
            x => new LitigationOrderDocument { LitigationCaseOrderId = x.Id, ExtractedText = x.Text, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted });

        static string Key(LitigationPropertyMatchResult m) => $"{m.LitigationCaseOrderId}|{m.PageNumber}|{m.RocChargeId}|{m.Strength}|{m.Excerpt}";
        var full = LitigationOrderAddressMatcher.MatchCases(pool, cases, docs).Where(m => m.Strength == AddressMatchStrength.Strong).Select(Key).OrderBy(x => x).ToList();
        var fast = LitigationOrderAddressMatcher.MatchCasesStrongOnly(pool, cases, docs).Select(Key).OrderBy(x => x).ToList();

        Assert.NotEmpty(full);          // the corpus really does contain strong matches
        Assert.Equal(full, fast);
    }

    [Fact]
    public void A_large_corpus_is_scanned_in_seconds_not_minutes()
    {
        // Regression guard: matching every paragraph against every charge with the full comparison took ~40 s for
        // this size (minutes for a real request). The plot-number pre-filter keeps it to about a second.
        var rng = new Random(7);
        string[] words = ["the", "petitioner", "respondent", "bank", "loan", "account", "court", "order", "hearing", "counsel", "submits", "property", "security", "notice"];
        string Page(int n) => $"--- Page {n} (native) ---\n" + string.Concat(Enumerable.Range(0, 12).Select(_ => string.Join(' ', Enumerable.Range(0, 70).Select(_ => words[rng.Next(words.Length)])) + "\n\n"));

        var charges = Enumerable.Range(1, 40).Select(i => Charge(i, $"CHG-{i}", "HDFC Bank Limited", particulars: $"Mortgage of land at Plot No. {100 + i}, Sector {i}, Rohini, Delhi, 1100{10 + i}")).ToList();
        var cases = new List<LitigationCase>();
        var docs = new Dictionary<long, LitigationOrderDocument>();
        for (var i = 1; i <= 60; i++)
        {
            cases.Add(Case(i, $"WP {i}/2020", orderId: i));
            docs[i] = new LitigationOrderDocument { LitigationCaseOrderId = i, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
                ExtractedText = string.Concat(Enumerable.Range(1, 12).Select(Page)) };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summary = ChargeLitigationLinker.Build(charges, cases, docs, CompanyNames);
        sw.Stop();

        Assert.False(summary.HasAny);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    // Ã¢â€â‚¬Ã¢â€â‚¬ loader (DB) Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<long> SeedRequestWithLitigationAsync(AppDbContext db, bool satisfied = false)
    {
        var client = new Client { ClientCode = "CLL" + Guid.NewGuid().ToString("N")[..7], ClientName = "Charge Litigation Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "Coastal Projects Limited", RequestNumber = $"CLL-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;

        db.RocCharges.Add(new RocCharge
        {
            RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, RocChargeNumber = "CHG-900",
            LatestChargeHolderRaw = "State Bank of India", LatestChargeHolderNormalized = "STATE BANK OF INDIA", ChargeStatus = satisfied ? "Satisfied" : "Open",
            SatisfactionDate = satisfied ? new DateOnly(2020, 1, 1) : null,
            Events = [new RocChargeEvent { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, SerialNumber = "1", EventType = ChargeEventType.Creation, PropertyType = "Movable property (not being pledge)", PropertyParticulars = VehicleCharge }]
        });

        var job = new LitigationSearchJob { RequestId = request.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow, RawResponseHash = "h" + request.RequestId };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = job.RawResponseHash!, Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow };
        db.LitigationReportSnapshots.Add(snapshot);
        var litCase = new LitigationCase { RequestId = request.RequestId, Court = "High Court of Orissa", CaseNumber = "WP 1/2019", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
        db.LitigationCases.Add(litCase);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = litCase.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        var order = new LitigationCaseOrder { LitigationCaseId = litCase.LitigationCaseId, OrderDate = "15-10-2019", OrderType = "Order", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument
        {
            LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded, RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            ExtractedText = OrderNamingVehicle, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted
        });
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    [Fact]
    public async Task Loader_finds_the_link_and_caches_it_in_the_size_limited_app_cache()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestWithLitigationAsync(db);
        // Program.cs registers AddMemoryCache(o => o.SizeLimit = 256): an entry without a Size throws (#331).
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var service = new ChargeLitigationService(db, cache);

        var first = await service.GetAsync(requestId);
        var second = await service.GetAsync(requestId);

        var link = Assert.Single(first.Links);
        Assert.Equal("CHG-900", link.ChargeNumber);
        Assert.Equal(ChargeLitigationSignal.MovableIdentifier, link.Signal);
        Assert.Same(first, second);
    }

    /// <summary>Order text is extracted asynchronously AFTER a snapshot completes. A comparison made while extraction
    /// is still running finds nothing; it must not be served from cache once the matching text has been published
    /// under the same snapshot (review of #334).</summary>
    [Fact]
    public async Task Links_appear_immediately_when_order_text_is_extracted_after_an_earlier_read_under_the_same_snapshot()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestWithLitigationAsync(db);

        // Put the order back to "text not extracted yet", as it is right after the snapshot completes.
        await db.LitigationOrderDocuments.Where(d => d.Order!.Case!.RequestId == requestId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.ExtractedText, (string?)null)
                .SetProperty(d => d.TextExtractionStatus, (FilingDocumentProcessingStatus?)null)
                .SetProperty(d => d.ExtractedUtc, (DateTime?)null));

        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var before = await new ChargeLitigationService(db, cache).GetAsync(requestId);
        Assert.False(before.HasAny);                                  // nothing to compare yet, and that is now cached
        var versionBefore = await ChargeLitigationService.VersionAsync(db, requestId);

        // Extraction finishes: the text is published under the SAME snapshot.
        await db.LitigationOrderDocuments.Where(d => d.Order!.Case!.RequestId == requestId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.ExtractedText, OrderNamingVehicle)
                .SetProperty(d => d.TextExtractionStatus, (FilingDocumentProcessingStatus?)FilingDocumentProcessingStatus.TextExtracted)
                .SetProperty(d => d.ExtractedUtc, (DateTime?)DateTime.UtcNow));

        var after = await new ChargeLitigationService(db, cache).GetAsync(requestId);   // same cache instance

        Assert.NotEqual(versionBefore, await ChargeLitigationService.VersionAsync(db, requestId));
        var link = Assert.Single(after.Links);                         // visible at once, not after the cache expires
        Assert.Equal("CHG-900", link.ChargeNumber);
        Assert.Equal(after.Version, await ChargeLitigationService.VersionAsync(db, requestId));
    }

    private static ChargeLitigationService ServiceFor(AppDbContext db, IMemoryCache cache, MCAROC_Analysis.Services.LitigationModule.Reading.LitigationReaderMode mode) =>
        new(db, cache, new MCAROC_Analysis.Services.LitigationModule.Reading.LitigationReaderGate(db,
            Microsoft.Extensions.Options.Options.Create(new MCAROC_Analysis.Services.LitigationModule.Reading.LitigationReaderOptions { ChargeLinks = mode }),
            Microsoft.Extensions.Options.Options.Create(new MCAROC_Analysis.Services.LitigationModule.Bridge.LitigationBridgeOptions { Enabled = true })));

    [Fact]
    public async Task Charge_links_follow_the_reader_mode_and_a_hidden_case_is_not_compared_in_module_mode()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestWithLitigationAsync(db);
        var caseId = await db.LitigationCases.Where(c => c.RequestId == requestId).Select(c => c.LitigationCaseId).SingleAsync();
        await new MCAROC_Analysis.Services.LitigationModule.Bridge.LitigationBridgeProcessor(db, new MCAROC_Analysis.Services.LitigationModule.Bridge.LitigationBridgeOptions { Enabled = true })
            .ApplyAsync(caseId, DateTime.MinValue, default);
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var legacy = MCAROC_Analysis.Services.LitigationModule.Reading.LitigationReaderMode.Legacy;
        var module = MCAROC_Analysis.Services.LitigationModule.Reading.LitigationReaderMode.Module;

        // Not hidden: both sources compare the same case and find the same link.
        Assert.Single((await ServiceFor(db, cache, legacy).GetAsync(requestId)).Links);
        Assert.Single((await ServiceFor(db, cache, module).GetAsync(requestId)).Links);

        // The scope hides the case, and (as the hide action does) the request's live-data version moves, so each source recomputes. Legacy still has the link, module has none.
        await db.Set<MCAROC_Analysis.Services.LitigationModule.Store.ModuleScopeCase>().Where(sc => sc.Case!.LegacyLitigationCaseId == caseId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, true));
        await MCAROC_Analysis.Services.LitigationModule.Refresh.RequestLiveData.BumpAsync(db, requestId, default);
        var legacyAfter = await ServiceFor(db, cache, legacy).GetAsync(requestId);
        var moduleAfter = await ServiceFor(db, cache, module).GetAsync(requestId);
        Assert.Single(legacyAfter.Links);
        Assert.Empty(moduleAfter.Links);
        Assert.Equal(legacyAfter.Version, moduleAfter.Version);
        Assert.Empty((await ServiceFor(db, cache, module).GetCachedAsync(requestId))!.Links);
        Assert.Single((await ServiceFor(db, cache, legacy).GetCachedAsync(requestId))!.Links);
    }

    /// <summary>Review of #483: in module mode the comparison also reads cases only an older completed snapshot lists, so extracting their order text must move the
    /// evidence version, or a cached comparison would keep omitting the link.</summary>
    [Fact]
    public async Task Text_extracted_for_a_case_only_an_older_snapshot_lists_moves_the_evidence_version()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestWithLitigationAsync(db);
        var jobId = await db.LitigationSearchJobs.Where(j => j.RequestId == requestId).Select(j => j.LitigationSearchJobId).SingleAsync();
        var older = new LitigationReportSnapshot { LitigationSearchJobId = jobId, RequestId = requestId, ReportHash = "old" + requestId, Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow.AddDays(-30), CreatedUtc = DateTime.UtcNow.AddDays(-30) };
        db.LitigationReportSnapshots.Add(older);
        var dropped = new LitigationCase { RequestId = requestId, Court = "DRT Cuttack", CaseNumber = "OA 7/2018", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
        db.LitigationCases.Add(dropped);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = dropped.LitigationCaseId, LitigationReportSnapshotId = older.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        var order = new LitigationCaseOrder { LitigationCaseId = dropped.LitigationCaseId, OrderDate = "01-02-2018", OrderType = "Order", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();
        var document = new LitigationOrderDocument { LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded, RetainedUntilUtc = DateTime.UtcNow.AddDays(7) };
        db.LitigationOrderDocuments.Add(document);
        await db.SaveChangesAsync();
        var before = await ChargeLitigationService.VersionAsync(db, requestId);

        await db.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.ExtractedText, OrderNamingVehicle)
                .SetProperty(d => d.TextExtractionStatus, (FilingDocumentProcessingStatus?)FilingDocumentProcessingStatus.TextExtracted)
                .SetProperty(d => d.ExtractedUtc, (DateTime?)DateTime.UtcNow));

        Assert.NotEqual(before, await ChargeLitigationService.VersionAsync(db, requestId));
    }

    [Fact]
    public async Task Re_extraction_of_an_order_with_different_text_changes_the_evidence_version()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestWithLitigationAsync(db);
        var first = await ChargeLitigationService.VersionAsync(db, requestId);

        // The same number of extracted documents, but its text was republished later.
        await db.LitigationOrderDocuments.Where(d => d.Order!.Case!.RequestId == requestId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.ExtractedUtc, (DateTime?)DateTime.UtcNow.AddMinutes(5)));

        Assert.NotEqual(first, await ChargeLitigationService.VersionAsync(db, requestId));
    }

    [Fact]
    public async Task Unchanged_evidence_keeps_the_same_version_so_the_cache_still_works()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestWithLitigationAsync(db);

        Assert.Equal(await ChargeLitigationService.VersionAsync(db, requestId), await ChargeLitigationService.VersionAsync(db, requestId));
        Assert.Equal("none", await ChargeLitigationService.VersionAsync(db, long.MaxValue));   // no snapshot at all
    }

    [Fact]
    public async Task The_report_assembler_carries_the_links_into_the_litigation_report()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestWithLitigationAsync(db);

        var report = await new LitigationReportAssembler(db).AssembleAsync(requestId);

        Assert.NotNull(report);
        var link = Assert.Single(report!.ChargeLinks!.Links);
        Assert.Equal("CHG-900", link.ChargeNumber);
        Assert.Equal(ChargeLitigationSignal.MovableIdentifier, link.Signal);

        // and it reaches both deliverables
        var csv = System.Text.Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(report));
        Assert.Contains("CHG-900", csv);
        Assert.True(LitigationReportArtifacts.RenderPdf(report).Length > 1000);
    }

    [Fact]
    public async Task Loader_ignores_satisfied_charges()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestWithLitigationAsync(db, satisfied: true);
        var service = new ChargeLitigationService(db, new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 }));

        Assert.False((await service.GetAsync(requestId)).HasAny);
    }

    [Fact]
    public async Task Loader_returns_an_empty_summary_for_a_request_with_no_litigation_data()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "CLE" + Guid.NewGuid().ToString("N")[..7], ClientName = "No Lit Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "No Lit Co", RequestNumber = $"CLE-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var summary = await new ChargeLitigationService(db, new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 })).GetAsync(request.RequestId);

        Assert.False(summary.HasAny);
    }

    /// <summary>The provider sends each party as a record; the linker used to read only plain strings, so on a real report a lender litigating was
    /// never recognised from the parties.</summary>
    [Fact]
    public void Lender_named_in_a_party_record_links_the_case_just_as_a_plain_name_does()
    {
        var charges = new[] { Charge(1, "CHG-1", "State Bank of India", particulars: null), Charge(3, "CHG-3", "HDFC Bank Limited", particulars: null) };
        var asRecords = Case(10, "SA 99/2021", court: "Debts Recovery Tribunal", act: "SARFAESI Act 2002",
            petitioners: "[{\"name\":\"State Bank of India\",\"address\":\"\",\"advocate\":\"\"}]", respondents: "[{\"name\":\"Coastal Projects Limited\"}]");

        var summary = Build(charges, [asRecords]);

        var link = Assert.Single(summary.Links);
        Assert.Equal((1L, ChargeLitigationSignal.LenderRecoveryCase), (link.ChargeId, link.Signal));
        Assert.Equal("State Bank of India v. Coastal Projects Limited", link.Case.Parties);
        Assert.Empty(summary.ForCharge(3));
    }
}
