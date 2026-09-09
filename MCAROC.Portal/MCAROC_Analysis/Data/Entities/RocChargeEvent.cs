namespace MCAROC_Analysis.Data.Entities;

/// <summary>One lifecycle event (creation/modification/satisfaction) for a charge.
/// Always populated from the ROC report's Sequence sheets; enriched with the charge report's
/// "...in Details" sheet fields when available and identity-matched.</summary>
public class RocChargeEvent : ExtractedEntityBase
{
    public long ChargeEventId { get; set; }

    public long RocChargeId { get; set; }
    public RocCharge? RocCharge { get; set; }

    /// <summary>Raw source serial number text, e.g. "2.3" — presentation artifact, not a reliable join key on its own.</summary>
    public string SerialNumber { get; set; } = string.Empty;

    public ChargeEventType EventType { get; set; }
    public DateOnly? EventDate { get; set; }
    public DateOnly? FilingDate { get; set; }

    public decimal? ChargeAmount { get; set; }
    public string? ChargeAmountRaw { get; set; }

    public string HolderNameRaw { get; set; } = string.Empty;
    public string HolderNameNormalized { get; set; } = string.Empty;

    public string? PropertyType { get; set; }
    public int? NumberOfHolders { get; set; }

    // Enrichment fields — only populated when a matching charge-report "...in Details" row was found.
    // Kept as full free text, never destructively normalized (future NLP/property extraction needs the original).
    public string? InstrumentDescription { get; set; }
    public string? RateOfInterest { get; set; }
    public string? TermsOfPayment { get; set; }
    public string? PropertyParticulars { get; set; }
    public string? ExtentAndOperation { get; set; }
    public string? OtherTerms { get; set; }
    public string? ModificationParticulars { get; set; }
    public bool? JointHolding { get; set; }
    public bool? ConsortiumHolding { get; set; }

    public ChargeEventMatchConfidence MatchConfidence { get; set; } = ChargeEventMatchConfidence.Unmatched;
    public string? MatchMethod { get; set; }
}
