namespace MCAROC_Analysis.Data.Entities;

/// <summary>One rating action, sourced from either the "Credit Ratings" sheet (accepted) or the
/// "Unaccepted Ratings" sheet (the issuer did not accept/participate — <see cref="IsAccepted"/> = false).
/// One table for both; the source sheet is the only thing that distinguishes them.</summary>
public class CreditRating : ExtractedEntityBase
{
    public long CreditRatingId { get; set; }

    public string Agency { get; set; } = string.Empty;
    /// <summary>The rating-action date for accepted ratings, or the "Date Of Non-Acceptance" for
    /// unaccepted ones — whichever the source sheet carries.</summary>
    public DateOnly? RatingDate { get; set; }
    public string? Instrument { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Rating { get; set; }
    /// <summary>Assigned / Reaffirmed / Upgraded / Downgraded / Withdrawn / ... — accepted ratings only;
    /// null for unaccepted rows (the sheet carries no action column).</summary>
    public string? Action { get; set; }
    public string? Outlook { get; set; }
    public string? Remarks { get; set; }

    public bool IsAccepted { get; set; }
}
