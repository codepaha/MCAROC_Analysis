using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.PreLogin;

public sealed record PreLoginParityReport(
    long JobId,
    int LegacyCount,
    int ModuleCount,
    IReadOnlyList<string> Discrepancies)
{
    public bool IsClean => Discrepancies.Count == 0;
}

/// <summary>
/// Parity checker for pre-login assignments (#446): compares the legacy DataJson case list
/// against the module scope cases, asserting zero unexplained differences.
/// </summary>
public static class PreLoginModuleParity
{
    public static async Task<PreLoginParityReport> CheckAsync(AppDbContext db, long jobId, CancellationToken ct = default)
    {
        var discrepancies = new List<string>();

        var job = await db.PreLoginReportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.PreLoginReportJobId == jobId, ct);

        if (job is null)
        {
            discrepancies.Add($"Job {jobId} not found.");
            return new PreLoginParityReport(jobId, 0, 0, discrepancies);
        }

        InstaReportData? data = null;
        if (!string.IsNullOrWhiteSpace(job.DataJson))
        {
            try { data = JsonSerializer.Deserialize<InstaReportData>(job.DataJson); }
            catch (JsonException ex) { discrepancies.Add($"Failed to parse DataJson: {ex.Message}"); }
        }

        var legacyCases = data?.LegalCases?.Cases ?? [];

        var scope = await db.Set<ModuleScope>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == jobId, ct);

        if (scope is null)
        {
            if (legacyCases.Count > 0)
            {
                discrepancies.Add($"ModuleScope does not exist for job {jobId} but legacy has {legacyCases.Count} cases.");
            }
            return new PreLoginParityReport(jobId, legacyCases.Count, 0, discrepancies);
        }

        var scopeCases = await db.Set<ModuleScopeCase>().AsNoTracking()
            .Where(sc => sc.ScopeId == scope.ScopeId)
            .ToListAsync(ct);

        if (legacyCases.Count != scopeCases.Count)
        {
            discrepancies.Add($"Case count mismatch: legacy has {legacyCases.Count}, module has {scopeCases.Count}.");
        }

        var moduleByAlias = scopeCases.ToDictionary(sc => sc.Alias, StringComparer.OrdinalIgnoreCase);

        foreach (var c in legacyCases)
        {
            if (string.IsNullOrWhiteSpace(c.CaseRowId)) continue;

            if (!moduleByAlias.TryGetValue(c.CaseRowId, out var sc))
            {
                discrepancies.Add($"Case {c.CaseRowId} is in legacy DataJson but missing from module scope.");
                continue;
            }

            if (sc.IsExcluded != c.IsExcludedFromReport)
            {
                discrepancies.Add($"Case {c.CaseRowId} exclusion mismatch: legacy={c.IsExcludedFromReport}, module={sc.IsExcluded}.");
            }

            if (!string.Equals(sc.ExclusionReason ?? string.Empty, c.ExclusionReason ?? string.Empty, StringComparison.Ordinal))
            {
                discrepancies.Add($"Case {c.CaseRowId} exclusion reason mismatch: legacy='{c.ExclusionReason}', module='{sc.ExclusionReason}'.");
            }

            if (!string.Equals(sc.AnalystNote ?? string.Empty, c.AnalystNote ?? string.Empty, StringComparison.Ordinal))
            {
                discrepancies.Add($"Case {c.CaseRowId} analyst note mismatch: legacy='{c.AnalystNote}', module='{sc.AnalystNote}'.");
            }

            var legacyInLatestFeed = !c.AnalystNote?.Contains("(not in latest feed)", StringComparison.OrdinalIgnoreCase) ?? true;
            var moduleInLatestFeed = sc.FeedLastSeenUtc != null && (!sc.AnalystNote?.Contains("(not in latest feed)", StringComparison.OrdinalIgnoreCase) ?? true);
            if (moduleInLatestFeed != legacyInLatestFeed)
            {
                discrepancies.Add($"Case {c.CaseRowId} InLatestFeed mismatch: legacy={legacyInLatestFeed}, module={moduleInLatestFeed}.");
            }
        }

        var legacyRowIds = legacyCases.Where(c => !string.IsNullOrWhiteSpace(c.CaseRowId))
            .Select(c => c.CaseRowId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sc in scopeCases)
        {
            if (!legacyRowIds.Contains(sc.Alias))
            {
                discrepancies.Add($"Case {sc.Alias} is in module scope but missing from legacy DataJson.");
            }
        }

        return new PreLoginParityReport(jobId, legacyCases.Count, scopeCases.Count, discrepancies);
    }
}
