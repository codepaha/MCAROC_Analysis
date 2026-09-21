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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Registry;

public sealed class CompanyRegistryQueryService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CompanyRegistryQueryService> _logger;
    private static readonly SemaphoreSlim RebuildLock = new(1, 1);

    public CompanyRegistryQueryService(
        AppDbContext db,
        IMemoryCache cache,
        ILogger<CompanyRegistryQueryService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public static string CacheKeyForJob(long jobId) => $"RegistryAggregates_Job_{jobId}";

    public async Task<RegistryDashboardViewModel> GetDashboardAsync(
        string? activeTab,
        RegistryExplorerCriteria? explorerCriteria,
        CancellationToken ct = default)
    {
        var vm = new RegistryDashboardViewModel
        {
            ActiveTab = string.IsNullOrWhiteSpace(activeTab) ? "overview" : activeTab.ToLowerInvariant()
        };

        // 1. Identify snapshot status
        var latestCompleted = await _db.CompanyMasterSyncJobs
            .AsNoTracking()
            .Where(j => j.Status == CompanyMasterSyncJobStatus.Completed)
            .OrderByDescending(j => j.JobId)
            .FirstOrDefaultAsync(ct);

        var activePromotionJob = await _db.CompanyMasterSyncJobs
            .AsNoTracking()
            .Where(j => j.Status == CompanyMasterSyncJobStatus.Promoting
                     || j.Status == CompanyMasterSyncJobStatus.Staging
                     || j.Status == CompanyMasterSyncJobStatus.Downloading)
            .OrderByDescending(j => j.JobId)
            .FirstOrDefaultAsync(ct);

        if (latestCompleted == null)
        {
            var hasAnyRecords = await _db.CompanyMasterRecords.AsNoTracking().AnyAsync(ct);
            if (hasAnyRecords)
            {
                vm.State = RegistrySnapshotState.UnverifiedLegacyImport;
                vm.StatusMessage = "Legacy Master Import Detected (Unverified Provenance): The registry contains records imported without a sync lifecycle job. Aggregate metrics are withheld until an automated or manual sync establishes verified snapshot provenance. Exact identifier and name-prefix lookups in the Explorer remain fully operational.";
            }
            else
            {
                vm.State = RegistrySnapshotState.EmptyRegistry;
                vm.StatusMessage = "No records found in registry. A master data sync or bulk import is required to initialize registry intelligence.";
            }

            if (explorerCriteria != null && !string.IsNullOrWhiteSpace(explorerCriteria.Q))
            {
                vm.Explorer = await SearchExplorerAsync(explorerCriteria, ct);
            }

            return vm;
        }

        // 2. We have a completed sync job. Check in-flight sync & cache.
        string cacheKey = CacheKeyForJob(latestCompleted.JobId);
        bool hasWarmCache = _cache.TryGetValue(cacheKey, out RegistryAggregateData? cachedData);

        if (activePromotionJob != null)
        {
            if (hasWarmCache && cachedData != null)
            {
                // Serve cached stable snapshot with active sync flag
                vm.State = RegistrySnapshotState.VerifiedSnapshot;
                vm.Aggregates = CloneWithActiveSync(cachedData, activePromotionJob);
            }
            else
            {
                // Cold cache during active promotion — NEVER scan live table!
                vm.State = RegistrySnapshotState.SyncColdUnavailable;
                vm.StatusMessage = "Verified Aggregates Temporarily Unavailable: A Company Master snapshot sync is currently promoting into the registry database, and this instance has no pre-promotion cached aggregates. Verified metrics will appear once the active promotion completes and establishes consistency. Exact identifier and name-prefix lookups in the Explorer remain fully operational.";
            }
        }
        else
        {
            // No active sync. Check cache or single-flight rebuild.
            if (hasWarmCache && cachedData != null)
            {
                vm.State = RegistrySnapshotState.VerifiedSnapshot;
                vm.Aggregates = cachedData;
            }
            else
            {
                // Single-flight rebuild
                await RebuildLock.WaitAsync(ct);
                try
                {
                    if (!_cache.TryGetValue(cacheKey, out cachedData))
                    {
                        cachedData = await BuildAggregatesFromDatabaseAsync(latestCompleted, ct);
                        _cache.Set(cacheKey, cachedData, TimeSpan.FromHours(24));
                    }
                    vm.State = RegistrySnapshotState.VerifiedSnapshot;
                    vm.Aggregates = cachedData;
                }
                finally
                {
                    RebuildLock.Release();
                }
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

        // Query-plan gate rule: Reject secondary filters in v1
        if (criteria.HasSecondaryFilters)
        {
            result.ValidationErrorMessage = "Secondary filtering on State, Status, Industry, Class, or Year is disabled in v1 to protect database performance. Search by exact CIN/LLPIN/FCRN or Entity Type with a 3+ character Name prefix.";
            return result;
        }

        var rawQ = criteria.Q?.Trim();
        if (string.IsNullOrWhiteSpace(rawQ))
        {
            result.ValidationErrorMessage = "Enter an exact Identifier (CIN, LLPIN, FCRN) or select an Entity Type and enter at least 3 letters of a company name.";
            return result;
        }

        var upperQ = rawQ.ToUpperInvariant();

        // 1. Check if input is formatted like an Identifier (CIN: 21 chars; LLPIN: contains hyphen e.g. AAA-1234; FCRN: starts with F)
        bool isCinShape = upperQ.Length == 21 && (upperQ.StartsWith('U') || upperQ.StartsWith('L'));
        bool isLlpinShape = upperQ.Contains('-') && upperQ.Length >= 6 && upperQ.Length <= 10;
        bool isFcrnShape = upperQ.StartsWith('F') && upperQ.Length >= 5;

        if (isCinShape || isLlpinShape || isFcrnShape)
        {
            // Exact PK Seek on Identifier
            var exactMatch = await _db.CompanyMasterRecords
                .AsNoTracking()
                .Where(r => r.Identifier == upperQ)
                .FirstOrDefaultAsync(ct);

            if (exactMatch != null)
            {
                result.Items = [ToRow(exactMatch)];
                result.HasNextPage = false;
                return result;
            }

            // Also check if they passed CIN/LLPIN without exact match
            result.Items = [];
            result.HasNextPage = false;
            return result;
        }

        // 2. Name-prefix seek requires EntityType and at least 3 characters
        if (!criteria.RecordType.HasValue)
        {
            result.ValidationErrorMessage = "Searching by name prefix requires selecting an Entity Type (Company, LLP, or Foreign).";
            return result;
        }

        if (rawQ.Length < 3)
        {
            result.ValidationErrorMessage = "Name prefix search requires at least 3 characters.";
            return result;
        }

        var targetType = criteria.RecordType.Value;
        int pageSize = Math.Clamp(criteria.PageSize, 1, 50);

        // Bounded index seek on (RecordType, Name) with deterministic keyset tie-breaker (Name, Identifier)
        var query = _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.RecordType == targetType && r.Name.StartsWith(rawQ));

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
            .ToListAsync(ct);

        if (rows.Count > pageSize)
        {
            result.HasNextPage = true;
            var lastKept = rows[pageSize - 1];
            result.NextCursorName = lastKept.Name;
            result.NextCursorIdentifier = lastKept.Identifier;
            result.Items = rows.Take(pageSize).Select(ToRow).ToList();
        }
        else
        {
            result.HasNextPage = false;
            result.Items = rows.Select(ToRow).ToList();
        }

        return result;
    }

    private static RegistryRecordRow ToRow(CompanyMasterRecord r) => new()
    {
        Identifier = r.Identifier,
        Name = r.Name,
        RecordType = r.RecordType,
        Status = r.Status,
        RegistrationDate = r.RegistrationDate,
        State = r.State,
        Roc = r.Roc,
        Class = r.Class,
        Industry = r.IndustrialClassification,
        Address = r.Address,
        PinCode = r.PinCode,
        ListingStatus = r.ListingStatus,
        Country = r.Country
    };

    private static RegistryAggregateData CloneWithActiveSync(RegistryAggregateData source, CompanyMasterSyncJob activeJob)
    {
        return new RegistryAggregateData
        {
            Metadata = new RegistrySnapshotMetadata
            {
                PublishedDate = source.Metadata.PublishedDate,
                CompletedUtc = source.Metadata.CompletedUtc,
                Source = source.Metadata.Source,
                IsSyncInProgress = true,
                ActiveSyncStatus = activeJob.Status,
                ActiveJobId = activeJob.JobId,
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

    private async Task<RegistryAggregateData> BuildAggregatesFromDatabaseAsync(CompanyMasterSyncJob completedJob, CancellationToken ct)
    {
        _logger.LogInformation("Building verified RegistryAggregates for completed Job {JobId} (Published {Date})...", completedJob.JobId, completedJob.PublishedDate);

        var data = new RegistryAggregateData
        {
            Metadata = new RegistrySnapshotMetadata
            {
                PublishedDate = completedJob.PublishedDate,
                CompletedUtc = completedJob.CompletedUtc,
                Source = "MCA Corporate Data Management (mcacdm.nic.in)",
                IsSyncInProgress = false
            }
        };

        // 1. Dynamic vocabulary check across datasets
        var observedVocabulary = await _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.Status != null)
            .Select(r => new { r.RecordType, Status = r.Status! })
            .Distinct()
            .ToListAsync(ct);

        var observedCompanyStatuses = new HashSet<string>(observedVocabulary.Where(v => v.RecordType == CompanyMasterRecordType.Company).Select(v => v.Status), StringComparer.OrdinalIgnoreCase);
        var observedLlpStatuses = new HashSet<string>(observedVocabulary.Where(v => v.RecordType == CompanyMasterRecordType.Llp).Select(v => v.Status), StringComparer.OrdinalIgnoreCase);
        var observedForeignStatuses = new HashSet<string>(observedVocabulary.Where(v => v.RecordType == CompanyMasterRecordType.Foreign).Select(v => v.Status), StringComparer.OrdinalIgnoreCase);

        // 2. Status counts grouped by RecordType and Status
        var statusGroups = await _db.CompanyMasterRecords
            .AsNoTracking()
            .GroupBy(r => new { r.RecordType, r.Status })
            .Select(g => new { g.Key.RecordType, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        data.CompanyStatus = MapStatusMetrics(statusGroups.Where(g => g.RecordType == CompanyMasterRecordType.Company).Select(g => (g.Status, g.Count)), observedCompanyStatuses, isForeign: false);
        data.LlpStatus = MapStatusMetrics(statusGroups.Where(g => g.RecordType == CompanyMasterRecordType.Llp).Select(g => (g.Status, g.Count)), observedLlpStatuses, isForeign: false);
        data.ForeignStatus = MapStatusMetrics(statusGroups.Where(g => g.RecordType == CompanyMasterRecordType.Foreign).Select(g => (g.Status, g.Count)), observedForeignStatuses, isForeign: true);

        // Overall status
        data.OverallStatus = new RegistryStatusMetrics
        {
            Active = data.CompanyStatus.Active + data.LlpStatus.Active + data.ForeignStatus.Active,
            StrikeOff = data.CompanyStatus.StrikeOff + data.LlpStatus.StrikeOff + data.ForeignStatus.StrikeOff,
            UnderCirp = data.CompanyStatus.UnderCirp + data.LlpStatus.UnderCirp,
            UnderLiquidation = data.CompanyStatus.UnderLiquidation + data.LlpStatus.UnderLiquidation,
            OtherUnclassified = data.CompanyStatus.OtherUnclassified + data.LlpStatus.OtherUnclassified + data.ForeignStatus.OtherUnclassified,
            HasObservedCirp = data.CompanyStatus.HasObservedCirp || data.LlpStatus.HasObservedCirp,
            HasObservedLiquidation = data.CompanyStatus.HasObservedLiquidation || data.LlpStatus.HasObservedLiquidation
        };

        data.Metadata.CompanyCount = data.CompanyStatus.Total;
        data.Metadata.LlpCount = data.LlpStatus.Total;
        data.Metadata.ForeignCount = data.ForeignStatus.Total;
        data.Metadata.TotalRecords = data.OverallStatus.Total;

        // 3. Top 10 States (Companies)
        var topStates = await _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.RecordType == CompanyMasterRecordType.Company && !string.IsNullOrWhiteSpace(r.State))
            .GroupBy(r => r.State!)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync(ct);

        if (topStates.Count > 0)
        {
            var statePoints = topStates
                .Select(s => new ChartCategoryPoint(s.State, s.Count, null, null, ChartAccent.Brand))
                .ToList();
            data.TopStatesChart = ChartCategorySeries.Create("Top States by Companies", MetricUnit.Count, ["CompanyMasterRecord.State"], statePoints);
        }

        // 4. Top 10 Industries (Companies)
        var topIndustries = await _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.RecordType == CompanyMasterRecordType.Company && !string.IsNullOrWhiteSpace(r.IndustrialClassification))
            .GroupBy(r => r.IndustrialClassification!)
            .Select(g => new { Industry = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync(ct);

        if (topIndustries.Count > 0)
        {
            var indPoints = topIndustries
                .Select(i => new ChartCategoryPoint(Truncate(i.Industry, 25), i.Count, null, null, ChartAccent.Brand))
                .ToList();
            data.TopIndustriesChart = ChartCategorySeries.Create("Top Industries", MetricUnit.Count, ["CompanyMasterRecord.IndustrialClassification"], indPoints);
        }

        // 5. Foreign Countries (Foreign entities)
        var topCountries = await _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.RecordType == CompanyMasterRecordType.Foreign && !string.IsNullOrWhiteSpace(r.Country))
            .GroupBy(r => r.Country!)
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync(ct);

        if (topCountries.Count > 0)
        {
            var ctryPoints = topCountries
                .Select(c => new ChartCategoryPoint(c.Country, c.Count, null, null, ChartAccent.Warning))
                .ToList();
            data.ForeignCountriesChart = ChartCategorySeries.Create("Foreign Origin Countries", MetricUnit.Count, ["CompanyMasterRecord.Country"], ctryPoints);
        }

        // 6. Registration Years (Companies & LLPs - safe incorporation period only)
        var companyYearData = await _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.RecordType == CompanyMasterRecordType.Company && r.RegistrationDate != null && r.RegistrationDate.Value.Year >= 2000)
            .GroupBy(r => r.RegistrationDate!.Value.Year)
            .Select(g => new { Year = g.Key, Count = g.Count() })
            .OrderBy(x => x.Year)
            .ToListAsync(ct);

        if (companyYearData.Count > 0)
        {
            var points = companyYearData
                .Select(y => new ChartTimePoint(ChartPeriod.ForDate(new DateOnly(y.Year, 1, 1), y.Year.ToString(CultureInfo.InvariantCulture)), y.Count))
                .ToList();
            data.CompanyRegistrationYearSeries = ChartSeries.Create("Company Incorporations by Year", MetricUnit.Count, ["CompanyMasterRecord.RegistrationDate"], points);
        }

        // 7. Company Class Breakdown
        var classGroups = await _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.RecordType == CompanyMasterRecordType.Company && !string.IsNullOrWhiteSpace(r.Class))
            .GroupBy(r => r.Class!)
            .Select(g => new { Class = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        long totalCo = Math.Max(1, data.CompanyStatus.Total);
        data.CompanyClasses = classGroups
            .Select(g => (g.Class, g.Count, (double)g.Count / totalCo * 100.0))
            .ToList();

        // 8. Listing Status Split
        var listingGroups = await _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.RecordType == CompanyMasterRecordType.Company && !string.IsNullOrWhiteSpace(r.ListingStatus))
            .GroupBy(r => r.ListingStatus!)
            .Select(g => new { Listing = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        data.ListingStatusSplit = listingGroups
            .Select(g => (g.Listing, g.Count, (double)g.Count / totalCo * 100.0))
            .ToList();

        // 9. Capital Distribution (Authorized Capital)
        var capitalGroups = await _db.CompanyMasterRecords
            .AsNoTracking()
            .Where(r => r.RecordType == CompanyMasterRecordType.Company && r.AuthorizedCapital != null)
            .GroupBy(r => r.AuthorizedCapital < 100000 ? "< ₹1 Lakh"
                        : r.AuthorizedCapital < 1000000 ? "₹1L – ₹10L"
                        : r.AuthorizedCapital < 10000000 ? "₹10L – ₹1 Crore"
                        : r.AuthorizedCapital < 100000000 ? "₹1Cr – ₹10 Crore"
                        : r.AuthorizedCapital < 1000000000 ? "₹10Cr – ₹100 Crore"
                        : "> ₹100 Crore")
            .Select(g => new { Range = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var capitalSortOrder = new List<string> { "< ₹1 Lakh", "₹1L – ₹10L", "₹10L – ₹1 Crore", "₹1Cr – ₹10 Crore", "₹10Cr – ₹100 Crore", "> ₹100 Crore" };
        data.CapitalDistribution = capitalSortOrder
            .Select(range =>
            {
                int count = capitalGroups.FirstOrDefault(c => c.Range == range)?.Count ?? 0;
                return (range, count, (double)count / totalCo * 100.0);
            })
            .ToList();

        return data;
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
