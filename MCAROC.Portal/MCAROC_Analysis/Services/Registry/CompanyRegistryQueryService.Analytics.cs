using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.Registry;

public sealed partial class CompanyRegistryQueryService
{
    // Kept separate from verified sync-job snapshots; this never fabricates a completed sync job.
    private const long ImportedBaselineSnapshotId = -1;
    private const string ImportedBaselineCacheKey = "RegistryImportedBaseline_AnalyticsV2";
    private static bool IsUsableBaseline(RegistryAggregateData? data) =>
        data?.Metadata.IsImportedBaseline == true && data.EntityAnalytics.Count > 0 && data.Metadata.CalculatedUtc.HasValue;

    /// <summary>The longest an imported baseline is trusted without being rebuilt, whatever the row count says: the count cannot see an
    /// in-place edit, so this bounds how stale a figure can become.</summary>
    internal static readonly TimeSpan MaxBaselineAge = TimeSpan.FromHours(24);

    /// <summary>The baseline is rebuilt when the registry's rows have changed, not on a clock. The rebuild is a full pass over millions of
    /// rows (a GROUPING SETS aggregate that needs a large memory grant), so repeating it every few minutes on an unchanged table starved
    /// everything else on a small SQL Server. A baseline is current while it is younger than <see cref="MaxBaselineAge"/> and the row
    /// count it was built from is still the table's. A baseline saved before the count was recorded is judged by age alone.</summary>
    private async Task<bool> IsFreshBaselineAsync(RegistryAggregateData? data, CancellationToken ct)
    {
        if (!IsUsableBaseline(data)) return false;
        if (data!.Metadata.CalculatedUtc <= DateTime.UtcNow - MaxBaselineAge) return false;
        return data.Metadata.SourceRowCount is not { } built || built == await CurrentMasterRowCountAsync(ct);
    }

    /// <summary>The registry's row count, cheaply: table metadata rather than a scan of millions of rows. Falls back to a count when the
    /// login cannot read the metadata.</summary>
    private async Task<long> CurrentMasterRowCountAsync(CancellationToken ct)
    {
        try
        {
            return (await _db.Database.SqlQuery<long>($"""
                SELECT COALESCE(SUM(row_count), 0) AS [Value] FROM sys.dm_db_partition_stats
                WHERE object_id = OBJECT_ID(N'dbo.CompanyMasterRecords') AND index_id IN (0, 1)
                """).ToListAsync(ct)).SingleOrDefault();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return await _db.CompanyMasterRecords.AsNoTracking().LongCountAsync(ct);
        }
    }

    public async Task RefreshImportedBaselineAsync(CancellationToken ct)
    {
        if (!_allowImportedBaseline) return;
        if (await _db.CompanyMasterSyncJobs.AsNoTracking().AnyAsync(j => j.Status == CompanyMasterSyncJobStatus.Promoting ||
            (j.Status == CompanyMasterSyncJobStatus.Completed && j.PublishedDate.HasValue), ct)) return;
        if (!await _db.CompanyMasterRecords.AsNoTracking().AnyAsync(ct))
        {
            // Nothing to aggregate. A baseline saved while there were rows must not keep being served for records that no longer exist.
            await ClearImportedBaselineAsync(ct);
            return;
        }
        await GetImportedBaselineAsync(ct, refresh: true);
    }

    /// <summary>The registry has no rows: replace any usable stored baseline with an empty one and drop the cached copy, so the dashboard reads
    /// "no baseline" instead of figures for records that are gone. An empty baseline is not usable (<see cref="IsUsableBaseline"/> needs analytics),
    /// and the next refresh that finds rows rebuilds it. Takes the same locks as a rebuild, and does nothing if a promotion owns the registry.</summary>
    private async Task ClearImportedBaselineAsync(CancellationToken ct)
    {
        await _rebuildLock.WaitAsync(ct);
        try
        {
            await using var gate = await _promotionCoordinator.TryAcquireRebuildGateAsync(ct);
            if (gate == null) return;
            _cache.Remove(ImportedBaselineCacheKey);
            if (!IsUsableBaseline(await _snapshotStore.GetSnapshotAsync(ImportedBaselineSnapshotId, ct))) return; // nothing stale is being served
            await _snapshotStore.SaveSnapshotAsync(ImportedBaselineSnapshotId, new RegistryAggregateData
            {
                EntityAnalytics = [],
                Metadata = new() { IsImportedBaseline = true, CalculatedUtc = DateTime.UtcNow, SourceRowCount = 0 }
            }, ct);
        }
        finally { _rebuildLock.Release(); }
    }

    private async Task<RegistryAggregateData?> GetImportedBaselineAsync(CancellationToken ct, bool refresh = false)
    {
        const string key = ImportedBaselineCacheKey;
        if (!refresh)
        {
            if (!await _promotionCoordinator.TryProbePromotionAdmissionAsync(ct)) return null;
            if (_cache.TryGetValue(key, out RegistryAggregateData? available) && IsUsableBaseline(available)) return available;
            available = await _snapshotStore.GetSnapshotAsync(ImportedBaselineSnapshotId, ct);
            if (IsUsableBaseline(available)) _cache.Set(key, available!, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)).SetSize(1));
            return IsUsableBaseline(available) ? available : null;
        }
        RegistryAggregateData? cached;
        await _rebuildLock.WaitAsync(ct);
        try
        {
            await using var gate = await _promotionCoordinator.TryAcquireRebuildGateAsync(ct);
            if (gate == null) return null;
            if (await _db.CompanyMasterSyncJobs.AsNoTracking().AnyAsync(j =>
                j.Status == CompanyMasterSyncJobStatus.Promoting ||
                (j.Status == CompanyMasterSyncJobStatus.Completed && j.PublishedDate.HasValue), ct)) return null;
            if (_cache.TryGetValue(key, out cached) && await IsFreshBaselineAsync(cached, ct)) return cached;
            cached = await _snapshotStore.GetSnapshotAsync(ImportedBaselineSnapshotId, ct);
            if (!await IsFreshBaselineAsync(cached, ct))
            {
                var previousTimeout = _db.Database.GetCommandTimeout();
                try
                {
                    // Rebuild runs in a worker scope, never in a dashboard request.
                    _db.Database.SetCommandTimeout(300);
                    var rowsBeforeBuild = await CurrentMasterRowCountAsync(ct); // read first: a row added mid-build then triggers the next rebuild
                    cached = await BuildAggregatesFromDatabaseAsync(null, ct);
                    cached.Metadata.CalculatedUtc = DateTime.UtcNow;
                    cached.Metadata.SourceRowCount = rowsBeforeBuild;
                    await _snapshotStore.SaveSnapshotAsync(ImportedBaselineSnapshotId, cached, ct);
                }
                finally { _db.Database.SetCommandTimeout(previousTimeout); }
            }
            _cache.Set(key, cached!, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)).SetSize(1));
            return cached;
        }
        finally { _rebuildLock.Release(); }
    }

    private async Task<RegistryAggregateData> BuildAggregatesFromDatabaseAsync(CompanyMasterSyncJob? job, CancellationToken ct)
    {
        var previousTimeout = _db.Database.GetCommandTimeout();
        try
        {
            _db.Database.SetCommandTimeout(300);
            return await BuildAggregateCoreAsync(job, ct);
        }
        finally { _db.Database.SetCommandTimeout(previousTimeout); }
    }

}
