namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Rule-engine-owned interpretation of Phase 1's free-text CompanyProfile.CompanyStatus — this is
/// Phase 3 reading Phase 1's raw text, so it lives here rather than as a Phase 1 schema change (Phase 3
/// doesn't edit Phase 1 entity files while both PRs are unmerged).</summary>
public enum NormalizedCompanyStatus
{
    Active,
    Dormant,
    StruckOff,
    UnderLiquidation,
    Liquidated,
    UnderCirp,
    Amalgamated,
    Unknown
}

/// <summary>Explicit keyword lookup, not raw substring matching alone — handles real MCA-data variants
/// (Strike Off / Struck Off, Under Liquidation / Liquidated, "Dormant under section...", "Active In
/// Progress"). Ordered most-specific-adverse-state first so a combined phrase resolves to the more serious
/// status. Unmapped/unrecognized text returns Unknown, which rules treat as NotEvaluated, never an
/// implicit negative.</summary>
public static class CompanyStatusNormalizer
{
    private static readonly (string[] Keywords, NormalizedCompanyStatus Status)[] Rules =
    [
        (["struck off", "strike off"], NormalizedCompanyStatus.StruckOff),
        (["under liquidation"], NormalizedCompanyStatus.UnderLiquidation),
        (["liquidated"], NormalizedCompanyStatus.Liquidated),
        (["under cirp", "corporate insolvency resolution"], NormalizedCompanyStatus.UnderCirp),
        (["amalgamated"], NormalizedCompanyStatus.Amalgamated),
        (["dormant"], NormalizedCompanyStatus.Dormant),
        (["active"], NormalizedCompanyStatus.Active)
    ];

    public static NormalizedCompanyStatus Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return NormalizedCompanyStatus.Unknown;
        var text = raw.Trim().ToLowerInvariant();
        foreach (var (keywords, status) in Rules)
            if (keywords.Any(k => text.Contains(k, StringComparison.Ordinal)))
                return status;
        return NormalizedCompanyStatus.Unknown;
    }
}
