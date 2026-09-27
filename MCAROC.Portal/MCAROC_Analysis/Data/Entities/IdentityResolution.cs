namespace MCAROC_Analysis.Data.Entities;

/// <summary>Audit record of one name → CIN/LLPIN resolution (issue #294, plan §5A.2 step 5): what was asked, what
/// the resolver saw and decided, under which algorithm version and thresholds, and against which master
/// snapshot — enough to reproduce or dispute the decision later, like <c>RuleEngineVersion</c> does for
/// analysis. A request's identifier is only ever written together with one of these rows, in one transaction.</summary>
public sealed class IdentityResolution
{
    public long IdentityResolutionId { get; set; }

    /// <summary>The request this resolution was for; null for a resolution recorded before a request existed.</summary>
    public long? RequestId { get; set; }
    public McaRequest? Request { get; set; }

    public string? InputName { get; set; }
    public string? InputIdentifier { get; set; }
    /// <summary>Hints as supplied (state, district, PIN, incorporation year, listed, entity type, PAN).</summary>
    public string? HintsJson { get; set; }
    public string? NormalizedInput { get; set; }

    public ResolutionStatus Status { get; set; }
    /// <summary>Set only when an identifier was chosen.</summary>
    public ResolutionMethod? Method { get; set; }
    public string? ChosenIdentifier { get; set; }
    /// <summary>The resolver's single clear recommendation, when it had one (NeedsConfirmation).</summary>
    public string? RecommendedIdentifier { get; set; }
    /// <summary>Whether the decision met every auto-select condition — recorded even while auto-select is disabled,
    /// so live metrics can show what enabling it would have done.</summary>
    public bool AutoSelectEligible { get; set; }
    public double? TopScore { get; set; }
    public double? Margin { get; set; }
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>Top-N ranked candidates with per-feature scores and reasons.</summary>
    public string CandidatesJson { get; set; } = "[]";
    public int AlgorithmVersion { get; set; }
    public int NormalizerVersion { get; set; }
    /// <summary>The <c>ResolverOptions</c> (thresholds, flags) in force when the decision was made.</summary>
    public string OptionsJson { get; set; } = "{}";
    /// <summary>Published date of the latest completed master sync; null when the master only came from a bulk import.</summary>
    public DateOnly? MasterSnapshotDate { get; set; }

    /// <summary>Whether the chosen identifier was written to the request.</summary>
    public bool AppliedToRequest { get; set; }
    /// <summary>When applying collided with the client's existing request for the same company (DUPLICATE_REQUEST).</summary>
    public long? ExistingRequestId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}
