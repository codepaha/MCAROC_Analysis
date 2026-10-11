using MCAROC_Analysis.Services.LitigationModule.Normalisation;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Comprehensive parsed details of a consumer case from e-Jagriti (plan section 4.9).
/// Encompasses all 24 core metadata fields, hearing history, normalized case identity,
/// derived state/district from the commission registry, and derived decision date.</summary>
public sealed record ConsumerCaseDetails(
    string CaseNumber,
    CaseNumberParts NormalizedCaseNumber,
    long? FilingReferenceNumber,
    DateOnly? CaseFilingDate,
    string? FiledInCommissionName,
    string? Complainant,
    string? Respondent,
    string? CaseStage,
    DateOnly? DateOfNextHearing,
    string? ComplainantAdvocate,
    string? RespondentAdvocate,
    IReadOnlyList<string> ApplicationsFiled,
    IReadOnlyList<string> AttachedLowerCourt,
    IReadOnlyList<ConsumerHearing> Hearings,
    int? CommissionTypeId,
    int? CommissionId,
    string? CommissionName,
    string? StateName,
    string? DistrictName,
    bool IsBench,
    CaseStatusParts? StatusParts,
    DateOnly? DerivedDecisionDate,
    bool IsDecisionDateDerived,
    bool IsTerminal,
    string? RawResponse = null,
    string? ExitAddress = null)
{
    private static readonly string[] TerminalWords =
    [
        "DISPOS",
        "DISMISS",
        "DECIDED",
        "ALLOWED",
        "WITHDRAWN",
        "SETTLED",
        "ABATED"
    ];

    /// <summary>Checks whether a case stage represents a terminal / disposed outcome.</summary>
    public static bool IsTerminalStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return false;
        var upper = stage.ToUpperInvariant();
        return TerminalWords.Any(w => upper.Contains(w));
    }

    /// <summary>Derives a candidate decision date from the case hearings when the matter is terminal.</summary>
    public static (DateOnly? Date, bool IsDerived) DeriveDecisionDate(string? stage, IReadOnlyList<ConsumerHearing> hearings)
    {
        if (!IsTerminalStage(stage) || hearings.Count == 0)
        {
            return (null, false);
        }

        // Search backward from the latest hearing for a valid hearing date
        for (var i = hearings.Count - 1; i >= 0; i--)
        {
            var h = hearings[i];
            if (h.DateOfHearing.HasValue)
            {
                return (h.DateOfHearing.Value, true);
            }
            if (h.DateOfNextHearing.HasValue)
            {
                return (h.DateOfNextHearing.Value, true);
            }
        }

        return (null, false);
    }
}

