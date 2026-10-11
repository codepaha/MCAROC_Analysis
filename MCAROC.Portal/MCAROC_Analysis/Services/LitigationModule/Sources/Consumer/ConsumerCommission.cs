namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Tier of the consumer disputes redressal commission per Consumer Protection Act.</summary>
public enum ConsumerCommissionTier
{
    /// <summary>National Consumer Disputes Redressal Commission (NCDRC).</summary>
    National = 1,

    /// <summary>State Consumer Disputes Redressal Commission (SCDRC) including circuit/regional benches.</summary>
    State = 2,

    /// <summary>District Consumer Disputes Redressal Commission (DCDRC).</summary>
    District = 3
}

/// <summary>Represents a consumer commission in the e-Jagriti hierarchy (plan section 4.9).</summary>
/// <param name="CommissionId">e-Jagriti backend commission ID (e.g. 11000000 for NCDRC, 12270497 for Thane DCDRC).</param>
/// <param name="CommissionTypeId">Commission type: 1 for NCDRC, 2 for SCDRC, 3 for DCDRC.</param>
/// <param name="Tier">Tier of the commission.</param>
/// <param name="CommissionName">Official commission display name.</param>
/// <param name="StateName">State name (or parent state for benches).</param>
/// <param name="DistrictName">District name for DCDRC commissions (or bench location name for SCDRC benches).</param>
/// <param name="IsBench">True if this is a circuit or regional bench of a State commission.</param>
/// <param name="ParentState">Parent state name for circuit/regional benches.</param>
/// <param name="Prefix">Empirically-observed case number prefix (e.g. "NC", "SC/35", "DC/497"), if known.</param>
public sealed record ConsumerCommission(
    int CommissionId,
    int CommissionTypeId,
    ConsumerCommissionTier Tier,
    string CommissionName,
    string? StateName = null,
    string? DistrictName = null,
    bool IsBench = false,
    string? ParentState = null,
    string? Prefix = null)
{
    /// <summary>Display label for the commission.</summary>
    public string Label => Tier switch
    {
        ConsumerCommissionTier.National => "NCDRC (National)",
        ConsumerCommissionTier.State when IsBench => $"SCDRC · {DistrictName ?? CommissionName} (bench of {ParentState ?? StateName})",
        ConsumerCommissionTier.State => $"SCDRC · {StateName ?? CommissionName}",
        ConsumerCommissionTier.District => $"DCDRC · {StateName} · {DistrictName ?? CommissionName}",
        _ => CommissionName
    };
}

/// <summary>State information for UI selectors and filters.</summary>
public sealed record ConsumerStateInfo(int Id, string Name, bool IsBench = false, string? ParentState = null);

/// <summary>District commission information for UI selectors and filters.</summary>
public sealed record ConsumerDistrictInfo(int Id, string Name, string StateName);

