using System.Text.RegularExpressions;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.PreLoginReports;

/// <summary>
/// Reconciles incoming legal cases with existing assignment records during background polling,
/// director searches, manual re-polling, and vendor refreshes per §8 and §11.
/// Preserves stable CaseRowIds, analyst exclusions, and notes on verified 1-to-1 matches,
/// and allocates distinct sequential IDs for separate observations.
/// </summary>
public static partial class LitigationCaseReconciliationService
{
    public static InstaLegalCases ReconcileCases(InstaLegalCases? existing, IReadOnlyList<LegalCaseRecord> incoming)
    {
        var existingCases = existing?.Cases ?? [];
        int nextIndex = existing?.NextCaseRowIdIndex ?? 1;
        if (existingCases.Count > 0 && nextIndex <= existingCases.Count)
        {
            nextIndex = existingCases.Count + 1;
        }

        if (existingCases.Count == 0)
        {
            var initialList = new List<LegalCaseRecord>(incoming.Count);
            foreach (var inc in incoming)
            {
                var assigned = inc with { CaseRowId = $"C-{nextIndex++}" };
                initialList.Add(assigned);
            }
            return LegalCaseFileParser.ToInstaLegalCases(initialList) with { NextCaseRowIdIndex = nextIndex };
        }

        var reconciled = new List<LegalCaseRecord>();
        var matchedExisting = new HashSet<string>();

        // Build existing lookup structures
        var existingWithCnr = existingCases
            .Where(e => !string.IsNullOrWhiteSpace(LitigationCaseIdentity.NormaliseCnr(e.CnrNumber)))
            .GroupBy(e => LitigationCaseIdentity.NormaliseCnr(e.CnrNumber)!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var existingCnrLess = existingCases
            .Where(e => string.IsNullOrWhiteSpace(LitigationCaseIdentity.NormaliseCnr(e.CnrNumber)))
            .ToList();

        foreach (var inc in incoming)
        {
            var normCnr = LitigationCaseIdentity.NormaliseCnr(inc.CnrNumber);
            LegalCaseRecord? match = null;

            if (normCnr is not null)
            {
                // CNR-based 1-to-1 match
                if (existingWithCnr.TryGetValue(normCnr, out var candidates))
                {
                    var compatible = candidates.Where(c =>
                        !matchedExisting.Contains(c.CaseRowId) &&
                        AreCompatibleProceedingTypes(c.ProceedingType ?? c.CaseType, inc.ProceedingType ?? inc.CaseType)
                    ).ToList();

                    if (compatible.Count == 1)
                    {
                        match = compatible[0];
                    }
                }
            }
            else
            {
                // Strict Non-CNR / Court Proceeding Match (§8 exception to §2.4)
                if (IsCompleteIdentity(inc))
                {
                    var compatible = existingCnrLess.Where(c =>
                        !matchedExisting.Contains(c.CaseRowId) &&
                        IsCompleteIdentity(c) &&
                        IsExactTupleMatch(c, inc)
                    ).ToList();

                    if (compatible.Count == 1)
                    {
                        match = compatible[0];
                    }
                }
            }

            if (match is not null)
            {
                matchedExisting.Add(match.CaseRowId);

                // Preserve existing CaseRowId and analyst state
                var isConcluded = LitigationCaseStatusClassifier.IsConcluded(match.Status, match.CaseStage);
                var mergedStatus = isConcluded ? match.Status : inc.Status;
                var mergedStage = isConcluded ? match.CaseStage : inc.CaseStage;

                reconciled.Add(inc with
                {
                    CaseRowId = match.CaseRowId,
                    Status = mergedStatus,
                    CaseStage = mergedStage,
                    IsExcludedFromReport = match.IsExcludedFromReport,
                    ExclusionReason = match.ExclusionReason,
                    AnalystNote = match.AnalystNote
                });
            }
            else
            {
                // Separate observation: allocate new unique CaseRowId
                reconciled.Add(inc with
                {
                    CaseRowId = $"C-{nextIndex++}",
                    IsExcludedFromReport = false,
                    ExclusionReason = null,
                    AnalystNote = null
                });
            }
        }

        // Check for duplicate CaseRowIds in reconciled output
        var duplicate = reconciled.GroupBy(r => r.CaseRowId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Duplicate or conflicting CaseRowId detected during reconciliation: {duplicate.Key}");

        return LegalCaseFileParser.ToInstaLegalCases(reconciled) with { NextCaseRowIdIndex = nextIndex };
    }

    private static bool IsCompleteIdentity(LegalCaseRecord r)
    {
        return !IsPlaceholder(r.Court)
            && !IsPlaceholder(r.ProceedingType ?? r.CaseType)
            && !IsPlaceholder(r.CaseNo)
            && !IsPlaceholder(r.CaseYear)
            && !IsPlaceholder(r.Petitioner)
            && !IsPlaceholder(r.Respondent);
    }

    private static bool IsExactTupleMatch(LegalCaseRecord left, LegalCaseRecord right)
    {
        return NormalizeText(left.Court) == NormalizeText(right.Court)
            && NormalizeText(left.ProceedingType ?? left.CaseType) == NormalizeText(right.ProceedingType ?? right.CaseType)
            && NormalizeText(left.CaseNo) == NormalizeText(right.CaseNo)
            && NormalizeText(left.CaseYear) == NormalizeText(right.CaseYear)
            && NormalizeText(left.Petitioner) == NormalizeText(right.Petitioner)
            && NormalizeText(left.Respondent) == NormalizeText(right.Respondent);
    }

    private static bool AreCompatibleProceedingTypes(string? left, string? right)
    {
        var normLeft = LitigationCaseIdentity.NormaliseProceedingType(left);
        var normRight = LitigationCaseIdentity.NormaliseProceedingType(right);
        return normLeft is not null && normRight is not null && string.Equals(normLeft, normRight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        return trimmed is "-" or "." or "NA" or "NIL" or "NULL" or "DUMMY" or "TEST" or "Not Found";
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Whitespace().Replace(value.Trim(), " ").ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

