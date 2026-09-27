using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #295: the spend trust ladder and the live identity metrics, table-tested.</summary>
public sealed class IdentityTrustAndMetricsTests
{
    private static IdentityFacts Applied(ResolutionMethod? method, double? score) =>
        new(1, ResolutionStatus.Resolved, method, "RESOLVED", true, "U45203OR1995PLC003982", null, score, null);

    [Fact]
    public void A_request_identified_at_intake_is_trusted()
    {
        var trust = IdentityTrust.Evaluate(null, 0.99);
        Assert.True(trust.Trusted);
        Assert.Equal("IDENTIFIED_AT_INTAKE", trust.ReasonCode);
    }

    [Theory]
    [InlineData(ResolutionMethod.UserProvidedCin, null, true)]
    [InlineData(ResolutionMethod.HumanSelected, 0.4, true)]
    [InlineData(ResolutionMethod.AutoSelected, 1.0, true)]
    [InlineData(ResolutionMethod.AutoSelected, 0.99, true)]
    [InlineData(ResolutionMethod.AutoSelected, 0.9899, false)]
    [InlineData(ResolutionMethod.AutoSelected, null, false)]
    [InlineData(null, 1.0, false)]
    public void Only_a_named_or_high_confidence_identity_may_spend_unattended(ResolutionMethod? method, double? score, bool trusted)
    {
        var trust = IdentityTrust.Evaluate(Applied(method, score), 0.99);
        Assert.Equal(trusted, trust.Trusted);
        if (!trusted) Assert.Equal(IdentityTrust.BelowSpendThreshold, trust.ReasonCode);
    }

    private static ResolutionMetricRow Row(long request, ResolutionMethod? method, string code, string? input = null,
        string? chosen = null, string? recommended = null, bool eligible = false) =>
        new(request, method, code, input, chosen, recommended, eligible);

    [Fact]
    public void Live_metrics_count_auto_selections_overrides_and_false_accepts()
    {
        var rows = new[]
        {
            // 1: auto-selected A, the preview refuted it, a person picked B — a false accept, not an override.
            Row(1, ResolutionMethod.AutoSelected, "RESOLVED_AUTO_SELECTED", chosen: "A", eligible: true),
            Row(1, null, ResolutionReasonCodes.FalseAccept, input: "A"),
            Row(1, ResolutionMethod.HumanSelected, "RESOLVED_HUMAN_SELECTED", input: "B", chosen: "B"),
            // 2: recommended C, a person confirmed C.
            Row(2, null, "AUTO_SELECT_DISABLED", recommended: "C", eligible: true),
            Row(2, ResolutionMethod.HumanSelected, "RESOLVED_HUMAN_SELECTED", input: "C", chosen: "C"),
            // 3: recommended D, a person picked E — an override.
            Row(3, null, "IDENTITY_NEEDS_CONFIRMATION", recommended: "D"),
            Row(3, ResolutionMethod.HumanSelected, "RESOLVED_HUMAN_SELECTED", input: "E", chosen: "E"),
            // 4: ambiguous, a person picked F — no suggestion to override.
            Row(4, null, "IDENTITY_AMBIGUOUS"),
            Row(4, ResolutionMethod.HumanSelected, "RESOLVED_HUMAN_SELECTED", input: "F", chosen: "F"),
            // 5: the requester supplied the CIN — not a name decision.
            Row(5, ResolutionMethod.UserProvidedCin, "RESOLVED_USER_PROVIDED_CIN", input: "G", chosen: "G"),
        };

        var m = IdentityResolutionMetrics.Compute(rows);

        Assert.Equal(4, m.NameDecisions);
        Assert.Equal(1, m.AutoSelected);
        Assert.Equal(2, m.AutoSelectEligible);
        Assert.Equal(0.25, m.AutoSelectRate);
        Assert.Equal(4, m.HumanSelections);
        Assert.Equal(2, m.HumanSelectionsAfterSuggestion);
        Assert.Equal(1, m.HumanOverrides);
        Assert.Equal(0.5, m.HumanOverrideRate);
        Assert.Equal(1, m.FalseAccepts);

        var text = IdentityResolutionMetrics.Format(m);
        Assert.Contains("| False accepts (refuted by the reference tool) | 1 |", text);
        Assert.Contains("25.0%", text);
    }

    [Fact]
    public void No_resolutions_means_no_rates()
    {
        var m = IdentityResolutionMetrics.Compute([]);
        Assert.Null(m.AutoSelectRate);
        Assert.Null(m.HumanOverrideRate);
        Assert.Contains("n/a", IdentityResolutionMetrics.Format(m));
    }
}
