using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationOrderAddressMatcherTests
{
    private const string CoastalRegistered = "Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, Orissa, 751012";
    private const string KanpurRegistered = "Arazi Number 428,429  Bhaunti, Pratappur, Kalyanpur, Kanpur, Uttar Pradesh, 209305";
    private const string CoastalHyderabadMortgage =
        "Equitable mortgage on the land and building at 8-2-293/82/F-B-1/F, filmnagar, Hyderabad";

    [Fact]
    public void BuildAddressPool_ExtractsCompanyPremisesAndImmovableCharges()
    {
        var profile = new CompanyProfile
        {
            RegisteredAddress = CoastalRegistered,
            RegisteredAddressCity = "Bhubaneswar",
            RegisteredAddressState = "Orissa",
            BusinessAddress = "Different Office, Unit 4, Bhubaneswar"
        };

        var epfo = new List<EpfoEstablishment>
        {
            new() { EstablishmentId = "ORBBU00123", Address = "Plot 10, Chandaka Industrial Estate, Bhubaneswar, 751024", City = "Bhubaneswar" }
        };

        var charges = new List<RocCharge>
        {
            // Open immovable charge: should be included
            new()
            {
                ChargeId = 101,
                RocChargeNumber = "CHG-101",
                LatestChargeHolderRaw = "State Bank of India",
                SatisfactionDate = null,
                Events =
                [
                    new()
                    {
                        ChargeEventId = 1,
                        PropertyType = "Immovable property",
                        PropertyParticulars = CoastalHyderabadMortgage
                    }
                ]
            },
            // Satisfied charge: should be skipped
            new()
            {
                ChargeId = 102,
                RocChargeNumber = "CHG-102",
                LatestChargeHolderRaw = "Bank of Baroda",
                SatisfactionDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)),
                Events =
                [
                    new()
                    {
                        ChargeEventId = 2,
                        PropertyType = "Immovable property",
                        PropertyParticulars = "Plot 99, Sahid Nagar, Bhubaneswar"
                    }
                ]
            },
            // Movable-only charge: should be skipped
            new()
            {
                ChargeId = 103,
                RocChargeNumber = "CHG-103",
                LatestChargeHolderRaw = "Canara Bank",
                SatisfactionDate = null,
                Events =
                [
                    new()
                    {
                        ChargeEventId = 3,
                        PropertyType = "Movable property (hypothecation of book debts and stock)",
                        PropertyParticulars = "Hypothecation of book debts"
                    }
                ]
            }
        };

        var pool = LitigationOrderAddressMatcher.BuildAddressPool(profile, epfo, charges);

        Assert.Equal(4, pool.Count);
        Assert.Contains(pool, p => p.SourceLabel == "Registered office" && p.IsCompanyPremises);
        Assert.Contains(pool, p => p.SourceLabel == "Business address" && p.IsCompanyPremises);
        Assert.Contains(pool, p => p.SourceLabel.StartsWith("EPFO establishment") && p.IsCompanyPremises);
        Assert.Contains(pool, p => p.RocChargeId == 101 && !p.IsCompanyPremises && p.RocChargeNumber == "CHG-101");
        Assert.DoesNotContain(pool, p => p.RocChargeId == 102);
        Assert.DoesNotContain(pool, p => p.RocChargeId == 103);
    }

    [Fact]
    public void BuildAddressPool_DeduplicatesIdenticalBusinessAddress()
    {
        var profile = new CompanyProfile
        {
            RegisteredAddress = CoastalRegistered,
            BusinessAddress = CoastalRegistered // identical to registered
        };

        var pool = LitigationOrderAddressMatcher.BuildAddressPool(profile, [], []);

        Assert.Single(pool);
        Assert.Equal("Registered office", pool[0].SourceLabel);
    }

    [Fact]
    public void MatchText_MultiPageOrder_ReportsExactPageAndMatchesPremises()
    {
        var pool = new List<LitigationAddressTarget>
        {
            new("Registered office", CoastalRegistered, ["Bhubaneswar", "Orissa"], IsCompanyPremises: true)
        };

        var orderText = """
            --- Page 1 (native) ---
            IN THE HIGH COURT OF ORISSA AT CUTTACK
            W.P.(C) No. 11227 of 2019
            Between:
            Coastal Projects Limited ... Petitioner
            And
            State Bank of India ... Respondent

            --- Page 2 (native) ---
            ORDER
            Heard learned counsel for the parties.
            The petitioner submits that the corporate headquarters and office situated at
            Plot No. A-36, Nayapalli, Bhubaneswar is under interim protection.

            --- Page 3 (native) ---
            Let the matter be listed after four weeks.
            """;

        var matches = LitigationOrderAddressMatcher.MatchText(pool, 501, "15-10-2019", "Interim Order", orderText);

        Assert.Single(matches);
        var match = matches[0];
        Assert.Equal(501, match.LitigationCaseOrderId);
        Assert.Equal(2, match.PageNumber);
        Assert.Equal("Registered office", match.SourceLabel);
        Assert.Equal(AddressMatchStrength.Strong, match.Strength);
        Assert.Contains("A-36", match.MatchedPlotNumbers);
        Assert.Contains("NAYAPALLI", match.MatchedLocalities);
        Assert.True(match.IsCompanyPremises);
        Assert.Contains("Plot No. A-36, Nayapalli", match.Excerpt);
    }

    [Fact]
    public void MatchText_IsolatesParagraphs_PreventsFalsePinConflictFromCourtHeader()
    {
        // Issue #191 key edge case: The court header on the same page states Cuttack PIN 753002,
        // but the company's premises is Bhubaneswar 751012. Testing by paragraph isolates the
        // property description so the unrelated court PIN does not trigger a false conflict!
        var pool = new List<LitigationAddressTarget>
        {
            new("Registered office", CoastalRegistered, ["Bhubaneswar", "Orissa"], IsCompanyPremises: true)
        };

        var orderText = """
            --- Page 1 (native) ---
            HIGH COURT OF JUDICATURE AT CUTTACK - 753002

            In the matter of property situated at Plot No A-36, Nayapalli, Bhubaneswar.
            Notice is hereby issued to all concerned.
            """;

        var matches = LitigationOrderAddressMatcher.MatchText(pool, 502, "20-01-2020", "Order", orderText);

        Assert.Single(matches);
        Assert.Equal(1, matches[0].PageNumber);
        Assert.Equal(AddressMatchStrength.Strong, matches[0].Strength);
        Assert.Contains("A-36", matches[0].MatchedPlotNumbers);
        Assert.Contains("NAYAPALLI", matches[0].MatchedLocalities);
    }

    [Fact]
    public void MatchText_ConflictingPinInSameParagraph_RejectsMatch()
    {
        // Same plot and locality, but stating a conflicting PIN within the same property description
        var pool = new List<LitigationAddressTarget>
        {
            new("Registered office", CoastalRegistered, ["Bhubaneswar", "Orissa"], IsCompanyPremises: true)
        };

        var orderText = """
            --- Page 1 (native) ---
            The respondent claims rights over premises situated at Plot No A-36, Nayapalli, Raipur - 492001.
            """;

        var matches = LitigationOrderAddressMatcher.MatchText(pool, 503, "10-02-2020", "Order", orderText);

        Assert.Empty(matches);
    }

    [Fact]
    public void MatchText_OpenChargeMortgagedProperty_MatchesCorrectly()
    {
        var pool = new List<LitigationAddressTarget>
        {
            new(
                SourceLabel: "Charge CHG-999 (SBI)",
                AddressText: CoastalHyderabadMortgage,
                ExcludedPlaceNames: ["Hyderabad"],
                IsCompanyPremises: false,
                RocChargeId: 999,
                RocChargeNumber: "CHG-999",
                ChargeHolder: "State Bank of India")
        };

        var orderText = """
            --- Page 1 (native) ---
            Interim injunction restraining alienation of immovable property bearing municipal no.
            8-2-293/82/F-B-1/F, Film Nagar, Hyderabad until further orders.
            """;

        var matches = LitigationOrderAddressMatcher.MatchText(pool, 504, "05-03-2020", "Order", orderText);

        Assert.Single(matches);
        var match = matches[0];
        Assert.Equal("Charge CHG-999 (SBI)", match.SourceLabel);
        Assert.Equal(999, match.RocChargeId);
        Assert.Equal("CHG-999", match.RocChargeNumber);
        Assert.False(match.IsCompanyPremises);
        Assert.Equal(AddressMatchStrength.Strong, match.Strength);
        Assert.Contains("8-2-293/82/F-B-1/F", match.MatchedPlotNumbers);
        Assert.Contains("FILMNAGAR", match.MatchedLocalities);
    }

    [Fact]
    public void MatchOrders_OnlyProcessesDownloadedAndExtractedDocuments()
    {
        var pool = new List<LitigationAddressTarget>
        {
            new("Registered office", KanpurRegistered, ["Kanpur", "Uttar Pradesh"], IsCompanyPremises: true)
        };

        var orders = new List<LitigationCaseOrder>
        {
            new() { LitigationCaseOrderId = 1, OrderDate = "01-01-2021", OrderType = "Order" },
            new() { LitigationCaseOrderId = 2, OrderDate = "02-02-2021", OrderType = "Order" },
            new() { LitigationCaseOrderId = 3, OrderDate = "03-03-2021", OrderType = "Order" }
        };

        var docs = new Dictionary<long, LitigationOrderDocument>
        {
            // Order 1: Extracted text matches Kanpur registered office
            [1] = new()
            {
                LitigationCaseOrderId = 1,
                Status = LitigationOrderDocumentStatus.Downloaded,
                TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
                ExtractedText = "--- Page 1 (native) ---\nMortgage on Arazi No. 428 and 429, Village Bhauti, Kanpur, PIN 209305."
            },
            // Order 2: Pending download, no text
            [2] = new()
            {
                LitigationCaseOrderId = 2,
                Status = LitigationOrderDocumentStatus.Pending,
                TextExtractionStatus = null,
                ExtractedText = null
            },
            // Order 3: Downloaded but extraction failed
            [3] = new()
            {
                LitigationCaseOrderId = 3,
                Status = LitigationOrderDocumentStatus.Downloaded,
                TextExtractionStatus = FilingDocumentProcessingStatus.CorruptPdf,
                ExtractedText = null
            }
        };

        var matches = LitigationOrderAddressMatcher.MatchOrders(pool, orders, docs);

        Assert.Single(matches);
        Assert.Equal(1, matches[0].LitigationCaseOrderId);
        Assert.Equal("209305", matches[0].MatchedPinCode);
        Assert.Contains("428", matches[0].MatchedPlotNumbers);
        Assert.Contains("429", matches[0].MatchedPlotNumbers);
    }

    [Fact]
    public void MatchCases_excludes_nclt_and_nclat_orders_per_issue_191_hard_prerequisite()
    {
        // Issue #191 hard prerequisite: "The Coastal litigation-orders corpus is not usable as-is:
        // the dedupe scanner from #189 found NCLT 93% duplicate/mislabeled (205 of 221 files)...
        // Do not start property-extraction work against NCLT until that archive is re-sourced".
        var pool = LitigationOrderAddressMatcher.BuildAddressPool(
            CreateCompanyProfile(),
            CreateEpfoEstablishments(),
            CreateOpenCharges());

        var hcOrder = new LitigationCaseOrder { LitigationCaseOrderId = 10, OrderDate = "10-02-2024", OrderType = "Order" };
        var ncltCourtOrder = new LitigationCaseOrder { LitigationCaseOrderId = 20, OrderDate = "15-03-2024", OrderType = "Order" };
        var ncltBenchOrder = new LitigationCaseOrder { LitigationCaseOrderId = 30, OrderDate = "20-04-2024", OrderType = "Order" };
        var nclatOrder = new LitigationCaseOrder { LitigationCaseOrderId = 40, OrderDate = "25-05-2024", OrderType = "Order" };

        var hcCase = new LitigationCase
        {
            LitigationCaseId = 1,
            CourtCategory = "high_court",
            Court = "High Court of Orissa",
            Orders = [hcOrder]
        };

        var ncltCourtCase = new LitigationCase
        {
            LitigationCaseId = 2,
            CourtCategory = "tribunal_cases",
            Court = "National Company Law Tribunal, Cuttack Bench",
            Orders = [ncltCourtOrder]
        };

        var ncltBenchCase = new LitigationCase
        {
            LitigationCaseId = 3,
            CourtCategory = "nclt",
            Bench = "nclt,cuttack bench.",
            Orders = [ncltBenchOrder]
        };

        var nclatCase = new LitigationCase
        {
            LitigationCaseId = 4,
            CourtCategory = "nclat",
            Court = "National Company Law Appellate Tribunal",
            Orders = [nclatOrder]
        };

        const string matchingText = "--- Page 1 (native) ---\nThe registered premises at Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, 751012.";

        var docs = new Dictionary<long, LitigationOrderDocument>
        {
            [10] = new() { LitigationCaseOrderId = 10, Status = LitigationOrderDocumentStatus.Downloaded, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ExtractedText = matchingText },
            [20] = new() { LitigationCaseOrderId = 20, Status = LitigationOrderDocumentStatus.Downloaded, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ExtractedText = matchingText },
            [30] = new() { LitigationCaseOrderId = 30, Status = LitigationOrderDocumentStatus.Downloaded, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ExtractedText = matchingText },
            [40] = new() { LitigationCaseOrderId = 40, Status = LitigationOrderDocumentStatus.Downloaded, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ExtractedText = matchingText }
        };

        var matches = LitigationOrderAddressMatcher.MatchCases(pool, [hcCase, ncltCourtCase, ncltBenchCase, nclatCase], docs);

        // Only High Court order matches; all NCLT/NCLAT orders are excluded
        var matched = Assert.Single(matches);
        Assert.Equal(10, matched.LitigationCaseOrderId);
        Assert.Equal("Registered office", matched.SourceLabel);
    }

    [Fact]
    public void MatchOrders_excludes_orders_with_nclt_url_or_case()
    {
        var pool = LitigationOrderAddressMatcher.BuildAddressPool(
            CreateCompanyProfile(),
            CreateEpfoEstablishments(),
            CreateOpenCharges());

        var order1 = new LitigationCaseOrder
        {
            LitigationCaseOrderId = 50,
            OrderDate = "10-02-2024",
            PdfUrl = "https://storage.example/orders/nclt/cuttack/cp_12_2020.pdf",
            Case = new LitigationCase { LitigationCaseId = 5, CourtCategory = "district_court" } // even if mislabeled as district_court
        };

        var order2 = new LitigationCaseOrder
        {
            LitigationCaseOrderId = 51,
            OrderDate = "10-02-2024",
            PdfUrl = "https://storage.example/orders/high_court/delhi/wp_123.pdf",
            Case = new LitigationCase { LitigationCaseId = 6, CourtCategory = "high_court", Court = "High Court of Delhi" }
        };

        const string matchingText = "--- Page 1 (native) ---\nThe registered premises at Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, 751012.";

        var docs = new Dictionary<long, LitigationOrderDocument>
        {
            [50] = new() { LitigationCaseOrderId = 50, Status = LitigationOrderDocumentStatus.Downloaded, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ExtractedText = matchingText },
            [51] = new() { LitigationCaseOrderId = 51, Status = LitigationOrderDocumentStatus.Downloaded, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ExtractedText = matchingText }
        };

        var matches = LitigationOrderAddressMatcher.MatchOrders(pool, [order1, order2], docs);

        var matched = Assert.Single(matches);
        Assert.Equal(51, matched.LitigationCaseOrderId);
    }

    private static CompanyProfile CreateCompanyProfile() => new()
    {
        RegisteredAddress = CoastalRegistered,
        RegisteredAddressCity = "Bhubaneswar",
        RegisteredAddressState = "Orissa",
        BusinessAddress = "Different Office, Unit 4, Bhubaneswar"
    };

    private static List<EpfoEstablishment> CreateEpfoEstablishments() =>
    [
        new() { EstablishmentId = "ORBBU00123", Address = "Plot 10, Chandaka Industrial Estate, Bhubaneswar, 751024", City = "Bhubaneswar" }
    ];

    private static List<RocCharge> CreateOpenCharges() =>
    [
        new()
        {
            ChargeId = 101,
            RocChargeNumber = "CHG-101",
            LatestChargeHolderRaw = "State Bank of India",
            SatisfactionDate = null,
            Events =
            [
                new()
                {
                    ChargeEventId = 1,
                    PropertyType = "Immovable property",
                    PropertyParticulars = CoastalHyderabadMortgage
                }
            ]
        }
    ];
}
