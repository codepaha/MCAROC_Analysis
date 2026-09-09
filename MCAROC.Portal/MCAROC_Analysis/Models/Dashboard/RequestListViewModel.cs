using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

public record RequestListRow(
    McaRequest Request,
    ReviewPriority? Priority,
    int CriticalFindingsCount,
    List<string> AttentionReasons);

public class RequestListViewModel
{
    public required RequestListFilterCriteria Filters { get; set; }
    public required List<Client> Clients { get; set; }
    public required List<RequestListRow> Rows { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)RequestListFilterCriteria.PageSize);
}
