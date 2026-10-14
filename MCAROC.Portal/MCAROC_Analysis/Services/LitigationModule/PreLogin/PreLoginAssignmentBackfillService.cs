using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.LitigationModule.PreLogin;

public sealed record BackfillResult(long JobId, int CasesBackfilled, bool CreatedScope, int UpdatedCases);

public sealed record BackfillSummary(int JobsProcessed, int TotalCasesBackfilled, int Errors);

/// <summary>
/// Idempotent, re-runnable backfill of existing pre-login assignments from DataJson into the litigation module scope and cases (#446).
/// Preserves stable CaseRowIds, target associations, aliases, exclusions, and analyst notes.
/// Decouples module scope Version from job.DataVersion.
/// </summary>
public sealed class PreLoginAssignmentBackfillService(AppDbContext db, ILogger<PreLoginAssignmentBackfillService>? logger = null)
{
    public async Task<BackfillResult> BackfillJobAsync(long jobId, CancellationToken ct = default)
    {
        var job = await db.PreLoginReportJobs
            .Include(j => j.LitigationTargets)
            .FirstOrDefaultAsync(j => j.PreLoginReportJobId == jobId, ct);

        if (job is null || string.IsNullOrWhiteSpace(job.DataJson))
        {
            return new BackfillResult(jobId, 0, false, 0);
        }

        InstaReportData? data = null;
        try
        {
            data = JsonSerializer.Deserialize<InstaReportData>(job.DataJson);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Could not deserialize DataJson for pre-login job {JobId}", jobId);
            return new BackfillResult(jobId, 0, false, 0);
        }

        if (data is null)
        {
            return new BackfillResult(jobId, 0, false, 0);
        }

        var companyName = job.SubmittedCompanyName ?? data.Company?.Name ?? job.Cin ?? $"Job-{jobId}";

        var existingScope = await db.Set<ModuleScope>().Include(s => s.Subject)
            .FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == jobId, ct);

        bool createdScope = false;
        if (existingScope is null)
        {
            var subject = new ModuleSubject
            {
                Kind = ModuleSubjectKind.Company,
                Name = companyName,
                CreatedUtc = job.CreatedUtc
            };
            db.Set<ModuleSubject>().Add(subject);
            await db.SaveChangesAsync(ct);

            var maxAliasIndex = 0;
            if (data.LegalCases?.Cases is { Count: > 0 } existingList)
            {
                foreach (var c in existingList)
                {
                    if (!string.IsNullOrWhiteSpace(c.CaseRowId) &&
                        c.CaseRowId.StartsWith("C-", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(c.CaseRowId[2..], out var idx) && idx > maxAliasIndex)
                    {
                        maxAliasIndex = idx;
                    }
                }
            }

            var nextAliasIndex = Math.Max(maxAliasIndex + 1, data.LegalCases?.NextCaseRowIdIndex ?? 1);

            existingScope = new ModuleScope
            {
                Kind = ModuleScopeKind.Assignment,
                ExternalId = jobId,
                SubjectId = subject.SubjectId,
                Version = 1,
                NextAliasIndex = Math.Max(1, nextAliasIndex),
                CreatedUtc = job.CreatedUtc
            };
            db.Set<ModuleScope>().Add(existingScope);
            await db.SaveChangesAsync(ct);
            createdScope = true;
        }

        var targets = job.LitigationTargets.ToList();
        var borrowerTarget = targets.FirstOrDefault(t => t.TargetKind == PreLoginTargetKind.Borrower);
        var directorTargets = targets.Where(t => t.TargetKind == PreLoginTargetKind.Director).ToList();

        var cases = data.LegalCases?.Cases ?? [];
        var backfilledCount = 0;
        var updatedCount = 0;

        var existingScopeCases = await db.Set<ModuleScopeCase>()
            .Where(sc => sc.ScopeId == existingScope.ScopeId)
            .ToDictionaryAsync(sc => sc.Alias, ct);

        var existingModuleCases = await db.Set<ModuleCase>()
            .Where(mc => mc.LegacyJobId == jobId && mc.LegacyCaseRowId != null)
            .ToDictionaryAsync(mc => mc.LegacyCaseRowId!, ct);

        foreach (var c in cases)
        {
            if (string.IsNullOrWhiteSpace(c.CaseRowId)) continue;

            // Determine TargetId: match Director if mentioned in CaseDetails, otherwise Borrower target
            long? targetId = null;
            if (directorTargets.Count > 0 && !string.IsNullOrWhiteSpace(c.CaseDetails))
            {
                var matchedDirector = directorTargets.FirstOrDefault(dt =>
                    !string.IsNullOrWhiteSpace(dt.TargetName) &&
                    c.CaseDetails.Contains(dt.TargetName, StringComparison.OrdinalIgnoreCase));
                if (matchedDirector is not null) targetId = matchedDirector.PreLoginLitigationTargetId;
            }
            targetId ??= borrowerTarget?.PreLoginLitigationTargetId;

            if (!existingModuleCases.TryGetValue(c.CaseRowId, out var mc))
            {
                mc = new ModuleCase
                {
                    LegacyJobId = jobId,
                    LegacyCaseRowId = c.CaseRowId,
                    CreatedUtc = job.CreatedUtc
                };
                db.Set<ModuleCase>().Add(mc);
                await db.SaveChangesAsync(ct);
                existingModuleCases[c.CaseRowId] = mc;
            }

            int aliasIndex = 0;
            if (c.CaseRowId.StartsWith("C-", StringComparison.OrdinalIgnoreCase) && int.TryParse(c.CaseRowId[2..], out var parsedIdx))
            {
                aliasIndex = parsedIdx;
            }

            var inLatestFeed = !c.AnalystNote?.Contains("(not in latest feed)", StringComparison.OrdinalIgnoreCase) ?? true;
            var feedSeenUtc = job.CompletedUtc ?? job.CreatedUtc;

            if (!existingScopeCases.TryGetValue(c.CaseRowId, out var sc))
            {
                sc = new ModuleScopeCase
                {
                    ScopeId = existingScope.ScopeId,
                    CaseId = mc.CaseId,
                    Alias = c.CaseRowId,
                    AliasIndex = aliasIndex,
                    TargetId = targetId,
                    FeedLastSeenUtc = inLatestFeed ? feedSeenUtc : null,
                    IsHidden = false,
                    IsExcluded = c.IsExcludedFromReport,
                    ExclusionReason = c.ExclusionReason,
                    AnalystNote = c.AnalystNote,
                    IncludedInReport = !c.IsExcludedFromReport,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                db.Set<ModuleScopeCase>().Add(sc);
                existingScopeCases[c.CaseRowId] = sc;
                backfilledCount++;
            }
            else
            {
                bool modified = false;
                if (sc.TargetId == null && targetId != null)
                {
                    sc.TargetId = targetId;
                    modified = true;
                }
                if (modified)
                {
                    sc.UpdatedUtc = DateTime.UtcNow;
                    updatedCount++;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return new BackfillResult(jobId, backfilledCount, createdScope, updatedCount);
    }

    public async Task<BackfillSummary> BackfillAllAsync(CancellationToken ct = default)
    {
        var jobIds = await db.PreLoginReportJobs.AsNoTracking()
            .Where(j => j.DataJson != null && j.DataJson != "")
            .Select(j => j.PreLoginReportJobId)
            .ToListAsync(ct);

        int totalCases = 0;
        int errors = 0;

        foreach (var jId in jobIds)
        {
            try
            {
                var r = await BackfillJobAsync(jId, ct);
                totalCases += r.CasesBackfilled;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed backfilling pre-login job {JobId}", jId);
                errors++;
            }
        }

        return new BackfillSummary(jobIds.Count, totalCases, errors);
    }
}
