namespace MCAROC_Analysis.Data.Entities;

/// <summary>A single-use, company-bound, expiring human approval to spend the 1-credit unlock
/// (docs/pipeline-automation-plan.md §5.7, step 3–4). Consumed exactly once, inside the same transaction as
/// the <see cref="PaidCallAdmission"/> it authorizes: <c>UPDATE UnlockApprovals SET ConsumedAdmissionId=@a
/// WHERE ApprovalId=@x AND ConsumedAdmissionId IS NULL AND ExpiresUtc &gt; @now</c> must change exactly one
/// row, else the whole spend rolls back — two reconcilers can never both spend on the same approval. Scope is
/// company-level (<see cref="Identifier"/>), so one approval unlocks for every request currently waiting on
/// that company, not just the one that raised the alert.</summary>
public sealed class UnlockApproval
{
    public long UnlockApprovalId { get; set; }
    public string Identifier { get; set; } = string.Empty;
    /// <summary>The request whose alert prompted this approval — provenance only; the grant itself applies
    /// to every request waiting on <see cref="Identifier"/>, per the scope note above.</summary>
    public long RequestId { get; set; }

    public string ApprovedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime ApprovedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }

    /// <summary>Set exactly once, inside the admission transaction — null means still available to spend
    /// (and not yet expired); non-null means already consumed, by whichever admission id is here.</summary>
    public long? ConsumedAdmissionId { get; set; }
}
