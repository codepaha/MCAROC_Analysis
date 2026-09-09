namespace MCAROC_Analysis.Data.Entities;

/// <summary>One row per ROC charge ID, summarizing its current state. Detail history lives in ROCChargeEvent.</summary>
public class RocCharge : ExtractedEntityBase
{
    public long ChargeId { get; set; }

    /// <summary>The source "CHARGE ID" value, e.g. "101265730" — the natural key events are grouped by.</summary>
    public string RocChargeNumber { get; set; } = string.Empty;

    public string LatestChargeHolderRaw { get; set; } = string.Empty;
    public string LatestChargeHolderNormalized { get; set; } = string.Empty;

    public DateOnly? CreationDate { get; set; }
    public decimal? CurrentAmount { get; set; }
    public string? CurrentAmountRaw { get; set; }

    public string ChargeStatus { get; set; } = string.Empty;

    public DateOnly? LatestModificationDate { get; set; }
    public DateOnly? SatisfactionDate { get; set; }

    // Phase 6 — rollup from the latest *classification-bearing* event (not the latest chronological
    // event: a Satisfaction event with no security narrative must not null these out). Lets the Open
    // Charges grid render without re-classifying on read.
    public FacilityType? LatestPrimaryFacilityType { get; set; }
    public ChargeArrangement? LatestArrangement { get; set; }
    public string? LatestSecurityTypesJson { get; set; }
    public ChargeClassificationConfidence? LatestSecurityConfidence { get; set; }

    public List<RocChargeEvent> Events { get; set; } = [];
}
