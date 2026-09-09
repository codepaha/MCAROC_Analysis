using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Dashboard;

/// <summary>Builds the Search History (request list/search) page's view model. Same "materialize the
/// filtered window, then sort/page in memory" approach as DashboardQueryService, for the same reason
/// (Priority sort needs real enum ordinal ranking, unsafe to push to SQL on a HasConversion&lt;string&gt;
/// column) — see DashboardQueryService's class doc for the full rationale and TODO.</summary>
public class RequestListQueryService(AppDbContext db)
{
    public async Task<RequestListViewModel> SearchAsync(RequestListFilterCriteria filters, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (from, to) = filters.ResolveWindow(today);

        var clients = await db.Clients.OrderBy(c => c.ClientName).ToListAsync(ct);

        var rows = await RequestRiskDefinitions.ApplyFilters(RequestRiskDefinitions.Query(db), filters, from, to).ToListAsync(ct);

        if (filters.AttentionRequired == true)
        {
            var isAttentionRequired = RequestRiskDefinitions.IsAttentionRequired.Compile();
            rows = rows.Where(isAttentionRequired).ToList();
        }

        if (!string.IsNullOrWhiteSpace(filters.SearchText))
        {
            var search = filters.SearchText.Trim();
            rows = rows.Where(r =>
                r.Request.RequestNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Request.CompanyName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.Request.Cin is not null && r.Request.Cin.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (r.Request.Llpin is not null && r.Request.Llpin.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (r.Request.Pan is not null && r.Request.Pan.Contains(search, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        if (filters.Section is { } section || filters.Code is not null)
        {
            var matchingRunIds = rows.Where(r => r.LatestAnalysisRunId is not null).Select(r => r.LatestAnalysisRunId!.Value).ToList();
            var findingQuery = db.AnalysisFindings.Where(f => matchingRunIds.Contains(f.AnalysisRunId));
            if (filters.Section is { } s) findingQuery = findingQuery.Where(f => f.Section == s);
            if (filters.Code is { } code) findingQuery = findingQuery.Where(f => f.Code == code);
            var matchingRequestIds = (await findingQuery.Select(f => f.RequestId).Distinct().ToListAsync(ct)).ToHashSet();
            rows = rows.Where(r => matchingRequestIds.Contains(r.Request.RequestId)).ToList();
        }

        var totalCount = rows.Count;

        var sorted = filters.Sort switch
        {
            RequestListSort.CreatedDateAsc => rows.OrderBy(r => r.Request.CreatedDate).ToList(),
            RequestListSort.PriorityDesc => rows.OrderByDescending(r => r.LatestReviewPriority is { } p ? PriorityRequestRanker.PriorityRank[p] : -1)
                .ThenByDescending(r => r.Request.CreatedDate).ToList(),
            RequestListSort.CompanyNameAsc => rows.OrderBy(r => r.Request.CompanyName).ToList(),
            _ => rows.OrderByDescending(r => r.Request.CreatedDate).ToList()
        };

        var page = Math.Max(1, filters.Page);
        var pageRows = sorted.Skip((page - 1) * RequestListFilterCriteria.PageSize).Take(RequestListFilterCriteria.PageSize)
            .Select(r => new RequestListRow(r.Request, r.LatestReviewPriority, r.LatestCriticalFindingsCount, DashboardQueryService.BuildAttentionReasons(r)))
            .ToList();

        return new RequestListViewModel { Filters = filters, Clients = clients, Rows = pageRows, TotalCount = totalCount };
    }
}
