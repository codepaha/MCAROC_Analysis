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

        // Precompute compatibility between existing and incoming records
        var existingToIncoming = new Dictionary<LegalCaseRecord, List<LegalCaseRecord>>();
        var incomingToExisting = new Dictionary<LegalCaseRecord, List<LegalCaseRecord>>();

        foreach (var ext in existingCases) existingToIncoming[ext] = [];
        foreach (var inc in incoming) incomingToExisting[inc] = [];

        foreach (var ext in existingCases)
        {
            foreach (var inc in incoming)
            {
                if (AreCompatibleCases(ext, inc))
                {
                    existingToIncoming[ext].Add(inc);
                    incomingToExisting[inc].Add(ext);
                }
            }
        }

        var matchedExistingIds = new HashSet<string>();

        foreach (var inc in incoming)
        {
            var candidateExisting = incomingToExisting[inc];
            LegalCaseRecord? match = null;

            // Strictly 1-to-1: Exactly one existing candidate matches, AND that existing candidate matches ONLY this incoming record
            if (candidateExisting.Count == 1)
            {
                var candidate = candidateExisting[0];
                if (existingToIncoming[candidate].Count == 1 && !matchedExistingIds.Contains(candidate.CaseRowId))
                {
                    match = candidate;
                }
            }

            if (match is not null)
            {
                matchedExistingIds.Add(match.CaseRowId);

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

    private static bool AreCompatibleCases(LegalCaseRecord left, LegalCaseRecord right)
    {
        var leftCnr = LitigationCaseIdentity.NormaliseCnr(left.CnrNumber);
        var rightCnr = LitigationCaseIdentity.NormaliseCnr(right.CnrNumber);

        if (leftCnr != null && rightCnr != null)
        {
            return leftCnr == rightCnr &&
                   AreCompatibleProceedingTypes(left.ProceedingType ?? left.CaseType, right.ProceedingType ?? right.CaseType);
        }

        if (leftCnr == null && rightCnr == null && IsCompleteIdentity(left) && IsCompleteIdentity(right))
        {
            return IsExactTupleMatch(left, right);
        }

        return false;
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

