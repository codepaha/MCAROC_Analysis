using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#554: the properties court orders name, against the properties the open charges secure. The charge texts are Godrej's (req 25).</summary>
public sealed class OrderPropertyChargeMatcherTests : IAsyncLifetime
{
    private static readonly string[] Godrej = ["Godrej Properties Limited"];

    private static AiProperty P(string? unit = null, string? building = null, string? project = null, string? survey = null, string? village = null,
        string? city = null, string? pin = null, PropertyKind kind = PropertyKind.Premises, params string[] localities) =>
        new(PropertyAssetClass.Immovable, kind, null, unit, null, building, project, [], null,
            survey is null ? [] : [new SurveyNumberGroup("CTS", null, [survey])], localities, village, null, null, city, null, pin);

    private static ChargedProperty Charge(long id, AiProperty p) => new(id, $"CHG-{id}", "HDFC Bank Limited", p);

    [Fact]
    public void The_same_unit_in_the_same_building_is_the_charged_property()
    {
        var charged = Charge(1, P(unit: "5C", building: "Godrej One", city: "Mumbai", localities: ["Pirojshanagar", "Vikhroli"]));
        var tie = Assert.Single(OrderPropertyChargeMatcher.Ties(P(unit: "5C", building: "Godrej One", city: "Mumbai"), [charged], Godrej));
        Assert.Equal(PropertyChargeTieKind.SameProperty, tie.Kind);
    }

    [Fact]
    public void A_survey_number_is_the_charged_land_only_in_the_same_village()
    {
        var charged = Charge(1, P(survey: "51/B", village: "Vikhroli", city: "Mumbai", kind: PropertyKind.Land));
        Assert.Equal(PropertyChargeTieKind.SameProperty,
            Assert.Single(OrderPropertyChargeMatcher.Ties(P(survey: "51/B", village: "Vikhroli", kind: PropertyKind.Land), [charged], Godrej)).Kind);
        Assert.Empty(OrderPropertyChargeMatcher.Ties(P(survey: "51/B", village: "Kirol", kind: PropertyKind.Land), [charged], Godrej));
        Assert.Empty(OrderPropertyChargeMatcher.Ties(P(survey: "5/1B", village: "Vikhroli", kind: PropertyKind.Land), [charged], Godrej));
    }

    /// <summary>A buyer's flat in Godrej Two, whose land the charge mortgages ("Commercial Project Godrej Two"): the project is in dispute, not the charged asset.</summary>
    [Fact]
    public void A_unit_in_a_charged_project_ties_to_the_project_not_the_asset()
    {
        var charged = Charge(2, P(project: "Commercial Project Godrej Two", city: "Mumbai", kind: PropertyKind.BuildingOrProject, localities: ["Vikhroli"]));
        var tie = Assert.Single(OrderPropertyChargeMatcher.Ties(P(unit: "1804", project: "Godrej Two", city: "Mumbai"), [charged], Godrej));
        Assert.Equal(PropertyChargeTieKind.ChargedProject, tie.Kind);
    }

    [Fact]
    public void The_company_name_alone_or_another_city_is_not_a_charged_project()
    {
        var charged = Charge(2, P(project: "Commercial Project Godrej Two", city: "Mumbai", kind: PropertyKind.BuildingOrProject));
        Assert.Empty(OrderPropertyChargeMatcher.Ties(P(unit: "12", project: "Godrej", city: "Mumbai"), [charged], Godrej));              // one word, the company's
        Assert.Empty(OrderPropertyChargeMatcher.Ties(P(unit: "12", project: "Godrej Properties", city: "Mumbai"), [Charge(3,
            P(project: "Godrej Properties Project Alpha", city: "Mumbai", kind: PropertyKind.BuildingOrProject))], Godrej));                  // only the company's words
        Assert.Empty(OrderPropertyChargeMatcher.Ties(P(unit: "1804", project: "Godrej Two", city: "Pune"), [charged], Godrej));         // another city
        Assert.Empty(OrderPropertyChargeMatcher.Ties(P(unit: "1804", project: "Godrej Alpine", city: "Mangalore"), [charged], Godrej)); // another project
    }

    [Fact]
    public void A_charge_without_an_extraction_is_read_by_the_rules()
    {
        var charge = new RocCharge
        {
            ChargeId = 7, RocChargeNumber = "CHG-7", LatestChargeHolderRaw = "Axis Bank Limited", ChargeStatus = "Open",
            Events = [new RocChargeEvent { ChargeEventId = 7, PropertyType = "Immovable property",
                PropertyParticulars = "Equitable mortgage of Flat No. B-21 in B Wing of the building known as Godrej Serenity, Deonar Village Road, Mumbai 400088" }]
        };

        var charged = OrderPropertyChargeMatcher.ChargedProperties([charge], null);

        Assert.NotEmpty(charged);
        Assert.All(charged, c => Assert.Equal(7, c.ChargeId));
    }

    // ── into the charge links ───────────────────────────────────────────────────────────────────────────

    private static LitigationOrderPropertyReading Reading(long documentId, params OrderProperty[] properties) => new()
    {
        LitigationOrderDocumentId = documentId, Status = LitigationOrderPropertyReadingStatus.Completed, CompletedUtc = DateTime.UtcNow,
        ResultJson = OrderPropertiesAi.Serialize(new OrderPropertiesResult(properties))
    };

    private static (LitigationCase Case, Dictionary<long, LitigationOrderDocument> Docs) CaseWithOrder(long caseId, long orderId)
    {
        var order = new LitigationCaseOrder { LitigationCaseOrderId = orderId, LitigationCaseId = caseId, OrderDate = "12-04-2018", OrderType = "Order" };
        var c = new LitigationCase { LitigationCaseId = caseId, CaseNumber = $"CS {caseId}/2018", Court = "Bombay High Court", CaseStatus = "Pending",
            PetitionersJson = "[\"Nupur Bansal\"]", RespondentsJson = "[\"Godrej Properties Limited\"]", Orders = [order] };
        return (c, new() { [orderId] = new LitigationOrderDocument { LitigationOrderDocumentId = 100 + orderId, LitigationCaseOrderId = orderId,
            Status = LitigationOrderDocumentStatus.Downloaded, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ExtractedText = "--- Page 1 (native) ---\nx" } });
    }

    [Fact]
    public void The_links_carry_the_order_page_and_quote_and_a_party_address_links_nothing()
    {
        var charges = new List<RocCharge> { new() { ChargeId = 1, RocChargeNumber = "100603359", LatestChargeHolderRaw = "HDFC Bank Limited", ChargeStatus = "Open", Events = [] } };
        var charged = new[] { Charge(1, P(unit: "5C", building: "Godrej One", city: "Mumbai")),
                              Charge(2, P(project: "Commercial Project Godrej Two", city: "Mumbai", kind: PropertyKind.BuildingOrProject)) };
        var (disputed, docs1) = CaseWithOrder(10, 500);
        var (buyer, docs2) = CaseWithOrder(11, 501);
        var (titleOnly, docs3) = CaseWithOrder(12, 502);
        var docs = docs1.Concat(docs2).Concat(docs3).ToDictionary(kv => kv.Key, kv => kv.Value);
        var readings = new Dictionary<long, LitigationOrderPropertyReading>
        {
            [600] = Reading(600, new OrderProperty(2, OrderPropertyRole.Attached, OrderPropertyRelation.TargetOwned, null, [],
                P(unit: "5C", building: "Godrej One", city: "Mumbai") with { SourceText = "office premises being Unit 5C, Godrej One are attached" })),
            [601] = Reading(601, new OrderProperty(1, OrderPropertyRole.Disputed, OrderPropertyRelation.SoldByTarget, OrderPropertyIssue.DelayedPossession, [],
                P(unit: "1804", project: "Godrej Two", city: "Mumbai") with { SourceText = "flat 1804 in Godrej Two" })),
            // The cause title: the company's registered office, which is also the charged office. Never a link (the old false links).
            [602] = Reading(602, new OrderProperty(1, OrderPropertyRole.PartyAddress, OrderPropertyRelation.TargetOwned, null, [],
                P(unit: "5C", building: "Godrej One", city: "Mumbai") with { SourceText = "having its office at 5th Floor, Godrej One" })),
        };
        var cases = new[] { disputed, buyer, titleOnly };
        var props = cases.ToDictionary(c => c.LitigationCaseId, c => CaseOrderProperties.Build(c.Orders.Select(o => (o, docs[o.LitigationCaseOrderId])).ToList(), readings));

        var summary = ChargeLitigationLinker.Build(charges, cases, docs, Godrej, props, charged);

        var same = Assert.Single(summary.Links, l => l.Signal == ChargeLitigationSignal.ImmovableAddress);
        Assert.Equal(10, same.Case.LitigationCaseId);
        Assert.Equal(2, same.PageNumber);
        Assert.Contains("Unit 5C", same.Excerpt);
        Assert.Contains("attached", same.Explanation);
        var project = Assert.Single(summary.Links, l => l.Signal == ChargeLitigationSignal.ChargedProject);
        Assert.Equal(11, project.Case.LitigationCaseId);
        Assert.DoesNotContain(summary.Links, l => l.Case.LitigationCaseId == 12);
        Assert.True(ChargeLitigationLabels.NamesTheAsset(ChargeLitigationSignal.ImmovableAddress));
        Assert.False(ChargeLitigationLabels.NamesTheAsset(ChargeLitigationSignal.ChargedProject));
    }

    // ── the service: readings arriving, and charges of the latest ingestion only ─────────────────────────

    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_link_appears_when_the_order_is_read_and_only_the_latest_ingestion_is_compared()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "OPC" + Guid.NewGuid().ToString("N")[..7], ClientName = "Property Charge Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "Godrej Properties Limited", RequestNumber = $"OPC-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var oldRun = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 2, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.AddRange(oldRun, run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        // Unique per run: extractions are stored per distinct text, shared across tests.
        var office = $"Equitable mortgage of office premises Unit No. 5C, 5th floor, Godrej One, Pirojshanagar, Vikhroli, Mumbai (deed {Guid.NewGuid():N})";
        // The charge's validated extraction, as the real Godrej charge has: it names the building, which the rules reading does not.
        db.PropertyParticularsExtractions.Add(new PropertyParticularsExtraction
        {
            TextHash = PropertyParticularsAi.HashOf(office, "Immovable property"), PromptVersion = PropertyParticularsAi.PromptVersion, ModelId = PropertyParticularsAi.ModelId,
            SourceText = office, PropertyType = "Immovable property", Status = PropertyParticularsExtractionStatus.Completed, CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
            ExtractionJson = PropertyParticularsAi.Serialize(new PropertyParticularsAiResult([P(unit: "5C", building: "Godrej One", city: "Mumbai", localities: ["Pirojshanagar", "Vikhroli"])]))
        });
        foreach (var r in new[] { oldRun, run })
            db.RocCharges.Add(new RocCharge { RequestId = request.RequestId, IngestionRunId = r.IngestionRunId, RocChargeNumber = "100603359",
                LatestChargeHolderRaw = "HDFC Bank Limited", ChargeStatus = "Open",
                Events = [new RocChargeEvent { RequestId = request.RequestId, IngestionRunId = r.IngestionRunId, SerialNumber = "1", EventType = ChargeEventType.Creation,
                    PropertyType = "Immovable property", PropertyParticulars = office }] });
        var job = new LitigationSearchJob { RequestId = request.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow, RawResponseHash = "h" + request.RequestId };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = job.RawResponseHash!,
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow };
        db.LitigationReportSnapshots.Add(snapshot);
        var c = new LitigationCase { RequestId = request.RequestId, Court = "Bombay High Court", CaseNumber = "CS 1/2018", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow, PetitionersJson = "[\"HDFC Bank Limited\"]", RespondentsJson = "[\"Godrej Properties Limited\"]" };
        db.LitigationCases.Add(c);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        var order = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "12-04-2018", OrderType = "Order", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();
        var document = new LitigationOrderDocument { LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded, RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            ExtractedText = "--- Page 1 (native) ---\nThe office premises being Unit No. 5C, Godrej One shall stand attached.", TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            ExtractedUtc = DateTime.UtcNow };
        db.LitigationOrderDocuments.Add(document);
        await db.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var before = await new ChargeLitigationService(db, cache).GetAsync(request.RequestId);
        Assert.DoesNotContain(before.Links, l => l.Signal == ChargeLitigationSignal.ImmovableAddress);    // not read yet
        var versionBefore = before.Version;

        db.LitigationOrderPropertyReadings.Add(new LitigationOrderPropertyReading
        {
            LitigationOrderDocumentId = document.LitigationOrderDocumentId, RequestId = request.RequestId, InputHash = new string('a', 64), PromptVersion = OrderPropertiesAi.PromptVersion,
            ModelId = OrderPropertiesAi.ModelId, Status = LitigationOrderPropertyReadingStatus.Completed, CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
            ResultJson = OrderPropertiesAi.Serialize(new OrderPropertiesResult([new OrderProperty(1, OrderPropertyRole.Attached, OrderPropertyRelation.TargetOwned, null, [],
                P(unit: "5C", building: "Godrej One", city: "Mumbai") with { SourceText = "Unit No. 5C, Godrej One shall stand attached" })]))
        });
        await db.SaveChangesAsync();

        var after = await new ChargeLitigationService(db, cache).GetAsync(request.RequestId);   // same cache: the reading must move the version
        Assert.NotEqual(versionBefore, after.Version);
        var link = Assert.Single(after.Links, l => l.Signal == ChargeLitigationSignal.ImmovableAddress);   // one, not one per ingestion
        Assert.Equal("100603359", link.ChargeNumber);
    }
}
