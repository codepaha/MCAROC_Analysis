using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;

namespace MCAROC_Analysis.Services.PropertyParticulars;

/// <summary>Where an order names a property: the order, its document (for the page link) and the page.</summary>
public sealed record OrderPropertyMention(long LitigationCaseOrderId, long LitigationOrderDocumentId, string? OrderDate, string? OrderType, int Page,
    string Quote, OrderPropertyRole Role, OrderPropertyRelation Relation, OrderPropertyIssue? Issue, IReadOnlyList<OrderPropertyDirection> Directions);

/// <summary>One property a case's orders name, with every order that names it. <see cref="Property"/> is the fullest description among them.
/// <see cref="Role"/> is the weightiest one any order gives it (attached over security over disputed over mentioned).</summary>
public sealed record CaseOrderProperty(AiProperty Property, string Description, OrderPropertyRole Role, OrderPropertyRelation Relation,
    OrderPropertyIssue? Issue, IReadOnlyList<OrderPropertyMention> Mentions)
{
    public IEnumerable<(OrderPropertyMention Mention, OrderPropertyDirection Direction)> Directions =>
        Mentions.SelectMany(m => m.Directions.Select(d => (m, d)));
}

/// <summary>A property an order acts on without describing it, with where.</summary>
public sealed record CaseUnidentifiedProperty(long LitigationOrderDocumentId, string? OrderDate, int Page, OrderPropertyRole Role, string Quote);

/// <summary>The properties a case's orders name, read from the stored readings (#554). Party addresses are left out: they say where a party
/// is, not what is in dispute. The same property named in several orders is one entry (<see cref="SameProperty"/>), so an order that adds an
/// area or a direction enriches the entry rather than repeating it; two properties that only share a common token stay apart, so one asset's
/// attachment never shows against another.</summary>
public sealed record CaseOrderProperties(
    IReadOnlyList<CaseOrderProperty> Properties, IReadOnlyList<CaseUnidentifiedProperty> Unidentified, int OrdersRead, int OrdersPending)
{
    public static CaseOrderProperties Empty { get; } = new([], [], 0, 0);

    public bool HasAny => Properties.Count > 0 || Unidentified.Count > 0;

    /// <param name="orders">The case's orders with their document, in order.</param>
    /// <param name="readings">The readings of those documents, by document id (any status).</param>
    public static CaseOrderProperties Build(IReadOnlyList<(LitigationCaseOrder Order, LitigationOrderDocument Document)> orders,
        IReadOnlyDictionary<long, LitigationOrderPropertyReading> readings)
    {
        var groups = new List<List<(OrderProperty P, OrderPropertyMention M)>>();
        var unidentified = new List<CaseUnidentifiedProperty>();
        int read = 0, pending = 0;
        foreach (var (order, document) in orders)
        {
            if (!readings.TryGetValue(document.LitigationOrderDocumentId, out var reading)) { pending++; continue; }
            if (!reading.IsTerminal) { pending++; continue; }
            read++;
            if (OrderPropertiesAi.Deserialize(reading.ResultJson) is not { } result) continue;
            foreach (var p in result.Properties.Where(p => p.Role != OrderPropertyRole.PartyAddress))
            {
                var mention = new OrderPropertyMention(order.LitigationCaseOrderId, document.LitigationOrderDocumentId, order.OrderDate, order.OrderType, p.Page,
                    p.Property.SourceText ?? "", p.Role, p.Relation, p.Issue, p.Directions);
                // Joins a group only when it is the same property as every member: compared to all of them, never chained through one.
                var group = groups.FirstOrDefault(g => g.All(i => SameProperty(i.P.Property, p.Property)));
                if (group is null) groups.Add([(p, mention)]);
                else group.Add((p, mention));
            }
            unidentified.AddRange((result.Unidentified ?? []).Select(u => new CaseUnidentifiedProperty(document.LitigationOrderDocumentId, order.OrderDate, u.Page, u.Role, u.Quote)));
        }

        var properties = groups.Select(g =>
        {
            var fullest = g.OrderByDescending(i => Richness(i.P.Property)).First().P;
            var role = g.Select(i => i.P.Role).OrderBy(Weight).First();
            var relation = g.Select(i => i.P.Relation).FirstOrDefault(r => r != OrderPropertyRelation.Unknown, OrderPropertyRelation.Unknown);
            var issue = g.Select(i => i.P.Issue).FirstOrDefault(i => i is not null);
            return new CaseOrderProperty(fullest.Property, Describe(fullest.Property), role, relation, issue, g.Select(i => i.M).ToList());
        }).OrderBy(p => Weight(p.Role)).ToList();
        return new CaseOrderProperties(properties, unidentified, read, pending);
    }

    /// <summary>The property in one line, in the same words as a charged property is described. An order's property is always immovable,
    /// so the asset class is not repeated.</summary>
    public static string Describe(AiProperty p) =>
        PropertyReading.Summarize(PropertyReading.FromAi(new PropertyParticularsAiResult([p]), 0).Items[0] with { AssetClasses = [] });

    /// <summary>Whether two order properties are the same asset. Nothing they both state may differ (unit, building, project, village, city,
    /// PIN, or survey numbers of the same scheme with none in common), and they must share an identity a place or building backs:
    /// <list type="bullet">
    /// <item>the same unit in the same building or project;</item>
    /// <item>a survey/CTS/khasra number in common in the same village or PIN (survey numbers repeat across villages, so a city is not enough);</item>
    /// <item>for a building or project with no unit or survey number, the same name in the same village, city or PIN.</item>
    /// </list></summary>
    public static bool SameProperty(AiProperty a, AiProperty b)
    {
        if (IdConflict(a.UnitNumber, b.UnitNumber) || Conflict(a.Building, b.Building) || Conflict(a.Project, b.Project)
            || Conflict(a.Village, b.Village) || Conflict(a.City, b.City) || Conflict(a.Pin, b.Pin))
            return false;
        var surveysA = Surveys(a);
        var surveysB = Surveys(b);
        foreach (var scheme in surveysA.Keys.Intersect(surveysB.Keys))
            if (!surveysA[scheme].Overlaps(surveysB[scheme])) return false;

        var sameVillageOrPin = Same(a.Village, b.Village) || Same(a.Pin, b.Pin);
        if (a.UnitNumber is not null && b.UnitNumber is not null)
            return SameId(a.UnitNumber, b.UnitNumber) && (Same(a.Building, b.Building) || Same(a.Project, b.Project));
        if (surveysA.Count > 0 && surveysB.Count > 0)
            return surveysA.Keys.Intersect(surveysB.Keys).Any() && sameVillageOrPin;
        if (a.UnitNumber is null && b.UnitNumber is null && surveysA.Count == 0 && surveysB.Count == 0)
            return (Same(a.Project, b.Project) || Same(a.Building, b.Building)) && (sameVillageOrPin || Same(a.City, b.City));
        return false;   // one names a unit or survey number the other does not: not enough to say they are one asset
    }

    private static string N(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool Same(string? a, string? b) => a is not null && b is not null && N(a).Length > 0 && N(a) == N(b);
    private static bool Conflict(string? a, string? b) => a is not null && b is not null && N(a).Length > 0 && N(b).Length > 0 && N(a) != N(b);

    /// <summary>A unit or survey number compared as written, but for spacing, case and the dash used: its separators are part of it
    /// ("1/23" and "12/3" are two parcels, "B-21" and "B2-1" two flats). Names and places are compared on letters and digits only.</summary>
    internal static string Id(string? s) =>
        new((s ?? "").Where(c => !char.IsWhiteSpace(c)).Select(c => c is '\u2013' or '\u2014' or '\u2212' ? '-' : char.ToUpperInvariant(c)).ToArray());
    private static bool SameId(string? a, string? b) => a is not null && b is not null && Id(a).Length > 0 && Id(a) == Id(b);
    private static bool IdConflict(string? a, string? b) => a is not null && b is not null && Id(a).Length > 0 && Id(b).Length > 0 && Id(a) != Id(b);

    private static Dictionary<string, HashSet<string>> Surveys(AiProperty p) =>
        p.SurveyNumbers.GroupBy(g => g.Scheme).ToDictionary(g => g.Key, g => g.SelectMany(x => x.Numbers).Select(Id).Where(n => n.Length > 0).ToHashSet());

    private static int Richness(AiProperty p) =>
        (p.UnitNumber is null ? 0 : 1) + (p.Building is null ? 0 : 1) + (p.Project is null ? 0 : 1) + p.SurveyNumbers.Count + p.Areas.Count
        + p.Localities.Count + (p.Village is null ? 0 : 1) + (p.City is null ? 0 : 1) + (p.Pin is null ? 0 : 1);

    public static int Weight(OrderPropertyRole role) => role switch
    {
        OrderPropertyRole.Attached => 0,
        OrderPropertyRole.Security => 1,
        OrderPropertyRole.Disputed => 2,
        _ => 3
    };
}

/// <summary>Plain words for what an order says about a property.</summary>
public static class OrderPropertyLabels
{
    public static string Role(OrderPropertyRole role) => role switch
    {
        OrderPropertyRole.Attached => "Attached / taken by the court",
        OrderPropertyRole.Security => "Lender's security in recovery",
        OrderPropertyRole.Disputed => "In dispute",
        _ => "Mentioned"
    };

    public static string RoleCss(OrderPropertyRole role) => role switch
    {
        OrderPropertyRole.Attached => "sev-critical",
        OrderPropertyRole.Security => "sev-critical",
        OrderPropertyRole.Disputed => "sev-watch",
        _ => ""
    };

    public static string? Relation(OrderPropertyRelation relation) => relation switch
    {
        OrderPropertyRelation.TargetOwned => "the company's",
        OrderPropertyRelation.SoldByTarget => "sold or allotted by the company",
        OrderPropertyRelation.OtherParty => "another party's",
        _ => null
    };

    public static string? Issue(OrderPropertyIssue? issue) => issue switch
    {
        OrderPropertyIssue.Title => "title",
        OrderPropertyIssue.Possession => "possession",
        OrderPropertyIssue.DelayedPossession => "delayed possession",
        OrderPropertyIssue.Refund => "refund",
        OrderPropertyIssue.Allotment => "allotment",
        OrderPropertyIssue.Construction => "construction",
        OrderPropertyIssue.Recovery => "recovery",
        OrderPropertyIssue.Tenancy => "tenancy",
        OrderPropertyIssue.Acquisition => "acquisition",
        OrderPropertyIssue.Other => "other",
        _ => null
    };

    public static string Direction(OrderPropertyDirectionKind kind) => kind switch
    {
        OrderPropertyDirectionKind.StatusQuo => "Status quo",
        _ => kind.ToString()
    };
}
