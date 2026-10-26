using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>A daily cap on paid calls for one purpose, reserved atomically just before each call (<see cref="AiDailyCallCount"/>).</summary>
public static class AiDailyCallBudget
{
    /// <summary>Reserves one call for today (IST) if fewer than <paramref name="maxPerDay"/> have been reserved. One conditional UPDATE does
    /// the check and the increment together, so concurrent workers can never pass the cap between them.</summary>
    public static async Task<bool> TryReserveAsync(AppDbContext db, string purpose, int maxPerDay, DateTime utcNow, CancellationToken ct)
    {
        if (maxPerDay <= 0) return false;
        var day = DateOnly.FromDateTime(Ist.FromUtc(utcNow));
        if (!await db.AiDailyCallCounts.AnyAsync(x => x.Purpose == purpose && x.Day == day, ct))
        {
            var row = new AiDailyCallCount { Purpose = purpose, Day = day };
            db.AiDailyCallCounts.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 }) { /* another worker created the day first */ }
            finally { db.Entry(row).State = EntityState.Detached; }
        }
        return await db.AiDailyCallCounts.Where(x => x.Purpose == purpose && x.Day == day && x.Calls < maxPerDay)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Calls, x => x.Calls + 1), ct) == 1;
    }

    public static async Task<int> CallsOnAsync(AppDbContext db, string purpose, DateTime utc, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(Ist.FromUtc(utc));
        return await db.AiDailyCallCounts.AsNoTracking().Where(x => x.Purpose == purpose && x.Day == day).Select(x => x.Calls).FirstOrDefaultAsync(ct);
    }
}
