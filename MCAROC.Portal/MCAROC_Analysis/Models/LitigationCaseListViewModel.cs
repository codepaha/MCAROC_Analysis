using Microsoft.AspNetCore.WebUtilities;

namespace MCAROC_Analysis.Models;

public sealed class LitigationCaseListViewModel
{
    public long RequestId { get; set; }
    public long? SnapshotId { get; set; }
    public string? Generation { get; set; }
    public string? Search { get; set; }
    public string? Court { get; set; }
    public string? Status { get; set; }
    public string? Outcome { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int Total { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)Total / PageSize));
    public int First => Total == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int Last => Math.Min(Page * PageSize, Total);
    public List<LitigationCaseCardViewModel> Cases { get; set; } = [];

    public string PageUrl(int page) => QueryHelpers.AddQueryString($"/Requests/{RequestId}", new Dictionary<string, string?>
    {
        ["q"] = Search, ["court"] = Court, ["status"] = Status, ["outcome"] = Outcome,
        ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["pageSize"] = PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
    }) + "#tab-litigation";
}
