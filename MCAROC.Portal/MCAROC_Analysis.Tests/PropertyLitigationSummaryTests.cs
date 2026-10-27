using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.PropertyParticulars;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#554: the request-level "Property in litigation" summary the Litigation tab, the litigation report and the dossier show.</summary>
public sealed class PropertyLitigationSummaryTests
{
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
        var other = Assert.Single(s.NotUnderCharge);
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
        var other = Assert.Single(summary.PropertyLitigation!.NotUnderCharge);
        Assert.Equal("1804", other.Property.UnitNumber);
    }

    [Fact]
    public void Nothing_read_yet_means_no_summary_to_show()
    {
        var c = Case(3, "CC 3/2020", null).Case;
        var summary = ChargeLitigationLinker.Build([], [c], new Dictionary<long, LitigationOrderDocument>(), Godrej, null, null);

        Assert.Null(summary.PropertyLitigation);
    }
}
