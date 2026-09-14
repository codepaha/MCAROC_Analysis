using System.Text.Json;
using System.Text.Json.Serialization;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Deterministically validates a raw Vertex AI response against the exact tag set sent in the
/// prompt — mirrors AiCrossSectionAnalysisService.Validate's mechanics: a malformed whole response is
/// rejected outright, but a single bad candidate is dropped, never the whole run. This is what makes "AI
/// never introduces a number/reference it wasn't given" a database-checkable property rather than a
/// prompt-only hope.</summary>
public static class CalculationAiAuditValidator
{
    public const string UnknownLedgerTag = "UnknownLedgerTag";
    public const string UnresolvableRelatedTag = "UnresolvableRelatedTag";
    public const string UnsupportedActualValue = "UnsupportedActualValue";

    public record ValidatedCandidate(
        string LedgerTag, long LedgerEntryId, string ClaimType, decimal? ExpectedValue, decimal? ActualValue,
        string Explanation, IReadOnlyList<long> RelatedLedgerEntryIds, CalculationDiscrepancySeverity? SuggestedSeverity);

    public record RejectedCandidate(
        [property: JsonPropertyName("ledgerTag")] string? LedgerTag,
        [property: JsonPropertyName("claimType")] string? ClaimType,
        [property: JsonPropertyName("expectedValue")] decimal? ExpectedValue,
        [property: JsonPropertyName("actualValue")] decimal? ActualValue,
        [property: JsonPropertyName("explanation")] string? Explanation,
        [property: JsonPropertyName("relatedLedgerTags")] IReadOnlyList<string>? RelatedLedgerTags,
        [property: JsonPropertyName("suggestedSeverity")] string? SuggestedSeverity,
        [property: JsonPropertyName("rejectReason")] string RejectReason);

    public record ValidationResult(
        bool ResponseWasValid, string? ResponseRejectReason,
        IReadOnlyList<ValidatedCandidate> Accepted, IReadOnlyList<RejectedCandidate> Rejected, bool NoIssuesFound);

    public static ValidationResult Validate(string rawResponse, IReadOnlyDictionary<string, CalculationLedgerEntry> tagMap)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new ValidationResult(false, "Empty model response.", [], [], false);

        AiResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AiResponseDto>(rawResponse, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new ValidationResult(false, $"Model did not return valid JSON: {ex.Message}", [], [], false);
        }
        if (dto is null)
            return new ValidationResult(false, "Model response parsed to null.", [], [], false);

        var accepted = new List<ValidatedCandidate>();
        var rejected = new List<RejectedCandidate>();

        foreach (var c in dto.Candidates ?? [])
        {
            if (c.LedgerTag is null || !tagMap.TryGetValue(c.LedgerTag, out var primaryEntry))
            {
                rejected.Add(ToRejected(c, UnknownLedgerTag));
                continue;
            }

            var relatedTags = (c.RelatedLedgerTags is { Count: > 0 } rt ? rt : [c.LedgerTag]).Distinct().ToList();
            var relatedIds = new List<long>();
            var allResolved = true;
            foreach (var tag in relatedTags)
            {
                if (!tagMap.TryGetValue(tag, out var relatedEntry)) { allResolved = false; break; }
                relatedIds.Add(relatedEntry.CalculationLedgerEntryId);
            }
            if (!allResolved)
            {
                rejected.Add(ToRejected(c, UnresolvableRelatedTag));
                continue;
            }

            // Proves the model read the real number rather than hallucinating one — expectedValue is
            // deliberately never checked against the ledger, since it's the model's claim about what the
            // value should be instead, which is the whole point of a candidate.
            if (!ActualValueMatches(c.ActualValue, primaryEntry.ValueNumeric))
            {
                rejected.Add(ToRejected(c, UnsupportedActualValue));
                continue;
            }

            // An unparseable severity or claim type never sinks an otherwise-valid candidate — it just
            // persists with a null suggestion / a generic label. Every candidate always survives as
            // Status=Open, Severity=null regardless (enforced by the caller, never here) — AI never
            // imposes a hold directly.
            var severity = Enum.TryParse<CalculationDiscrepancySeverity>(c.SuggestedSeverity, ignoreCase: true, out var parsedSeverity)
                ? parsedSeverity
                : (CalculationDiscrepancySeverity?)null;
            var claimType = string.IsNullOrWhiteSpace(c.ClaimType) ? "Other" : c.ClaimType;

            accepted.Add(new ValidatedCandidate(
                c.LedgerTag, primaryEntry.CalculationLedgerEntryId, claimType, c.ExpectedValue, c.ActualValue,
                c.Explanation ?? "", relatedIds, severity));
        }

        return new ValidationResult(true, null, accepted, rejected, dto.NoIssuesFound);
    }

    /// <summary>A row with no stored numeric value (text-only or NotEvaluated) supports no numeric
    /// "actualValue" claim at all. Deliberately exact decimal equality, NOT the 0/1-decimal rounding
    /// tolerance AiCrossSectionAnalysisService.BuildAllowedNumbers uses — that tolerance exists there for a
    /// genuinely different problem (free-form prose paraphrasing a number, e.g. writing "25%" for a stored
    /// 24.97). Here "actualValue" is supposed to be a verbatim echo of the ledger row's own stored value,
    /// which is the whole proof the model actually read it rather than guessing a plausible nearby number —
    /// a rounding tolerance would let a hallucinated-but-close value (e.g. 99.96 against a stored 100.04,
    /// both rounding to "100.0") pass as if it were a real read. `decimal == decimal` compares by numeric
    /// value regardless of trailing-zero scale (100m == 100.0000m), so exact equality is also safe against
    /// a client that omits trailing zeros the DB's decimal(18,4) column stores.</summary>
    private static bool ActualValueMatches(decimal? claimedActual, decimal? ledgerValue)
    {
        if (claimedActual is null) return true; // no numeric claim made — nothing to contradict
        if (ledgerValue is null) return false;
        return claimedActual.Value == ledgerValue.Value;
    }

    private static RejectedCandidate ToRejected(CandidateDto c, string reason) =>
        new(c.LedgerTag, c.ClaimType, c.ExpectedValue, c.ActualValue, c.Explanation, c.RelatedLedgerTags, c.SuggestedSeverity, reason);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private record AiResponseDto(
        [property: JsonPropertyName("candidates")] List<CandidateDto>? Candidates,
        [property: JsonPropertyName("noIssuesFound")] bool NoIssuesFound);

    private record CandidateDto(
        [property: JsonPropertyName("ledgerTag")] string? LedgerTag,
        [property: JsonPropertyName("claimType")] string? ClaimType,
        [property: JsonPropertyName("expectedValue")] decimal? ExpectedValue,
        [property: JsonPropertyName("actualValue")] decimal? ActualValue,
        [property: JsonPropertyName("explanation")] string? Explanation,
        [property: JsonPropertyName("relatedLedgerTags")] List<string>? RelatedLedgerTags,
        [property: JsonPropertyName("suggestedSeverity")] string? SuggestedSeverity);
}
