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
/// is, not what is in dispute. The same property named in several orders is one entry, matched on its identifying values (survey numbers,
/// unit and building, project, place), so an order that adds an area or a direction enriches the entry rather than repeating it.</summary>
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
        var groups = new List<(string Key, List<(OrderProperty P, OrderPropertyMention M)> Items)>();
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
                var key = Key(p.Property);
                var group = groups.FirstOrDefault(g => g.Key == key);
                if (group.Items is null) groups.Add((key, [(p, mention)]));
                else group.Items.Add((p, mention));
            }
            unidentified.AddRange((result.Unidentified ?? []).Select(u => new CaseUnidentifiedProperty(document.LitigationOrderDocumentId, order.OrderDate, u.Page, u.Role, u.Quote)));
        }

        var properties = groups.Select(g =>
        {
            var fullest = g.Items.OrderByDescending(i => Richness(i.P.Property)).First().P;
            var role = g.Items.Select(i => i.P.Role).OrderBy(Weight).First();
            var relation = g.Items.Select(i => i.P.Relation).FirstOrDefault(r => r != OrderPropertyRelation.Unknown, OrderPropertyRelation.Unknown);
            var issue = g.Items.Select(i => i.P.Issue).FirstOrDefault(i => i is not null);
            return new CaseOrderProperty(fullest.Property, Describe(fullest.Property), role, relation, issue, g.Items.Select(i => i.M).ToList());
        }).OrderBy(p => Weight(p.Role)).ToList();
        return new CaseOrderProperties(properties, unidentified, read, pending);
    }

    /// <summary>The property in one line, in the same words as a charged property is described.</summary>
    /// <summary>An order's property is always immovable, so the asset class is not repeated.</summary>
    public static string Describe(AiProperty p) =>
        PropertyReading.Summarize(PropertyReading.FromAi(new PropertyParticularsAiResult([p]), 0).Items[0] with { AssetClasses = [] });

    /// <summary>The identifying values, upper case with no spaces or punctuation: survey numbers if any, else unit + building/project, else the place.</summary>
    internal static string Key(AiProperty p)
    {
        static string N(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var surveys = p.SurveyNumbers.SelectMany(g => g.Numbers.Select(n => g.Scheme + ":" + N(n))).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var place = N(p.Village ?? p.City ?? p.Localities.FirstOrDefault());
        if (surveys.Count > 0) return "S|" + string.Join(",", surveys) + "|" + place;
        if (p.UnitNumber is not null) return "U|" + N(p.UnitNumber) + "|" + N(p.Building ?? p.Project);
        return "P|" + N(p.Project ?? p.Building) + "|" + place;
    }

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
