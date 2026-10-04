using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Completed evidence for the current report hash wins, including a previously retrieved
/// report reused by a refresh. Until that report is imported, use the latest completed snapshot.</summary>
public static class LitigationSnapshotSelection
{
    public static IOrderedQueryable<LitigationReportSnapshot> ForRequest(AppDbContext db, long requestId) =>
        db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => s.SearchJob!.RequestId == requestId && s.Status == LitigationReportSnapshotStatus.Completed)
            .OrderByDescending(s => !string.IsNullOrWhiteSpace(s.SearchJob!.RawResponseHash)
                && s.ReportHash == s.SearchJob.RawResponseHash)
            .ThenByDescending(s => s.RetrievedUtc)
            .ThenByDescending(s => s.LitigationReportSnapshotId);
}
