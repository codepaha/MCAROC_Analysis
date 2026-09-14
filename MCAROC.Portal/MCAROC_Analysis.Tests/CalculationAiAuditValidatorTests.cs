using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;

namespace MCAROC_Analysis.Tests;

/// <summary>Tests CalculationAiAuditValidator directly against constructed response JSON — same boundary
/// as AiCrossSectionAnalysisServiceValidationTests (no live-call testing; the AI service's constructor
/// eagerly loads Google Cloud credentials from disk).</summary>
public class CalculationAiAuditValidatorTests
{
    private static CalculationLedgerEntry Entry(long id, decimal? value = null, string? valueText = null, string? insufficiencyReason = null) =>
        new()
        {
            CalculationLedgerEntryId = id, CalculationAuditSnapshotId = 1, CalculationKey = $"Test.Key{id}",
            MetricLabel = "Test metric", Period = "FY2025", ValueNumeric = value, ValueText = valueText,
            InsufficiencyReason = insufficiencyReason, InputsJson = "[]"
        };

    private static Dictionary<string, CalculationLedgerEntry> TagMap(params CalculationLedgerEntry[] entries)
    {
        var map = new Dictionary<string, CalculationLedgerEntry>();
        for (var i = 0; i < entries.Length; i++) map[$"L{i + 1}"] = entries[i];
        return map;
    }

    [Fact]
    public void EmptyResponse_RejectsWholeRun()
    {
        var result = CalculationAiAuditValidator.Validate("", TagMap(Entry(1, 100m)));

        Assert.False(result.ResponseWasValid);
        Assert.Empty(result.Accepted);
    }

    [Fact]
    public void NonJsonResponse_RejectsWholeRun()
    {
        var result = CalculationAiAuditValidator.Validate("not json at all", TagMap(Entry(1, 100m)));

        Assert.False(result.ResponseWasValid);
        Assert.Empty(result.Accepted);
    }

    [Fact]
    public void ValidCandidate_MatchingActualValue_IsAccepted_WithNullSeverity()
    {
        var tagMap = TagMap(Entry(1, 100m));
        var response = """
            { "candidates": [{ "ledgerTag": "L1", "claimType": "ArithmeticMismatch", "expectedValue": 90,
              "actualValue": 100, "explanation": "L1 should be 90 based on the prior period.",
              "relatedLedgerTags": ["L1"], "suggestedSeverity": "Material" }], "noIssuesFound": false }
            """;

        var result = CalculationAiAuditValidator.Validate(response, tagMap);

        Assert.True(result.ResponseWasValid);
        var candidate = Assert.Single(result.Accepted);
        Assert.Equal(1, candidate.LedgerEntryId);
        Assert.Equal(90m, candidate.ExpectedValue);
        Assert.Equal(100m, candidate.ActualValue);
        // suggestedSeverity is recoverable from the accepted candidate for the caller's ClaimSummary text,
        // but must never itself become CalculationDiscrepancy.Severity — that stays a human-only decision.
        Assert.Equal(CalculationDiscrepancySeverity.Material, candidate.SuggestedSeverity);
    }

    [Fact]
    public void FabricatedTag_IsDroppedNotWholeRunFailed()
    {
        var tagMap = TagMap(Entry(1, 100m));
        var response = """
            { "candidates": [{ "ledgerTag": "L99", "claimType": "Other", "actualValue": 100,
              "explanation": "citing a tag that was never sent" }], "noIssuesFound": false }
            """;

        var result = CalculationAiAuditValidator.Validate(response, tagMap);

        Assert.True(result.ResponseWasValid);
        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(CalculationAiAuditValidator.UnknownLedgerTag, rejected.RejectReason);
    }

    [Fact]
    public void FabricatedRelatedTag_IsDropped()
    {
        var tagMap = TagMap(Entry(1, 100m));
        var response = """
            { "candidates": [{ "ledgerTag": "L1", "claimType": "Other", "actualValue": 100,
              "explanation": "x", "relatedLedgerTags": ["L1", "L99"] }], "noIssuesFound": false }
            """;

        var result = CalculationAiAuditValidator.Validate(response, tagMap);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(CalculationAiAuditValidator.UnresolvableRelatedTag, rejected.RejectReason);
    }

    [Fact]
    public void ActualValueNotMatchingLedgerRow_IsDropped()
    {
        var tagMap = TagMap(Entry(1, 100m));
        var response = """
            { "candidates": [{ "ledgerTag": "L1", "claimType": "Other", "actualValue": 55,
              "explanation": "hallucinated a different actual value" }], "noIssuesFound": false }
            """;

        var result = CalculationAiAuditValidator.Validate(response, tagMap);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(CalculationAiAuditValidator.UnsupportedActualValue, rejected.RejectReason);
    }

    [Fact]
    public void ActualValueClaimedAgainstTextOnlyRow_IsDropped()
    {
        // The ledger row has no numeric value at all (text-only) — any numeric "actualValue" claim
        // against it cannot be verified and must be dropped, never trusted at face value.
        var tagMap = TagMap(Entry(1, valueText: "Unclassified"));
        var response = """
            { "candidates": [{ "ledgerTag": "L1", "claimType": "Other", "actualValue": 42,
              "explanation": "x" }], "noIssuesFound": false }
            """;

        var result = CalculationAiAuditValidator.Validate(response, tagMap);

        Assert.Empty(result.Accepted);
        Assert.Equal(CalculationAiAuditValidator.UnsupportedActualValue, Assert.Single(result.Rejected).RejectReason);
    }

    [Fact]
    public void UnparseableSeverity_DoesNotSinkTheCandidate()
    {
        var tagMap = TagMap(Entry(1, 100m));
        var response = """
            { "candidates": [{ "ledgerTag": "L1", "claimType": "Other", "actualValue": 100,
              "explanation": "x", "suggestedSeverity": "Extreme" }], "noIssuesFound": false }
            """;

        var result = CalculationAiAuditValidator.Validate(response, tagMap);

        var candidate = Assert.Single(result.Accepted);
        Assert.Null(candidate.SuggestedSeverity);
    }

    [Fact]
    public void NoIssuesFound_WithEmptyCandidates_IsAValidCleanPass()
    {
        var tagMap = TagMap(Entry(1, 100m));
        var response = """{ "candidates": [], "noIssuesFound": true }""";

        var result = CalculationAiAuditValidator.Validate(response, tagMap);

        Assert.True(result.ResponseWasValid);
        Assert.True(result.NoIssuesFound);
        Assert.Empty(result.Accepted);
    }

    [Fact]
    public void RejectedCandidate_RetainsFullOriginalContent()
    {
        // A disputed rejection must be fully inspectable on its own without re-parsing RawResponseJson.
        var tagMap = TagMap(Entry(1, 100m));
        var response = """
            { "candidates": [{ "ledgerTag": "L99", "claimType": "ArithmeticMismatch", "expectedValue": 5,
              "actualValue": 100, "explanation": "full detail preserved", "relatedLedgerTags": ["L99"],
              "suggestedSeverity": "Critical" }], "noIssuesFound": false }
            """;

        var result = CalculationAiAuditValidator.Validate(response, tagMap);

        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("L99", rejected.LedgerTag);
        Assert.Equal("ArithmeticMismatch", rejected.ClaimType);
        Assert.Equal(5m, rejected.ExpectedValue);
        Assert.Equal(100m, rejected.ActualValue);
        Assert.Equal("full detail preserved", rejected.Explanation);
        Assert.Equal("Critical", rejected.SuggestedSeverity);
    }
}
