using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>One litigation "thread" — a parent case plus its interlocutory applications, grouped by the
/// parent case-number stem. <see cref="GroupedAutomatically"/> is true whenever more than one source row
/// was folded together, so the dossier can show a "grouped automatically — verify against the Legal
/// History table" caveat.</summary>
public sealed record LitigationThread(string ThreadKey, IReadOnlyList<Litigation> Cases, bool GroupedAutomatically)
{
    public Litigation Primary => Cases[0];
    public int PendingCount => Cases.Count(c => DossierDeduplicator.IsPending(c.CaseStatus));
}

/// <summary>Assistive, conservative de-duplication for the dossier's Executive view. It never drops a
/// source record from the annexures — it produces secondary grouped views over them. Anything it can't
/// confidently group stays on its own.</summary>
public static class DossierDeduplicator
{
    // ── Shareholders ─────────────────────────────────────────────────────────
    /// <summary>Collapses shareholder rows that describe the same holder in the same year (the Phase 1
    /// parser already merges by (name, year); this is a safety net that also picks the richer row).</summary>
    public static List<Shareholding> MergeShareholders(IEnumerable<Shareholding> rows) =>
        rows.GroupBy(s => (s.ShareholderNameNormalized, s.FinancialYear))
            .Select(g => g.OrderByDescending(Detail).First())
            .OrderByDescending(s => s.FinancialYear)
            .ThenByDescending(s => s.HoldingPercentage ?? 0m)
            .ToList();

    private static int Detail(Shareholding s) =>
        (s.SharesHeld is not null ? 1 : 0) + (s.HoldingPercentage is not null ? 1 : 0) +
        (string.IsNullOrEmpty(s.ShareholderType) ? 0 : 1);

    // ── Related corporates / directors (exact-duplicate rows) ────────────────
    public static List<RelatedCorporate> DistinctRelatedCorporates(IEnumerable<RelatedCorporate> rows) =>
        rows.GroupBy(r => (r.EntityNameNormalized, r.Cin, r.RelationshipType, r.FinancialYearEnding))
            .Select(g => g.First())
            .ToList();

    // ── Litigation threading ────────────────────────────────────────────────
    private static readonly Regex InReference = new(@"\bin\s+(?<parent>[A-Za-z().]*\s*(No\.?\s*)?\d+[/\-]\S*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Groups interlocutory applications under their parent case. A case number that names a
    /// parent ("IA 88/2026 in CP(IB) 214/BB/2019") is threaded under that parent's normalized stem;
    /// everything else is its own single-case thread. Never merges two different parent case numbers.</summary>
    public static List<LitigationThread> ThreadLitigation(IReadOnlyList<Litigation> cases)
    {
        var byKey = new Dictionary<string, List<Litigation>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var c in cases)
        {
            var key = ThreadKeyFor(c.CaseNumber);
            if (!byKey.TryGetValue(key, out var list))
            {
                list = [];
                byKey[key] = list;
                order.Add(key);
            }
            list.Add(c);
        }

        return order
            .Select(k => new LitigationThread(k,
                byKey[k].OrderBy(c => c.CaseNumber?.Length ?? 0).ThenBy(c => c.CaseNumber).ToList(),
                GroupedAutomatically: byKey[k].Count > 1))
            .ToList();
    }

    private static string ThreadKeyFor(string? caseNumber)
    {
        if (string.IsNullOrWhiteSpace(caseNumber)) return "(unspecified)";
        var m = InReference.Match(caseNumber);
        if (m.Success) return NormalizeCaseNo(m.Groups["parent"].Value);
        return NormalizeCaseNo(caseNumber);
    }

    private static string NormalizeCaseNo(string s) =>
        Regex.Replace(s.Trim(), @"\s+", " ").TrimEnd('.', ',', ')').ToUpperInvariant();

    // ── Compliance suit-filed (display grouping only) ────────────────────────
    /// <summary>Groups the full suit-filed history by (bank, defaulter type, amount) for the Executive
    /// summary; the annexure still lists every reported quarter.</summary>
    public static List<(string Bank, string? DefaulterType, decimal? Amount, int Quarters, DateOnly? Latest)>
        SummariseSuitFiled(IEnumerable<ComplianceRecord> records) =>
        records.Where(r => r.RecordType == ComplianceRecordType.SuitFiled)
            .GroupBy(r => (Bank: r.Bank ?? "", r.DefaulterType, r.AmountCrore))
            .Select(g => (g.Key.Bank, g.Key.DefaulterType, g.Key.AmountCrore, g.Count(), g.Max(x => x.RecordDate)))
            .OrderByDescending(x => x.Item3 ?? 0m)
            .ToList();

    internal static readonly string[] ClosedKeywords = ["dispos", "clos", "resolv", "dismiss", "withdraw", "settl"];

    public static bool IsPending(string? caseStatus)
    {
        if (string.IsNullOrWhiteSpace(caseStatus)) return false;
        var t = caseStatus.ToLowerInvariant();
        return !ClosedKeywords.Any(k => t.Contains(k));
    }
}
