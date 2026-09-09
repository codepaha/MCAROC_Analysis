using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>Backs the paged Documents panel on the company page (<c>GET /Requests/{id}/Documents</c>).
/// One "row" is a filing (SRN group); its documents and Gemini extraction render nested underneath.
/// <see cref="Category"/> and <see cref="Page"/> stay in the query string so the panel is bookmarkable.</summary>
public class DocumentsPageViewModel
{
    public long RequestId { get; set; }

    /// <summary>Selected <see cref="FilingCategory"/> name, or null for "All". "Other" maps to <see cref="FilingCategory.Unclassified"/>.</summary>
    public string? Category { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public int TotalFilings { get; set; }
    public int TotalPages => TotalFilings == 0 ? 1 : (int)Math.Ceiling(TotalFilings / (double)PageSize);

    public bool HasBatch { get; set; }

    /// <summary>Filing count per dominant category (all categories, ignores the current filter) — drives the filter chips.</summary>
    public Dictionary<FilingCategory, int> CategoryCounts { get; set; } = [];

    public List<FilingRow> Filings { get; set; } = [];

    public record FilingRow(McaFiling Filing, FilingCategory DominantCategory, McaFilingExtraction? Extraction);
}
