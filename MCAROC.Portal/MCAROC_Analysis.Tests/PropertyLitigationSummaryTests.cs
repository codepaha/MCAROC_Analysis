using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#554: the request-level "Property in litigation" summary the Litigation tab and the litigation report show.</summary>
public sealed class PropertyLitigationSummaryTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly string[] Godrej = ["Godrej Properties Limited"];

    private static AiProperty P(string? unit = null, string? building = null, string? project = null, string? city = null, PropertyKind kind = PropertyKind.Premises) =>
        new(PropertyAssetClass.Immovable, kind, null, unit, null, building, project, [], null, [], [], null, null, null, city, null, null, $"{unit} {building} {project}".Trim());

    private static OrderProperty Op(OrderPropertyRole role, AiProperty p, OrderPropertyRelation relation = OrderPropertyRelation.Unknown,
        params OrderPropertyDirectionKind[] directions) =>
        new(1, role, relation, role == OrderPropertyRole.Disputed ? OrderPropertyIssue.Possession : null,
            directions.Select(d => new OrderPropertyDirection(d, "the order directs it")).ToList(), p);

    /// <summary>Case <paramref name="caseId"/> with one order whose reading names <paramref name="properties"/>.</summary>
    private static (LitigationCase Case, LitigationOrderDocument Doc, LitigationOrderPropertyReading Reading) Case(long caseId, string number,
        IReadOnlyList<OrderUnidentifiedProperty>? unidentified, params OrderProperty[] properties)
    {
        var order = new LitigationCaseOrder { LitigationCaseOrderId = caseId * 10, LitigationCaseId = caseId, OrderDate = "01-01-2020", OrderType = "Order" };
        var c = new LitigationCase { LitigationCaseId = caseId, CaseNumber = number, Court = "Bombay High Court", CaseStatus = "Pending",
            PetitionersJson = "[\"Someone\"]", RespondentsJson = "[\"Godrej Properties Limited\"]", Orders = [order] };
        var doc = new LitigationOrderDocument { LitigationOrderDocumentId = caseId * 100, LitigationCaseOrderId = order.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ExtractedText = "--- Page 1 (native) ---\nx" };
        var reading = new LitigationOrderPropertyReading { LitigationOrderDocumentId = doc.LitigationOrderDocumentId, Status = LitigationOrderPropertyReadingStatus.Completed,
            CompletedUtc = DateTime.UtcNow, ResultJson = OrderPropertiesAi.Serialize(new OrderPropertiesResult(properties, unidentified)) };
        return (c, doc, reading);
    }

    private static ChargeLitigationSummary Summarise(IReadOnlyList<RocCharge> charges, IReadOnlyList<ChargedProperty> charged,
        params (LitigationCase Case, LitigationOrderDocument Doc, LitigationOrderPropertyReading Reading)[] cases)
    {
        var docs = cases.ToDictionary(x => x.Doc.LitigationCaseOrderId, x => x.Doc);
        var readings = cases.ToDictionary(x => x.Doc.LitigationOrderDocumentId, x => x.Reading);
        var props = cases.ToDictionary(x => x.Case.LitigationCaseId,
            x => CaseOrderProperties.Build(x.Case.Orders.Select(o => (o, docs[o.LitigationCaseOrderId])).ToList(), readings));
        return ChargeLitigationLinker.Build(charges, cases.Select(x => x.Case).ToList(), docs, Godrej, props, charged);
    }

    [Fact]
    public void Properties_are_grouped_charged_first_then_charged_projects_then_other_property_in_dispute()
    {
        var charges = new List<RocCharge> { new() { ChargeId = 1, RocChargeNumber = "100603359", LatestChargeHolderRaw = "HDFC Bank Limited", ChargeStatus = "Open", Events = [] } };
        var charged = new[]
        {
            new ChargedProperty(1, "100603359", "HDFC Bank Limited", P(unit: "5C", building: "Godrej One", city: "Mumbai")),
            new ChargedProperty(2, "100496822", "Axis Bank Limited", P(project: "Commercial Project Godrej Two", city: "Mumbai", kind: PropertyKind.BuildingOrProject))
        };
        var summary = Summarise(charges, charged,
            Case(1, "OA 1/2019", null, Op(OrderPropertyRole.Attached, P(unit: "5C", building: "Godrej One", city: "Mumbai"), OrderPropertyRelation.TargetOwned, OrderPropertyDirectionKind.Attachment)),
            Case(2, "CC 2/2020", null, Op(OrderPropertyRole.Disputed, P(unit: "1804", project: "Godrej Two", city: "Mumbai"), OrderPropertyRelation.SoldByTarget)),
            // A buyer's flat in Godrej Alpine, in two cases: one entry, two cases. Not under any open charge.
            Case(3, "CC 3/2020", null, Op(OrderPropertyRole.Disputed, P(unit: "1804", project: "Godrej Alpine", city: "Mangalore"), OrderPropertyRelation.SoldByTarget)),
            Case(4, "CC 4/2021", null, Op(OrderPropertyRole.Disputed, P(unit: "1804", project: "Godrej Alpine", city: "Mangalore"), OrderPropertyRelation.SoldByTarget)),
            // Background only, and a cause-title address: neither listed.
            Case(5, "WP 5/2021", null, Op(OrderPropertyRole.Mentioned, P(project: "Godrej Garden City", city: "Ahmedabad", kind: PropertyKind.BuildingOrProject)),
                Op(OrderPropertyRole.PartyAddress, P(unit: "5C", building: "Godrej One", city: "Mumbai"), OrderPropertyRelation.TargetOwned)),
            // A tribunal order selling "the mortgaged properties described in the schedule".
            Case(6, "OA 6/2020", [new OrderUnidentifiedProperty(2, OrderPropertyRole.Security, "sale of mortgaged properties as described in the schedule")]));

        var s = summary.PropertyLitigation!;
        var chargedEntry = Assert.Single(s.ChargedProperty);
        Assert.Equal(OrderPropertyRole.Attached, chargedEntry.Role);
        Assert.Equal([OrderPropertyDirectionKind.Attachment], chargedEntry.Directions);
        Assert.Equal("100603359", Assert.Single(chargedEntry.Ties).Charge.ChargeNumber);
        Assert.Equal(2, Assert.Single(s.InChargedProject).Cases.Single().LitigationCaseId);
        var other = Assert.Single(s.NotMatchedToCharge);
        Assert.Equal([3L, 4L], other.Cases.Select(c => c.LitigationCaseId).ToArray());
        Assert.Equal(1, s.MentionedOnly);
        Assert.Equal(6, Assert.Single(s.UnidentifiedSecurityCases).LitigationCaseId);
        Assert.True(s.HasAny);
    }

    /// <summary>With no open charge, every property in dispute is one no open charge secures: the summary is still made.</summary>
    [Fact]
    public void A_company_with_no_open_charge_still_gets_its_property_in_dispute()
    {
        var summary = Summarise([], [],
            Case(3, "CC 3/2020", null, Op(OrderPropertyRole.Disputed, P(unit: "1804", project: "Godrej Alpine", city: "Mangalore"), OrderPropertyRelation.SoldByTarget)));

        Assert.Empty(summary.Links);
        var other = Assert.Single(summary.PropertyLitigation!.NotMatchedToCharge);
        Assert.Equal("1804", other.Property.UnitNumber);
    }

    [Fact]
    public void Nothing_read_yet_means_no_summary_to_show()
    {
        var c = Case(3, "CC 3/2020", null).Case;
        var summary = ChargeLitigationLinker.Build([], [c], new Dictionary<long, LitigationOrderDocument>(), Godrej, null, null);

        Assert.Null(summary.PropertyLitigation);
    }

    // ── review of #560 ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>An order that only mentions the charged office does not put it under "charged property in litigation": it is counted as mentioned.</summary>
    [Fact]
    public void A_charged_asset_only_mentioned_is_counted_not_listed()
    {
        var charges = new List<RocCharge> { new() { ChargeId = 1, RocChargeNumber = "100603359", LatestChargeHolderRaw = "HDFC Bank Limited", ChargeStatus = "Open", Events = [] } };
        var charged = new[] { new ChargedProperty(1, "100603359", "HDFC Bank Limited", P(unit: "5C", building: "Godrej One", city: "Mumbai")) };

        var s = Summarise(charges, charged, Case(1, "WP 1/2020", null, Op(OrderPropertyRole.Mentioned, P(unit: "5C", building: "Godrej One", city: "Mumbai")))).PropertyLitigation!;

        Assert.Empty(s.ChargedProperty);
        Assert.Equal(1, s.MentionedOnly);
        Assert.False(s.HasAny);
        Assert.True(s.ShouldShow);                       // shown, with the passing mention disclosed
    }

    [Fact]
    public void Orders_still_being_read_show_the_section_with_nothing_listed()
    {
        var (c, doc, reading) = Case(1, "CC 1/2020", null);
        reading.Status = LitigationOrderPropertyReadingStatus.Pending;
        reading.ResultJson = null;

        var s = Summarise([], [], (c, doc, reading)).PropertyLitigation!;

        Assert.False(s.HasAny);
        Assert.Equal(1, s.OrdersPending);
        Assert.True(s.ShouldShow);
    }

    /// <summary>No match is not proof a property is unsecured: the note says how many open charges could not be compared, or that there is none.</summary>
    [Fact]
    public void Not_matched_says_what_could_not_be_compared()
    {
        var thin = new RocCharge { ChargeId = 1, RocChargeNumber = "CHG-1", LatestChargeHolderRaw = "SBI", ChargeStatus = "Open",
            Events = [new RocChargeEvent { ChargeEventId = 1, PropertyType = "Immovable property", PropertyParticulars = "Immovable property of the company" }] };
        var stock = new RocCharge { ChargeId = 2, RocChargeNumber = "CHG-2", LatestChargeHolderRaw = "SBI", ChargeStatus = "Open",
            Events = [new RocChargeEvent { ChargeEventId = 2, PropertyType = "Movable property (not being pledge)", PropertyParticulars = "Hypothecation of stock and book debts" }] };
        var flat = Case(3, "CC 3/2020", null, Op(OrderPropertyRole.Disputed, P(unit: "1804", project: "Godrej Alpine", city: "Mangalore"), OrderPropertyRelation.SoldByTarget));

        var withCharges = Summarise([thin, stock], OrderPropertyChargeMatcher.ChargedProperties([thin, stock], null), flat).PropertyLitigation!;
        var none = Summarise([], [], flat).PropertyLitigation!;

        Assert.Single(withCharges.NotMatchedToCharge);
        Assert.Equal(2, withCharges.OpenCharges);
        Assert.Equal(1, withCharges.ChargesNotComparable);           // the thin immovable charge; the stock charge secures no immovable property
        Assert.Contains("not proof it is unsecured", withCharges.NotMatchedNote());
        Assert.Contains("1 of 2 open charge(s)", withCharges.NotMatchedNote());
        Assert.Contains("no open charge", none.NotMatchedNote());
        Assert.DoesNotContain("not under any open charge", withCharges.NotMatchedNote());
    }

    /// <summary>Review of #560: one case attaches the flat, another only mentions it. Each case shows what its own orders say, so the
    /// attachment is never shown against the case that only mentions the flat.</summary>
    [Fact]
    public void Each_case_keeps_its_own_role_and_directions()
    {
        var flat = P(unit: "1804", project: "Godrej Alpine", city: "Mangalore");
        var s = Summarise([], [],
            Case(1, "OA 1/2019", null, Op(OrderPropertyRole.Attached, flat, OrderPropertyRelation.TargetOwned, OrderPropertyDirectionKind.Attachment)),
            Case(2, "CC 2/2020", null, Op(OrderPropertyRole.Mentioned, flat))).PropertyLitigation!;

        var entry = Assert.Single(s.NotMatchedToCharge);
        var attaching = entry.Cases.Single(c => c.LitigationCaseId == 1);
        var mentioning = entry.Cases.Single(c => c.LitigationCaseId == 2);
        Assert.Equal(OrderPropertyRole.Attached, attaching.Role);
        Assert.Equal([OrderPropertyDirectionKind.Attachment], attaching.Directions);
        Assert.Equal(OrderPropertyRole.Mentioned, mentioning.Role);
        Assert.Empty(mentioning.Directions!);
        Assert.StartsWith("Attached", PropertyLitigationSummary.Says(attaching));
        Assert.Equal("Mentioned", PropertyLitigationSummary.Says(mentioning));
    }

    /// <summary>Review of #560: an order downloaded but awaiting its text is still to be read. Through the real loaders the Litigation tab
    /// (ChargeLitigationService) and the report (LitigationReportAssembler) both count it pending; an order whose text could not be extracted at
    /// all is not pending, as it will never be read.</summary>
    /// <summary>A request with one case per extraction status, each with one downloaded order without text; returns the request and the documents.</summary>
    private static async Task<(long RequestId, List<long> Documents)> SeedOrdersWithoutTextAsync(AppDbContext db, params FilingDocumentProcessingStatus[] extractions)
    {
        var client = new Client { ClientCode = "PLS" + Guid.NewGuid().ToString("N")[..7], ClientName = "Property Summary Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "Godrej Properties Limited", RequestNumber = $"PLS-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow, RawResponseHash = "h" + request.RequestId };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = job.RawResponseHash!,
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        var documents = new List<long>();
        var n = 0;
        foreach (var extraction in extractions)
        {
            var c = new LitigationCase { RequestId = request.RequestId, Court = "Consumer Court", CaseNumber = $"CC {++n}/2020", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow, PetitionersJson = "[\"Nupur Bansal\"]", RespondentsJson = "[\"Godrej Properties Limited\"]" };
            db.LitigationCases.Add(c);
            await db.SaveChangesAsync();
            db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
            var order = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "01-01-2020", OrderType = "Order", CreatedUtc = DateTime.UtcNow };
            db.LitigationCaseOrders.Add(order);
            await db.SaveChangesAsync();
            var document = new LitigationOrderDocument { LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded,
                RetainedUntilUtc = DateTime.UtcNow.AddDays(7), ExtractedText = null, TextExtractionStatus = extraction };
            db.LitigationOrderDocuments.Add(document);
            await db.SaveChangesAsync();
            documents.Add(document.LitigationOrderDocumentId);
        }
        return (request.RequestId, documents);
    }

    [Fact]
    public async Task An_order_awaiting_its_text_is_pending_on_the_tab_and_in_the_report_alike()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedOrdersWithoutTextAsync(db, FilingDocumentProcessingStatus.TextExtracting, FilingDocumentProcessingStatus.CorruptPdf);

        var tab = (await new ChargeLitigationService(db, new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 })).GetAsync(requestId)).PropertyLitigation!;
        var report = (await new LitigationReportAssembler(db).AssembleAsync(requestId, default, includeAnalysis: false))!.ChargeLinks!.PropertyLitigation!;

        Assert.Equal(1, tab.OrdersPending);                // the order awaiting its text; not the corrupt one
        Assert.True(tab.ShouldShow);
        Assert.Equal(tab.OrdersPending, report.OrdersPending);
    }

    /// <summary>Review of #560: a summary cached while an order's text is extracting must not keep calling it "being read" once the extraction
    /// ends without text; the cached comparison's version moves, so the next read through the same cache is fresh.</summary>
    [Fact]
    public async Task An_extraction_ending_without_text_moves_the_cached_summary()
    {
        await using var db = CreateContext();
        var (requestId, documents) = await SeedOrdersWithoutTextAsync(db, FilingDocumentProcessingStatus.TextExtracting);
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });

        var before = await new ChargeLitigationService(db, cache).GetAsync(requestId);
        Assert.Equal(1, before.PropertyLitigation!.OrdersPending);

        await db.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == documents[0])
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.TextExtractionStatus, (FilingDocumentProcessingStatus?)FilingDocumentProcessingStatus.CorruptPdf));
        var after = await new ChargeLitigationService(db, cache).GetAsync(requestId);   // same cache

        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal(0, after.PropertyLitigation!.OrdersPending);
        Assert.False(after.PropertyLitigation.ShouldShow);
    }
}
