namespace MCAROC_Analysis.Data.Entities;

/// <summary>One MCA filing (one nested zip, named "{SRN}_{COMPANY}_{CIN}.zip") — the SRN-level parent
/// between the batch and its documents. A filing's real information is often spread across a form and its
/// attachments (e.g. CHG-1 + instrument + satisfaction letter describe one charge event), so Gemini
/// extraction runs per filing, not per document.</summary>
public class McaFiling
{
    public long FilingId { get; set; }
    public long BatchId { get; set; }
    public McaFilingBatch? Batch { get; set; }
    public long RequestId { get; set; }

    public string Srn { get; set; } = string.Empty;
    public string? ParsedCompanyName { get; set; }
    public string? ParsedCin { get; set; }
    public bool IdentityMatchesRequest { get; set; } = true;

    /// <summary>Raw top-level zip folder name (e.g. "Charge Documents Financial Documets") — primary
    /// classification signal for documents whose filename/text don't resolve a category on their own.</summary>
    public string OuterCategoryFolder { get; set; } = string.Empty;
    public string NestedZipName { get; set; } = string.Empty;

    public bool ManualReviewRequired { get; set; }
    public string? ManualReviewReason { get; set; }

    public List<McaFilingDocument> Documents { get; set; } = [];
}
