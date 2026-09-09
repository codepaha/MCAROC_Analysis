using MCAROC_Analysis.Data;
using MCAROC_Analysis.Models.Dossier;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>One assembled <see cref="DossierModel"/> per (request, ingestion run, analysis run) — shared
/// by the PDF endpoint and (Phase 7 Track B) the company-page controller so the full dedup / threading /
/// grouping pass runs once per data change, not once per view. A re-ingest or re-analysis produces new
/// run ids, so a stale entry is simply never hit again.</summary>
public class DossierCache(AppDbContext db, DossierAssembler assembler, IMemoryCache cache)
{
    public async Task<DossierModel?> GetAsync(long requestId, CancellationToken ct = default)
    {
        var keyParts = await db.Requests
            .Where(r => r.RequestId == requestId)
            .Select(r => new
            {
                r.LatestCompletedIngestionRunId,
                AnalysisRunId = db.AnalysisRuns.Where(a => a.RequestId == requestId)
                    .OrderByDescending(a => a.RunNumber).Select(a => (long?)a.AnalysisRunId).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (keyParts?.LatestCompletedIngestionRunId is not { } ingestionRunId)
            return null;

        var key = $"dossier:{requestId}:{ingestionRunId}:{keyParts.AnalysisRunId?.ToString() ?? "-"}";
        if (cache.TryGetValue(key, out DossierModel? cached) && cached is not null)
            return cached;

        var model = await assembler.BuildAsync(requestId, ct);
        if (model is not null)
            cache.Set(key, model, new MemoryCacheEntryOptions
            {
                Size = 1,
                SlidingExpiration = TimeSpan.FromMinutes(20)
            });
        return model;
    }
}
