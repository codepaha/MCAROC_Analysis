using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Models;

/// <summary>The one implementation of the derived values shared by the on-screen company page
/// (<see cref="RequestDetailsViewModel"/>) and the client dossier (<c>DossierAssembler</c>) — charge
/// portfolio maths, lender concentration, litigation role attribution, revenue trend. Pure functions
/// over already-loaded collections. Guarded by the golden-master parity test.</summary>
public static class DossierComputations
{
    // ── Charges ─────────────────────────────────────────────────────────────
    public static bool IsOpenCharge(RocCharge c) =>
        c.SatisfactionDate is null &&
        !string.Equals(c.ChargeStatus, "Satisfied", StringComparison.OrdinalIgnoreCase);

    public static List<RocCharge> OpenChargesByAmount(IEnumerable<RocCharge> charges) =>
        charges.Where(IsOpenCharge).OrderByDescending(c => c.CurrentAmount ?? 0m).ToList();

    public static List<RocCharge> SatisfiedChargesBySatisfaction(IEnumerable<RocCharge> charges) =>
        charges.Where(c => !IsOpenCharge(c)).OrderByDescending(c => c.SatisfactionDate).ToList();

    public static int ChargeHolderCount(IEnumerable<RocCharge> charges) =>
        charges.Select(c => c.LatestChargeHolderNormalized).Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public static int ModifiedChargeCount(IEnumerable<RocCharge> charges) =>
        charges.Count(c => c.Events.Count > 1 || c.Events.Any(e => e.EventType == ChargeEventType.Modification));

    /// <summary>SecurityType values a charge's rollup names — read from LatestSecurityTypesJson, never re-derived.</summary>
    public static IReadOnlyList<string> SecurityTypeLabels(RocCharge c)
    {
        if (string.IsNullOrWhiteSpace(c.LatestSecurityTypesJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(c.LatestSecurityTypesJson) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Open charges grouped by normalized holder → count, Σ registered amount, % of open
    /// registered amount. Sorted by amount desc.</summary>
    public static List<LenderConcentrationRow> LenderConcentration(IEnumerable<RocCharge> charges)
    {
        var open = OpenChargesByAmount(charges);
        var totalOpen = open.Sum(c => c.CurrentAmount ?? 0m);
        return open
            .GroupBy(c => string.IsNullOrWhiteSpace(c.LatestChargeHolderNormalized)
                ? c.LatestChargeHolderRaw : c.LatestChargeHolderNormalized)
            .Select(g =>
            {
                var amt = g.Sum(c => c.CurrentAmount ?? 0m);
                return new LenderConcentrationRow(g.Key, g.Count(), amt,
                    totalOpen == 0m ? null : Math.Round(amt / totalOpen * 100m, 1));
            })
            .OrderByDescending(r => r.RegisteredAmount)
            .ThenByDescending(r => r.OpenCount)
            .ToList();
    }

    // ── Financials ──────────────────────────────────────────────────────────
    /// <summary>Year-on-year revenue change, percent — null unless the two latest years both have a
    /// non-zero revenue. <paramref name="standalone"/> may be in any order.</summary>
    public static decimal? RevenueYoYPercent(IEnumerable<FinancialYearData> standalone)
    {
        var asc = standalone.OrderBy(f => f.FinancialYear).ToList();
        if (asc.Count < 2) return null;
        var current = asc[^1].Revenue;
        var prior = asc[^2].Revenue;
        if (current is null || prior is null || prior.Value == 0m) return null;
        return Math.Round((current.Value - prior.Value) / Math.Abs(prior.Value) * 100m, 1);
    }

    // ── Litigation role attribution ─────────────────────────────────────────
    public static bool IsPendingLitigation(Litigation l)
    {
        if (string.IsNullOrWhiteSpace(l.CaseStatus)) return false;
        var t = l.CaseStatus.ToLowerInvariant();
        return !new[] { "dispos", "clos", "resolv", "dismiss", "withdraw", "settl" }.Any(k => t.Contains(k));
    }

    private static HashSet<long> LitigationIdsFromFinding(IEnumerable<AnalysisFinding> findings, string code)
    {
        var json = findings.FirstOrDefault(x => x.Code == code)?.SourceReferenceJson;
        if (json is null) return [];
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("entityIds", out var ids)
                && ids.ValueKind == JsonValueKind.Array)
                return ids.EnumerateArray().Where(e => e.TryGetInt64(out _)).Select(e => e.GetInt64()).ToHashSet();
        }
        catch (JsonException) { }
        return [];
    }

    /// <summary>Per-case role — only ever taken from a Phase 3 finding's SourceReferenceJson. "Filed By"
    /// is the residue of the rule's own deterministic partition (pending + confirmed cases the rule
    /// classified as neither "against" nor "role-uncertain"). A case named by no finding is
    /// "Role not determined" — never inferred at display time.</summary>
    public static Dictionary<long, LitigationRole> LitigationRoles(
        IEnumerable<Litigation> litigations, IReadOnlyList<AnalysisFinding> findings)
    {
        var against = LitigationIdsFromFinding(findings, LitigationRules.PendingAgainstCompanyCode);
        var uncertain = LitigationIdsFromFinding(findings, LitigationRules.RoleUncertainCode);
        var map = new Dictionary<long, LitigationRole>();
        foreach (var l in litigations)
        {
            if (against.Contains(l.LitigationId)) map[l.LitigationId] = LitigationRole.FiledAgainst;
            else if (uncertain.Contains(l.LitigationId)) map[l.LitigationId] = LitigationRole.NotDetermined;
            else if (IsPendingLitigation(l) && l.MatchStatus == LitigationMatchStatus.Confirmed
                     && (against.Count > 0 || uncertain.Count > 0))
                map[l.LitigationId] = LitigationRole.FiledBy;
            else map[l.LitigationId] = LitigationRole.NotDetermined;
        }
        return map;
    }
}
