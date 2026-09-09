namespace MCAROC_Analysis.Data.Entities;

/// <summary>One normalized (asset, ranking) pair derived by ChargeSecurityClassifier from a single
/// RocChargeEvent's security narrative. An event can have several — e.g. "pari-passu charge over
/// current assets AND first charge over movable fixed assets" → two components. The raw phrase each
/// was derived from is always kept in AssetDescriptionRaw.</summary>
public class ChargeSecurityComponent
{
    public long ChargeSecurityComponentId { get; set; }

    public long RocChargeEventId { get; set; }
    public RocChargeEvent? RocChargeEvent { get; set; }

    public long RequestId { get; set; }
    public long IngestionRunId { get; set; }

    public SecurityType SecurityType { get; set; }
    public ChargeRanking Ranking { get; set; } = ChargeRanking.Unknown;

    public string? AssetDescriptionRaw { get; set; }

    /// <summary>true only when the source explicitly says "primary security"; false only when explicitly
    /// "additional / collateral security"; null when not stated (never inferred from phrase order).</summary>
    public bool? IsPrimarySecurity { get; set; }

    public ChargeClassificationConfidence Confidence { get; set; } = ChargeClassificationConfidence.None;
    public string? MatchedRule { get; set; }
}
