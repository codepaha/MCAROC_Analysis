namespace MCAROC_Analysis.Models;

public class DocumentViewerViewModel
{
    public long RequestId { get; set; }
    public long DocumentId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? FormType { get; set; }
    public int PageCount { get; set; }
    public string? Srn { get; set; }
    public string RawPdfUrl { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}
