using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.Registry;

public sealed partial class CompanyRegistryQueryService
{
    // Kept separate from verified sync-job snapshots; this never fabricates a completed sync job.
    private const long ImportedBaselineSnapshotId = -1;
    private static bool IsUsableBaseline(RegistryAggregateData? data) =>
        data?.Metadata.IsImportedBaseline == true && data.EntityAnalytics.Count > 0 && data.Metadata.CalculatedUtc.HasValue;
    private static bool IsFreshBaseline(RegistryAggregateData? data) => IsUsableBaseline(data) &&
        data!.Metadata.CalculatedUtc > DateTime.UtcNow.AddMinutes(-15);

    public async Task RefreshImportedBaselineAsync(CancellationToken ct)
    {
        if (!_allowImportedBaseline) return;
        if (await _db.CompanyMasterSyncJobs.AsNoTracking().AnyAsync(j => j.Status == CompanyMasterSyncJobStatus.Promoting ||
            (j.Status == CompanyMasterSyncJobStatus.Completed && j.PublishedDate.HasValue), ct)) return;
        if (!await _db.CompanyMasterRecords.AsNoTracking().AnyAsync(ct)) return;
        await GetImportedBaselineAsync(ct, refresh: true);
    }

    private async Task<RegistryAggregateData?> GetImportedBaselineAsync(CancellationToken ct, bool refresh = false)
    {
        const string key = "RegistryImportedBaseline_AnalyticsV2";
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
            if (_cache.TryGetValue(key, out cached) && IsFreshBaseline(cached)) return cached;
            cached = await _snapshotStore.GetSnapshotAsync(ImportedBaselineSnapshotId, ct);
            if (!IsFreshBaseline(cached))
            {
                var previousTimeout = _db.Database.GetCommandTimeout();
                try
                {
                    // Rebuild runs in a worker scope, never in a dashboard request.
                    _db.Database.SetCommandTimeout(300);
                    cached = await BuildAggregatesFromDatabaseAsync(null, ct);
                    cached.Metadata.CalculatedUtc = DateTime.UtcNow;
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
