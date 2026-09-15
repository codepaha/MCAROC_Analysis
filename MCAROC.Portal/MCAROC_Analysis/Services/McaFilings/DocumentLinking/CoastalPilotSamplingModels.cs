using System.Text.Json;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>Which domain (D2 charge-link or D3 financial-link) a Deliverable 5 sample record came from.</summary>
public enum SampleDomain
{
    Charge,
    Financial
}

/// <summary>A reviewer's adjudication of one sampled document — starts <see cref="Pending"/> and is
/// filled in by a human before any production backfill.</summary>
public enum ReviewerDecision
{
    Pending,
    Confirm,
    Reject,
    Reclassify
}

/// <summary>
/// Deliverable 5: one document selected into the reviewer sample, with a full evidence contract —
/// identity (<see cref="Sha256Hex"/>, the two path fields), the inferred classification from its
/// domain pass, an evidence snippet a reviewer can check without re-running any code, and a
/// decision field for the human adjudication this whole deliverable exists to collect.
/// </summary>
public sealed record CoastalPilotSampleRecord
{
    public required string Sha256Hex { get; init; }
    public required string OuterEntryFullPath { get; init; }
    public required string NestedEntryRelativePath { get; init; }
    public required SampleDomain Domain { get; init; }

    /// <summary>The stratum this record was drawn from, e.g. "Charge:ExactMatchEventDate" or
    /// "Financial:Standalone" — informational, so a reviewer (or a re-run of the sampler) can see why
    /// this particular document was picked rather than another one in the same bucket.</summary>
    public required string StratumKey { get; init; }

    public required string InferredOutcome { get; init; }
    public required string InferredReason { get; init; }

    /// <summary>PDF text quote (financial evidence) or a plain-language rendering of the charge
    /// evidence's filing/event dates and classification — always present, never requires parsing
    /// <see cref="EvidenceJson"/> to understand at a glance.</summary>
    public required string EvidenceSnippet { get; init; }

    /// <summary>Set only when the evidence is a PDF text quote (financial AutoAccepted/PendingReview
    /// entries) — the page the quote came from.</summary>
    public int? EvidencePageNumber { get; init; }

    /// <summary>Set only when the evidence is a workbook target (financial entries) — the exact
    /// sheet/row/column the financial statement parser reported the value at.</summary>
    public string? EvidenceSheetName { get; init; }
    public int? EvidenceSourceRow { get; init; }
    public int? EvidenceSourceColumn { get; init; }

    /// <summary>The full raw evidence payload from D2/D3, preserved verbatim for complete
    /// traceability beyond the human-readable <see cref="EvidenceSnippet"/>.</summary>
    public required string EvidenceJson { get; init; }

    public ReviewerDecision ReviewerDecision { get; init; } = ReviewerDecision.Pending;
}

/// <summary>Result of Deliverable 5's sampling pass — the auditable package a human reviewer
/// adjudicates before any production backfill.</summary>
public sealed record CoastalPilotSamplingResult(IReadOnlyList<CoastalPilotSampleRecord> Records)
{
    public int TotalRecords => Records.Count;
    public int PendingReviewCount => Records.Count(r => r.InferredOutcome == "PendingReview");
    public int AutoAcceptedCount => Records.Count(r => r.InferredOutcome == "AutoAccepted");
    public int UnlinkedCount => Records.Count(r => r.InferredOutcome is "UnlinkedNoCandidate" or "UnlinkedOutOfScope");

    /// <summary>The committed-fixture JSON shape — a flat array, matching the D2/D3/D4 fixture
    /// convention exactly.</summary>
    public string ToJson() => JsonSerializer.Serialize(Records, new JsonSerializerOptions { WriteIndented = true });
}
