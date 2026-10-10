using System.Text;
using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Normalisation;

/// <summary>What the backfill needs from one stored case. A plain row, so the analysis is a pure function.</summary>
public sealed record CaseBackfillRow(
    long LitigationCaseId, long RequestId, string? Cnr, string? ProceedingType, string? Court, string? CaseNumber, string? CaseType,
    string? CaseYear, string? FilingDate);

/// <summary>A case whose number is not complete, with what the normaliser made of it.</summary>
public sealed record CaseBackfillIssue(long LitigationCaseId, long RequestId, CaseNumberCompleteness Completeness, string Display, string? Note, string Raw);

/// <summary>Cases of one request that read as the same court, type, number and year but are separate rows. A <b>report only</b>: nothing is merged here.
/// Merging happens in the module's de-duplication, and only for gate-complete equal keys (plan section 4.6).</summary>
public sealed record CasePossibleDuplicateGroup(long RequestId, string Number, IReadOnlyList<long> LitigationCaseIds);

public sealed record CaseBackfillReport(
    int Total, int Complete, int Partial, int Unresolved,
    IReadOnlyList<CaseBackfillIssue> Issues, IReadOnlyList<CasePossibleDuplicateGroup> PossibleDuplicates)
{
    /// <summary>A short plain-text summary for logs and the owner.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Cases: {Total}  complete: {Complete}  partial: {Partial}  unresolved: {Unresolved}  possible duplicate groups: {PossibleDuplicates.Count}");
        foreach (var i in Issues.Take(50)) sb.AppendLine($"  #{i.LitigationCaseId} [{i.Completeness}] \"{i.Raw}\" -> \"{i.Display}\"{(i.Note is null ? "" : " (" + i.Note + ")")}");
        if (Issues.Count > 50) sb.AppendLine($"  ... and {Issues.Count - 50} more");
        foreach (var g in PossibleDuplicates.Take(50)) sb.AppendLine($"  duplicate? request {g.RequestId} {g.Number}: cases {string.Join(", ", g.LitigationCaseIds)}");
        return sb.ToString();
    }
}

/// <summary>The backfill report of plan section 3 / issue #434: how many stored cases have a complete, partial or unresolved case number, which they are,
/// and which look like the same case stored twice. <b>Read-only and idempotent</b>: the same rows always give the same report, in the same order, and nothing is written.</summary>
public static class CaseNumberBackfill
{
    public static CaseBackfillReport Analyse(IEnumerable<CaseBackfillRow> rows)
    {
        var parsed = rows.OrderBy(r => r.LitigationCaseId)
            .Select(r => (Row: r, Parts: CaseNumberNormaliser.Parse(new CaseNumberInput(r.CaseNumber, r.CaseType, r.CaseYear, r.FilingDate, r.Cnr))))
            .ToList();

        var issues = parsed.Where(p => p.Parts.Completeness != CaseNumberCompleteness.Complete)
            .Select(p => new CaseBackfillIssue(p.Row.LitigationCaseId, p.Row.RequestId, p.Parts.Completeness, p.Parts.Display, p.Parts.Note, p.Parts.Raw))
            .ToList();

        // Possible duplicates: same request, same canonical court, and a complete type/number/year that is equal - yet different rows. Rows that share a CNR and
        // proceeding type are already one row by the CNR-first rule, so they cannot appear here as two. Free-text court names are compared case-insensitively
        // after the alias table, which is enough for a report and is not identity.
        var groups = parsed
            .Where(p => p.Parts.Completeness == CaseNumberCompleteness.Complete && p.Parts.ConnectedMatters.Count == 0)
            .GroupBy(p => (p.Row.RequestId, Court: CourtNames.Normalise(p.Row.Court).Canonical.ToUpperInvariant(), p.Parts.Display))
            .Where(g => g.Select(x => x.Row.LitigationCaseId).Distinct().Count() > 1 && !AllShareACnr(g.Select(x => x.Row)))
            .OrderBy(g => g.Key.RequestId).ThenBy(g => g.Key.Display, StringComparer.Ordinal).ThenBy(g => g.Key.Court, StringComparer.Ordinal)
            .Select(g => new CasePossibleDuplicateGroup(g.Key.RequestId, g.Key.Display, g.Select(x => x.Row.LitigationCaseId).OrderBy(i => i).ToList()))
            .ToList();

        return new CaseBackfillReport(
            parsed.Count,
            parsed.Count(p => p.Parts.Completeness == CaseNumberCompleteness.Complete),
            parsed.Count(p => p.Parts.Completeness == CaseNumberCompleteness.Partial),
            parsed.Count(p => p.Parts.Completeness == CaseNumberCompleteness.Unresolved),
            issues, groups);
    }

    /// <summary>Two rows with different, valid CNRs are different cases even when the numbers read alike (two courts, same number), so they are not suspected.</summary>
    private static bool AllShareACnr(IEnumerable<CaseBackfillRow> rows)
    {
        var cnrs = rows.Select(r => r.Cnr).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim().ToUpperInvariant()).ToList();
        return cnrs.Count == rows.Count() && cnrs.Distinct().Count() == cnrs.Count;
    }

    /// <summary>Reads the stored cases (one request, or all when <paramref name="requestId"/> is null) and analyses them. Writes nothing.</summary>
    public static async Task<CaseBackfillReport> RunAsync(AppDbContext db, long? requestId, CancellationToken ct)
    {
        var query = db.LitigationCases.AsNoTracking();
        if (requestId is { } id) query = query.Where(c => c.RequestId == id);
        var rows = await query.Select(c => new CaseBackfillRow(c.LitigationCaseId, c.RequestId, c.Cnr, c.ProceedingType, c.Court, c.CaseNumber, c.CaseType, c.CaseYear, c.FilingDate))
            .ToListAsync(ct);
        return Analyse(rows);
    }
}
