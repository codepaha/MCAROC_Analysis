using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.PropertyParticulars;

/// <summary>A case in which a property is named, with what that case's own orders say about it: an attachment in one case is never shown against
/// another case that only mentions the property.</summary>
public sealed record PropertyLitigationCase(long LitigationCaseId, string? CaseNumber, string? Court, string? Status,
    OrderPropertyRole Role = OrderPropertyRole.Mentioned, OrderPropertyIssue? Issue = null, IReadOnlyList<OrderPropertyDirectionKind>? Directions = null);

/// <summary>One property across the request's cases: the same asset named in several cases is one entry (<see cref="CaseOrderProperties.SameProperty"/>).
/// <see cref="Role"/> is the weightiest any order gives it, for ordering and grouping only: what each case's orders say is on that case
/// (<see cref="PropertyLitigationCase.Role"/>). <see cref="Ties"/>: the open charges it is, or lies in the project of.</summary>
public sealed record PropertyLitigationEntry(
    AiProperty Property, string Description, OrderPropertyRole Role, OrderPropertyRelation Relation, OrderPropertyIssue? Issue,
    IReadOnlyList<OrderPropertyDirectionKind> Directions, IReadOnlyList<PropertyChargeTie> Ties, IReadOnlyList<PropertyLitigationCase> Cases)
{
    public bool IsChargedProperty => Ties.Any(t => t.Kind == PropertyChargeTieKind.SameProperty);
    public bool IsInChargedProject => !IsChargedProperty && Ties.Any(t => t.Kind == PropertyChargeTieKind.ChargedProject);
}

/// <summary>Every property the request's court orders name (#554), for the Litigation tab and the litigation report: first the charged properties in
/// litigation, then units of charged projects, then the properties in dispute that matched no open charge. Only a property the orders put in issue
/// (disputed, a lender's security, attached) is listed; one only mentioned in passing is counted, whatever it matches; party addresses are never
/// part of it. <see cref="UnidentifiedSecurityCases"/>: cases whose orders act on properties they do not describe ("sale of the mortgaged properties
/// as described in the schedule").
/// <para>Not matching a charge is not proof a property is unsecured: a charge or an order may describe it too thinly to compare.
/// <see cref="ChargesNotComparable"/> counts the open charges over immovable property whose description names nothing to compare, and
/// <see cref="NotMatchedNote"/> says so wherever the group is shown.</para></summary>
public sealed record PropertyLitigationSummary(
    IReadOnlyList<PropertyLitigationEntry> ChargedProperty, IReadOnlyList<PropertyLitigationEntry> InChargedProject,
    IReadOnlyList<PropertyLitigationEntry> NotMatchedToCharge, int MentionedOnly, IReadOnlyList<PropertyLitigationCase> UnidentifiedSecurityCases,
    int CasesRead, int OrdersPending, int OpenCharges = 0, int ChargesNotComparable = 0)
{
    public static PropertyLitigationSummary Empty { get; } = new([], [], [], 0, [], 0, 0);

    public bool HasAny => ChargedProperty.Count > 0 || InChargedProject.Count > 0 || NotMatchedToCharge.Count > 0 || UnidentifiedSecurityCases.Count > 0;

    /// <summary>Whether to show the section: something to list, or something to disclose (orders still being read, properties only mentioned).</summary>
    public bool ShouldShow => HasAny || MentionedOnly > 0 || OrdersPending > 0;

    /// <summary>What "not matched" can and cannot say, for the tab and the report alike.</summary>
    public string NotMatchedNote() =>
        OpenCharges == 0
            ? "In dispute, under a lender's security or attached. The company has no open charge."
            : "In dispute, under a lender's security or attached, and no open charge's property description matched it. That is not proof it is unsecured: "
              + (ChargesNotComparable == 0
                  ? "a charge or an order may describe the property in words that cannot be compared."
                  : $"{ChargesNotComparable} of {OpenCharges} open charge(s) describe the immovable property they secure too thinly to compare.");

    /// <param name="orderProperties">Each case's properties, with their charge ties (<see cref="CaseOrderProperties.WithTies"/>).</param>
    /// <param name="openCharges">The company's open charges, and how many of them secure immovable property described too thinly to compare.</param>
    public static PropertyLitigationSummary Build(IReadOnlyList<LitigationCase> cases, IReadOnlyDictionary<long, CaseOrderProperties> orderProperties,
        int openCharges = 0, int chargesNotComparable = 0)
    {
        var groups = new List<List<(CaseOrderProperty P, LitigationCase Case)>>();
        var unidentified = new List<PropertyLitigationCase>();
        int read = 0, pending = 0;
        foreach (var c in cases.OrderBy(c => c.LitigationCaseId))
        {
            if (!orderProperties.TryGetValue(c.LitigationCaseId, out var props)) continue;
            if (props.OrdersRead > 0) read++;
            pending += props.OrdersPending;
            foreach (var p in props.Properties)
            {
                var group = groups.FirstOrDefault(g => g.All(i => CaseOrderProperties.SameProperty(i.P.Property, p.Property)));
                if (group is null) groups.Add([(p, c)]);
                else group.Add((p, c));
            }
            if (props.Unidentified.Any(u => u.Role is OrderPropertyRole.Security or OrderPropertyRole.Attached))
                unidentified.Add(CaseOf(c, []));
        }

        var entries = groups.Select(g =>
        {
            var fullest = g.OrderByDescending(i => i.P.Mentions.Count).ThenByDescending(i => i.P.Description.Length).First().P;
            return new PropertyLitigationEntry(fullest.Property, fullest.Description,
                g.Select(i => i.P.Role).OrderBy(CaseOrderProperties.Weight).First(),
                g.Select(i => i.P.Relation).FirstOrDefault(r => r != OrderPropertyRelation.Unknown, OrderPropertyRelation.Unknown),
                g.Select(i => i.P.Issue).FirstOrDefault(i => i is not null),
                g.SelectMany(i => i.P.Directions.Select(d => d.Direction.Kind)).Distinct().ToList(),
                g.SelectMany(i => i.P.Ties ?? []).GroupBy(t => (t.Charge.ChargeId, t.Kind)).Select(t => t.First()).ToList(),
                g.GroupBy(i => i.Case.LitigationCaseId).Select(byCase => CaseOf(byCase.First().Case, byCase.Select(i => i.P).ToList())).ToList());
        }).OrderBy(e => CaseOrderProperties.Weight(e.Role)).ThenByDescending(e => e.Cases.Count).ToList();

        // Only what the orders put in issue is listed, in every group: a charged asset merely mentioned is not "charged property in litigation".
        var inIssue = entries.Where(e => e.Role != OrderPropertyRole.Mentioned).ToList();
        return new PropertyLitigationSummary(
            inIssue.Where(e => e.IsChargedProperty).ToList(),
            inIssue.Where(e => e.IsInChargedProject).ToList(),
            inIssue.Where(e => e.Ties.Count == 0).ToList(),
            entries.Count(e => e.Role == OrderPropertyRole.Mentioned),
            unidentified, read, pending, openCharges, chargesNotComparable);
    }

    /// <summary>The case, with the weightiest role, an issue and the directions its own orders give the property.</summary>
    private static PropertyLitigationCase CaseOf(LitigationCase c, IReadOnlyList<CaseOrderProperty> own) =>
        new(c.LitigationCaseId, c.CaseNumber ?? c.Cnr, c.Court, c.CaseStatus,
            own.Count == 0 ? OrderPropertyRole.Mentioned : own.Select(p => p.Role).OrderBy(CaseOrderProperties.Weight).First(),
            own.Select(p => p.Issue).FirstOrDefault(i => i is not null),
            own.SelectMany(p => p.Directions.Select(d => d.Direction.Kind)).Distinct().ToList());

    /// <summary>"Attached: possession (Attachment)" — what one case's orders say about the property.</summary>
    public static string Says(PropertyLitigationCase c)
    {
        var issue = OrderPropertyLabels.Issue(c.Issue);
        var directions = c.Directions is { Count: > 0 } d ? " (" + string.Join(", ", d.Select(OrderPropertyLabels.Direction)) + ")" : "";
        return OrderPropertyLabels.Role(c.Role) + (issue is null ? "" : ": " + issue) + directions;
    }
}
