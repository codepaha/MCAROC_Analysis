using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

/// <summary>The request-level live-data version (plan section 4.2a). Anything that changes what a request's litigation views show - a live publication to its cases, a discovery
/// insert, a merge or un-merge, a hide or un-hide, a new order or hearing - bumps it in the same transaction as the change, so the page fence and every litigation-derived cache
/// miss and rebuild. The bump is a single atomic UPDATE, never a read-modify-write.</summary>
public static class RequestLiveData
{
    /// <summary>Adds one to the request's live-data version and returns the new value, or null when the request does not exist. Uses the context's current transaction, if any.</summary>
    public static async Task<long?> BumpAsync(AppDbContext db, long requestId, CancellationToken ct)
    {
        var rows = await db.Requests.Where(r => r.RequestId == requestId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LiveDataVersion, r => r.LiveDataVersion + 1), ct);
        if (rows == 0) return null;
        return await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => (long?)r.LiveDataVersion).FirstOrDefaultAsync(ct);
    }

    public static Task<long> ReadAsync(AppDbContext db, long requestId, CancellationToken ct) =>
        db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.LiveDataVersion).FirstOrDefaultAsync(ct);
}
