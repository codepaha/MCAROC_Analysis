using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Models.Viz;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Registry;

public sealed partial class CompanyRegistryQueryService
{
    private readonly AppDbContext _db;
    private readonly bool _allowImportedBaseline;
    private readonly IMemoryCache _cache;
    private readonly IRegistryPromotionCoordinator _promotionCoordinator;
    private readonly IRegistrySnapshotStore _snapshotStore;
    private readonly ILogger<CompanyRegistryQueryService> _logger;
    private static readonly SemaphoreSlim DefaultProcessRebuildLock = new(1, 1);
    private readonly SemaphoreSlim _rebuildLock;

    [GeneratedRegex("^[LU][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}$")]
    private static partial Regex CinPattern();

    [GeneratedRegex("^[A-Z]{3}-[0-9]{4}$")]
    private static partial Regex LlpinPattern();

    [GeneratedRegex("^F[0-9]{5}$")]
    private static partial Regex StandardFcrnPattern();

    [GeneratedRegex("^F[0-9]+$")]
    private static partial Regex NumericFcrnPattern();

    public CompanyRegistryQueryService(
        AppDbContext db,
        IMemoryCache cache,
        IRegistryPromotionCoordinator promotionCoordinator,
        IRegistrySnapshotStore snapshotStore,
        ILogger<CompanyRegistryQueryService> logger,
        SemaphoreSlim? localRebuildLock = null,
        IConfiguration? configuration = null)
    {
        _allowImportedBaseline = configuration?.GetValue<bool>("RegistryAnalytics:AllowImportedBaseline") == true;
        _db = db;
        _cache = cache;
        _promotionCoordinator = promotionCoordinator;
        _snapshotStore = snapshotStore;
        _logger = logger;
        _rebuildLock = localRebuildLock ?? DefaultProcessRebuildLock;
    }

    public static string CacheKeyForJob(long jobId) => $"RegistryAggregates_Job_{jobId}";

    public Task<RegistryDashboardViewModel> GetDashboardAsync(RegistryExplorerCriteria? explorerCriteria = null, CancellationToken ct = default)
        => GetDashboardAsync("overview", explorerCriteria, ct);

    public async Task<RegistryDashboardViewModel> GetDashboardAsync(string? activeTab, RegistryExplorerCriteria? explorerCriteria, CancellationToken ct = default)
    {
        try
        {
            return await GetDashboardCoreAsync(activeTab, explorerCriteria, ct);
        }
        catch (SqlException ex) when (ex.Number == -2 && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Registry dashboard SQL request timed out. No registry metrics will be displayed.");
            return new RegistryDashboardViewModel
            {
                ActiveTab = string.IsNullOrWhiteSpace(activeTab) ? "overview" : activeTab.ToLowerInvariant(),
                State = RegistrySnapshotState.DatabaseUnavailable,
                StatusMessage = "Registry data is taking longer than expected to load. Please try again shortly."
            };
        }
    }

    private async Task<RegistryDashboardViewModel> GetDashboardCoreAsync(string? activeTab, RegistryExplorerCriteria? explorerCriteria, CancellationToken ct)
    {
        var vm = new RegistryDashboardViewModel
        {
            ActiveTab = string.IsNullOrWhiteSpace(activeTab) ? "overview" : activeTab.ToLowerInvariant()
        };

        // 1. Initial status check across jobs
        var activePromotionJob = await _db.CompanyMasterSyncJobs
            .AsNoTracking()
            .Where(j => j.Status == CompanyMasterSyncJobStatus.Promoting)
            .OrderByDescending(j => j.JobId)
            .FirstOrDefaultAsync(ct);

        var latestCompleted = await _db.CompanyMasterSyncJobs
            .AsNoTracking()
            .Where(j => j.Status == CompanyMasterSyncJobStatus.Completed && j.PublishedDate.HasValue)
            .OrderByDescending(j => j.JobId)
            .FirstOrDefaultAsync(ct);

        if (latestCompleted == null)
        {
            // Evaluate legacy unverified vs empty state
            bool recordsExist = await _db.CompanyMasterRecords.AnyAsync(ct);
            if (recordsExist)
            {
                vm.State = RegistrySnapshotState.UnverifiedLegacyImport;
                vm.StatusMessage = "Legacy Master Import Detected (Unverified Provenance): The registry contains records imported without a sync lifecycle job. Aggregate metrics are withheld until an automated or manual sync establishes verified snapshot provenance. Exact identifier and name-prefix lookups in the Explorer remain fully operational.";
            }
            else
            {
                vm.State = RegistrySnapshotState.EmptyRegistry;
                vm.StatusMessage = "No records found in registry. A master data sync or bulk import is required to initialize registry intelligence.";
            }

            if (recordsExist && _allowImportedBaseline && activePromotionJob == null)
            {
                vm.Aggregates = await GetImportedBaselineAsync(ct);
                if (vm.Aggregates == null)
                {
                    vm.State = RegistrySnapshotState.SyncColdUnavailable;
                    vm.StatusMessage = "Imported registry analytics are being prepared in the background or a sync is in progress. Please try again shortly. The Explorer remains available.";
                }
                else
                {
                    vm.State = RegistrySnapshotState.ImportedBaseline;
                    vm.StatusMessage = "Analytics reflect the currently imported records. Source publication date and import history are unavailable; these are not a verified MCA published snapshot.";
                }
            }

            if (explorerCriteria != null && (!string.IsNullOrWhiteSpace(explorerCriteria.Q) || explorerCriteria.HasSecondaryFilters))
            {
                vm.Explorer = await SearchExplorerAsync(explorerCriteria, ct);
            }
            return vm;
        }

        // 2. Multi-tier aggregate caching: L1 (in-memory) -> L2 (shared snapshot store) -> L3 (single-flight rebuild)
        string cacheKey = CacheKeyForJob(latestCompleted.JobId);
        bool hasWarmCache = _cache.TryGetValue(cacheKey, out RegistryAggregateData? cachedData);

        if (!hasWarmCache || cachedData == null)
        {
            // Check L2 shared store before contending for rebuild lock
            cachedData = await _snapshotStore.GetSnapshotAsync(latestCompleted.JobId, ct);
            if (cachedData != null)
            {
                hasWarmCache = true;
                _cache.Set(cacheKey, cachedData, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(24)).SetSize(1));
            }
        }

        if (hasWarmCache && cachedData != null)
        {
            // A warm-cache request probes the advisory promotion gate to detect remote or local active promotions
            bool isPromoting = activePromotionJob != null;
            if (!isPromoting)
            {
                bool gateOpen = await _promotionCoordinator.TryProbePromotionAdmissionAsync(ct);
                if (!gateOpen)
                {
                    isPromoting = true;
                }
                else
                {
                    isPromoting = await _db.CompanyMasterSyncJobs
                        .AsNoTracking()
                        .AnyAsync(j => j.Status == CompanyMasterSyncJobStatus.Promoting, ct);
                }
            }

            if (isPromoting)
            {
                activePromotionJob ??= await _db.CompanyMasterSyncJobs
                    .AsNoTracking()
                    .Where(j => j.Status == CompanyMasterSyncJobStatus.Promoting)
                    .OrderByDescending(j => j.JobId)
                    .FirstOrDefaultAsync(ct);

                vm.State = RegistrySnapshotState.VerifiedSnapshot;
                vm.Aggregates = CloneWithActiveSync(cachedData, activePromotionJob);
            }
            else
            {
                vm.State = RegistrySnapshotState.VerifiedSnapshot;
                vm.Aggregates = cachedData;
            }
        }
        else
        {
            // Cold cache: single-flight rebuild under _rebuildLock
            await _rebuildLock.WaitAsync(ct);
            try
            {
                if (!_cache.TryGetValue(cacheKey, out cachedData))
                {
                    // Double-check L2 store inside RebuildLock
                    cachedData = await _snapshotStore.GetSnapshotAsync(latestCompleted.JobId, ct);
                    if (cachedData != null)
                    {
                        _cache.Set(cacheKey, cachedData, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(24)).SetSize(1));
                    }
                    else
                    {
                        // If promotion was already detected in initial check, withhold cold cache without acquiring gate
                        if (activePromotionJob != null)
                        {
                            vm.State = RegistrySnapshotState.SyncColdUnavailable;
                            vm.StatusMessage = "Verified Aggregates Temporarily Unavailable: A Company Master snapshot sync is currently promoting into the registry database, and this instance has no pre-promotion cached aggregates. Verified metrics will appear once the active promotion completes and establishes consistency. Exact identifier and name-prefix lookups in the Explorer remain fully operational.";
                            return vm;
                        }

                        // Acquire cluster-wide rebuild gate (Shared promotion + Exclusive rebuild)
                        await using var rebuildGate = await _promotionCoordinator.TryAcquireRebuildGateAsync(ct);
                        if (rebuildGate == null)
                        {
                            // Non-owner node: another node holds rebuild lock, or promotion is active
                            vm.State = RegistrySnapshotState.SyncColdUnavailable;
                            vm.StatusMessage = "Verified Aggregates Temporarily Unavailable: A Company Master snapshot sync is currently promoting into the registry database or another portal instance is compiling verified aggregates. Verified metrics will appear once compilation completes. Exact identifier and name-prefix lookups in the Explorer remain fully operational.";
                            return vm;
                        }

                        // Inside rebuild gate, verify DB does not show active Promoting
                        bool isDbPromoting = await _db.CompanyMasterSyncJobs
                            .AsNoTracking()
                            .AnyAsync(j => j.Status == CompanyMasterSyncJobStatus.Promoting, ct);

                        if (isDbPromoting)
                        {
                            vm.State = RegistrySnapshotState.SyncColdUnavailable;
                            vm.StatusMessage = "Verified Aggregates Temporarily Unavailable: A Company Master snapshot sync is currently promoting into the registry database, and this instance has no pre-promotion cached aggregates. Verified metrics will appear once the active promotion completes and establishes consistency. Exact identifier and name-prefix lookups in the Explorer remain fully operational.";
                            return vm;
                        }

                        cachedData = await BuildAggregatesFromDatabaseAsync(latestCompleted, ct);

                        // Retain rebuild gate until shared-store write succeeds. Fail closed without populating L1 if persistence fails.
                        try
                        {
                            await _snapshotStore.SaveSnapshotAsync(latestCompleted.JobId, cachedData, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to persist aggregate snapshot for Job {JobId} to shared store. Failing closed without populating L1.", latestCompleted.JobId);
                            vm.State = RegistrySnapshotState.SyncColdUnavailable;
                            vm.StatusMessage = "Verified Aggregates Storage Error: Aggregates were computed but could not be persisted to the shared snapshot store. Explorer lookups remain operational.";
                            return vm;
                        }

                        _cache.Set(cacheKey, cachedData, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(24)).SetSize(1));
                    }
                }

                vm.State = RegistrySnapshotState.VerifiedSnapshot;
                if (cachedData != null)
                {
                    vm.Aggregates = activePromotionJob != null
                        ? CloneWithActiveSync(cachedData, activePromotionJob)
                        : cachedData;
                }
            }
            finally
            {
                _rebuildLock.Release();
            }
        }

        // 3. Handle Explorer if query submitted
        if (explorerCriteria != null && (!string.IsNullOrWhiteSpace(explorerCriteria.Q) || explorerCriteria.HasSecondaryFilters))
        {
            vm.Explorer = await SearchExplorerAsync(explorerCriteria, ct);
        }

        return vm;
    }

    public async Task<RegistryExplorerResult> SearchExplorerAsync(RegistryExplorerCriteria criteria, CancellationToken ct = default)
    {
        var result = new RegistryExplorerResult
        {
            Criteria = criteria,
            SearchExecuted = true
        };

        var rawQ = criteria.Q?.Trim();
        var status = criteria.Status?.Trim();
        if (string.IsNullOrWhiteSpace(rawQ) && string.IsNullOrWhiteSpace(status))
        {
            result.ValidationErrorMessage = "Enter an exact identifier, a 3+ character name prefix, or select an entity type and status.";
            return result;
        }

        var upperQ = rawQ?.ToUpperInvariant() ?? "";

        // 1. Check if input is formatted like an exact Identifier
        // CIN: 21 chars, LLPIN: AAA-1234, Standard FCRN: F00000.
        // For explicitly selected Foreign entity type, also accept numeric FCRN variations (e.g. F123456).
        bool isCin = CinPattern().IsMatch(upperQ);
        bool isLlpin = LlpinPattern().IsMatch(upperQ);
        bool isStandardFcrn = StandardFcrnPattern().IsMatch(upperQ);
        bool isExplicitForeignNumeric = criteria.RecordType == CompanyMasterRecordType.Foreign && NumericFcrnPattern().IsMatch(upperQ);

        if (isCin || isLlpin || isStandardFcrn || isExplicitForeignNumeric)
        {
            if (criteria.HasSecondaryFilters)
            {
                result.ValidationErrorMessage = "Clear the filters when looking up an exact identifier.";
                return result;
            }
            // Exact PK Seek on Identifier
            var exactMatch = await _db.CompanyMasterRecords
                .AsNoTracking()
                .Where(r => r.Identifier == upperQ)
                .Select(ExplorerProjection)
                .FirstOrDefaultAsync(ct);

            if (exactMatch != null)
            {
                result.Items = [exactMatch];
                result.HasNextPage = false;
                return result;
            }

            // Also check if they passed CIN/LLPIN without exact match
            result.Items = [];
            result.HasNextPage = false;
            return result;
        }

        // 2. List lookup requires an entity type and either a name prefix or an indexed status.
        if (!criteria.RecordType.HasValue)
        {
            result.ValidationErrorMessage = "List search requires selecting an Entity Type (Company, LLP, or Foreign).";
            return result;
        }

        if (!Enum.IsDefined(criteria.RecordType.Value) ||
            (!string.IsNullOrWhiteSpace(rawQ) && rawQ.Length < 3) ||
            (string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(rawQ)))
        {
            result.ValidationErrorMessage = "Name prefix search requires at least 3 characters, or choose a status.";
            return result;
        }

        if (status?.Length > 30 || criteria.State?.Length > 80 || criteria.District?.Length > 80 ||
            criteria.Country?.Length > 80 || criteria.Industry?.Length > 200 ||
            criteria.Class?.Length > 50 || criteria.Year is < 1800 or > 2100)
        {
            result.ValidationErrorMessage = "One or more filters are too long or outside the supported year range.";
            return result;
        }

        int pageSize = Math.Clamp(criteria.PageSize, 1, 50);

        // Status-only lists use (RecordType, Status, Name); name searches use (RecordType, Name).
        var query = BuildExplorerListQuery(criteria);

        if (!string.IsNullOrWhiteSpace(criteria.CursorName) && !string.IsNullOrWhiteSpace(criteria.CursorIdentifier))
        {
            var cursorName = criteria.CursorName;
            var cursorId = criteria.CursorIdentifier;
            query = query.Where(r => string.Compare(r.Name, cursorName) > 0
                || (r.Name == cursorName && string.Compare(r.Identifier, cursorId) > 0));
        }

        var rows = await query
            .OrderBy(r => r.Name)
            .ThenBy(r => r.Identifier)
            .Take(pageSize + 1)
            .Select(ExplorerProjection)
            .ToListAsync(ct);

        if (rows.Count > pageSize)
        {
            result.HasNextPage = true;
            var lastKept = rows[pageSize - 1];
            result.NextCursorName = lastKept.Name;
            result.NextCursorIdentifier = lastKept.Identifier;
            result.Items = rows.Take(pageSize).ToList();
        }
        else
        {
            result.HasNextPage = false;
            result.Items = rows.ToList();
        }

        return result;
    }

    private IQueryable<CompanyMasterRecord> BuildExplorerListQuery(RegistryExplorerCriteria criteria)
    {
        var query = _db.CompanyMasterRecords.AsNoTracking()
            .Where(r => r.RecordType == criteria.RecordType!.Value);
        var q = criteria.Q?.Trim();
        var status = criteria.Status?.Trim();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(r => r.Name.StartsWith(q));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(criteria.State)) query = query.Where(r => r.State == criteria.State.Trim());
        if (!string.IsNullOrWhiteSpace(criteria.District)) query = query.Where(r => r.District == criteria.District.Trim());
        if (!string.IsNullOrWhiteSpace(criteria.Country)) query = query.Where(r => r.Country == criteria.Country.Trim());
        if (!string.IsNullOrWhiteSpace(criteria.Industry)) query = query.Where(r => r.IndustrialClassification != null && r.IndustrialClassification.StartsWith(criteria.Industry.Trim()));
        if (!string.IsNullOrWhiteSpace(criteria.Class)) query = query.Where(r => r.Class == criteria.Class.Trim());
        if (criteria.Year.HasValue)
        {
            var first = new DateOnly(criteria.Year.Value, 1, 1);
            var next = first.AddYears(1);
            query = query.Where(r => r.RegistrationDate >= first && r.RegistrationDate < next);
        }
        return query;
    }

    public async Task<(IReadOnlyList<RegistryRecordRow> Rows, string? Error)> ExportExplorerAsync(RegistryExplorerCriteria criteria, CancellationToken ct = default)
    {
        var validation = await SearchExplorerAsync(new RegistryExplorerCriteria
        {
            Q = criteria.Q, RecordType = criteria.RecordType, Status = criteria.Status,
            State = criteria.State, District = criteria.District, Country = criteria.Country,
            Industry = criteria.Industry, Class = criteria.Class, Year = criteria.Year,
            PageSize = 1
        }, ct);
        if (validation.ValidationErrorMessage != null) return ([], validation.ValidationErrorMessage);

        const int maxRows = 10000;
        IQueryable<CompanyMasterRecord> query;
        if (criteria.Q is { } q &&
            (CinPattern().IsMatch(q.Trim().ToUpperInvariant()) || LlpinPattern().IsMatch(q.Trim().ToUpperInvariant()) ||
             StandardFcrnPattern().IsMatch(q.Trim().ToUpperInvariant()) ||
             (criteria.RecordType == CompanyMasterRecordType.Foreign && NumericFcrnPattern().IsMatch(q.Trim().ToUpperInvariant()))))
            query = _db.CompanyMasterRecords.AsNoTracking().Where(r => r.Identifier == q.Trim().ToUpperInvariant());
        else
            query = BuildExplorerListQuery(criteria);

        var rows = await query.OrderBy(r => r.Name).ThenBy(r => r.Identifier)
            .Take(maxRows + 1).Select(ExplorerProjection).ToListAsync(ct);
        if (rows.Count > maxRows)
            return ([], $"More than {maxRows:N0} records match. Narrow the filters before downloading Excel.");
        return (rows, null);
    }

    internal static readonly Expression<Func<CompanyMasterRecord, RegistryRecordRow>> ExplorerProjection = r => new RegistryRecordRow()
    {
        Identifier = r.Identifier,
        Name = r.Name,
        RecordType = r.RecordType,
        Status = r.Status,
        RegistrationDate = r.RegistrationDate,
        State = r.State,
        District = r.District,
        Roc = r.Roc,
        Class = r.Class,
        Industry = r.IndustrialClassification,
        Address = r.Address,
        PinCode = r.PinCode,
        ListingStatus = r.ListingStatus,
        Country = r.Country
    };

    private static RegistryAggregateData CloneWithActiveSync(RegistryAggregateData source, CompanyMasterSyncJob? activeJob)
    {
        return new RegistryAggregateData
        {
            EntityAnalytics = source.EntityAnalytics,
            Metadata = new RegistrySnapshotMetadata
            {
                CalculatedUtc = source.Metadata.CalculatedUtc,
                IsImportedBaseline = source.Metadata.IsImportedBaseline,
                PublishedDate = source.Metadata.PublishedDate,
                CompletedUtc = source.Metadata.CompletedUtc,
                Source = source.Metadata.Source,
                IsSyncInProgress = true,
                ActiveSyncStatus = activeJob?.Status ?? CompanyMasterSyncJobStatus.Promoting,
                ActiveJobId = activeJob?.JobId,
                TotalRecords = source.Metadata.TotalRecords,
                CompanyCount = source.Metadata.CompanyCount,
                LlpCount = source.Metadata.LlpCount,
                ForeignCount = source.Metadata.ForeignCount
            },
            OverallStatus = source.OverallStatus,
            CompanyStatus = source.CompanyStatus,
            LlpStatus = source.LlpStatus,
            ForeignStatus = source.ForeignStatus,
            TopStatesChart = source.TopStatesChart,
            TopIndustriesChart = source.TopIndustriesChart,
            ForeignCountriesChart = source.ForeignCountriesChart,
            CompanyRegistrationYearSeries = source.CompanyRegistrationYearSeries,
            LlpRegistrationYearSeries = source.LlpRegistrationYearSeries,
            CompanyClasses = source.CompanyClasses,
            ListingStatusSplit = source.ListingStatusSplit,
            CapitalDistribution = source.CapitalDistribution
        };
    }

    public static RegistryStatusMetrics MapStatusMetrics(
        IEnumerable<(string? Status, int Count)> rows,
        HashSet<string> observedVocabulary,
        bool isForeign)
    {
        var metrics = new RegistryStatusMetrics();

        // CIRP / Liquidation conditional on actual observed strings
        metrics.HasObservedCirp = !isForeign && observedVocabulary.Contains("Under CIRP");
        metrics.HasObservedLiquidation = !isForeign && (observedVocabulary.Contains("Under Liquidation") || observedVocabulary.Contains("Dissolved (Liquidated)"));

        foreach (var (status, count) in rows)
        {
            var s = status?.Trim();
            if (string.IsNullOrEmpty(s))
            {
                metrics.OtherUnclassified += count;
                continue;
            }

            if (s.Equals("Active", StringComparison.OrdinalIgnoreCase))
            {
                metrics.Active += count;
            }
            else if (s.Equals("Strike Off", StringComparison.OrdinalIgnoreCase) ||
                     s.Equals("Defunct", StringComparison.OrdinalIgnoreCase) ||
                     (isForeign && (s.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                                    s.Equals("Ceased to have place of business", StringComparison.OrdinalIgnoreCase) ||
                                    s.Equals("Not available for efiling", StringComparison.OrdinalIgnoreCase))))
            {
                metrics.StrikeOff += count;
            }
            else if (!isForeign && s.Equals("Under CIRP", StringComparison.OrdinalIgnoreCase))
            {
                metrics.UnderCirp += count;
            }
            else if (!isForeign && (s.Equals("Under Liquidation", StringComparison.OrdinalIgnoreCase) || s.Equals("Dissolved (Liquidated)", StringComparison.OrdinalIgnoreCase)))
            {
                metrics.UnderLiquidation += count;
            }
            else
            {
                metrics.OtherUnclassified += count;
            }
        }

        return metrics;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..(maxLength - 1)] + "…";
    }
}
