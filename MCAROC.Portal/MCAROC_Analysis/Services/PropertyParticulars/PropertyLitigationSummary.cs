using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.PropertyParticulars;

/// <summary>A case in which a property is named.</summary>
public sealed record PropertyLitigationCase(long LitigationCaseId, string? CaseNumber, string? Court, string? Status);

/// <summary>One property across the request's cases: the same asset named in several cases is one entry (<see cref="CaseOrderProperties.SameProperty"/>).
/// <see cref="Role"/> is the weightiest any order gives it; <see cref="Ties"/> the open charges it is, or lies in the project of.</summary>
public sealed record PropertyLitigationEntry(
    AiProperty Property, string Description, OrderPropertyRole Role, OrderPropertyRelation Relation, OrderPropertyIssue? Issue,
    IReadOnlyList<OrderPropertyDirectionKind> Directions, IReadOnlyList<PropertyChargeTie> Ties, IReadOnlyList<PropertyLitigationCase> Cases)
{
    public bool IsChargedProperty => Ties.Any(t => t.Kind == PropertyChargeTieKind.SameProperty);
    public bool IsInChargedProject => !IsChargedProperty && Ties.Any(t => t.Kind == PropertyChargeTieKind.ChargedProject);
}

/// <summary>Every property the request's court orders name (#554), for the Litigation tab, the litigation report and the dossier: first the charged
/// properties in litigation, then units of charged projects, then the properties in dispute that no open charge secures. A property only mentioned
/// in passing is counted, not listed; party addresses are never part of it. <see cref="UnidentifiedSecurityCases"/>: cases whose orders act on
/// properties they do not describe ("sale of the mortgaged properties as described in the schedule").</summary>
public sealed record PropertyLitigationSummary(
    IReadOnlyList<PropertyLitigationEntry> ChargedProperty, IReadOnlyList<PropertyLitigationEntry> InChargedProject,
    IReadOnlyList<PropertyLitigationEntry> NotUnderCharge, int MentionedOnly, IReadOnlyList<PropertyLitigationCase> UnidentifiedSecurityCases,
    int CasesRead, int OrdersPending)
{
    public static PropertyLitigationSummary Empty { get; } = new([], [], [], 0, [], 0, 0);

    public bool HasAny => ChargedProperty.Count > 0 || InChargedProject.Count > 0 || NotUnderCharge.Count > 0 || UnidentifiedSecurityCases.Count > 0;

    /// <param name="orderProperties">Each case's properties, with their charge ties (<see cref="CaseOrderProperties.WithTies"/>).</param>
    public static PropertyLitigationSummary Build(IReadOnlyList<LitigationCase> cases, IReadOnlyDictionary<long, CaseOrderProperties> orderProperties)
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
                unidentified.Add(CaseOf(c));
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
                g.Select(i => i.Case).DistinctBy(c => c.LitigationCaseId).Select(CaseOf).ToList());
        }).OrderBy(e => CaseOrderProperties.Weight(e.Role)).ThenByDescending(e => e.Cases.Count).ToList();

        var inDispute = entries.Where(e => e.Role != OrderPropertyRole.Mentioned).ToList();
        return new PropertyLitigationSummary(
            entries.Where(e => e.IsChargedProperty).ToList(),
            entries.Where(e => e.IsInChargedProject).ToList(),
            inDispute.Where(e => e.Ties.Count == 0).ToList(),
            entries.Count(e => e.Role == OrderPropertyRole.Mentioned && e.Ties.Count == 0),
            unidentified, read, pending);
    }

    private static PropertyLitigationCase CaseOf(LitigationCase c) => new(c.LitigationCaseId, c.CaseNumber ?? c.Cnr, c.Court, c.CaseStatus);
}
