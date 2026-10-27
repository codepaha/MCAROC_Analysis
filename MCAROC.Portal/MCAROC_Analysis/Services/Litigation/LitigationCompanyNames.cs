using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>The names a request's company goes by in court records: its registered name, its former names (latest completed ingestion) and the
/// name variations a person added for the company (<see cref="CompanyNameVariation"/>). One definition for every reader that decides the
/// company's side of a case (the register, the case page, the analysis rules, the chat), so a variation counts the same everywhere.</summary>
public static class LitigationCompanyNames
{
    public static async Task<List<string>> ForRequestAsync(AppDbContext db, long requestId, CancellationToken ct)
    {
        var r = await db.Requests.AsNoTracking().Where(x => x.RequestId == requestId)
            .Select(x => new { x.CompanyName, x.Cin, x.Llpin, x.LatestCompletedIngestionRunId }).FirstOrDefaultAsync(ct);
        return r is null ? [] : await ForAsync(db, requestId, r.CompanyName, r.Cin ?? r.Llpin, r.LatestCompletedIngestionRunId, ct);
    }

    public static async Task<List<string>> ForAsync(AppDbContext db, long requestId, string? companyName, string? identifier, long? ingestionRunId,
        CancellationToken ct)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(companyName)) names.Add(companyName);
        if (ingestionRunId is { } runId)
            names.AddRange(await db.CompanyNameHistories.AsNoTracking()
                .Where(h => h.RequestId == requestId && h.IngestionRunId == runId && h.PreviousName != null && h.PreviousName != "")
                .Select(h => h.PreviousName!).ToListAsync(ct));
        names.AddRange((await ActiveVariationsAsync(db, identifier, ct)).Select(v => v.Name));
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The company's active name variations, oldest first. None without an identifier.</summary>
    public static async Task<List<CompanyNameVariation>> ActiveVariationsAsync(AppDbContext db, string? identifier, CancellationToken ct)
    {
        var id = identifier?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(id)) return [];
        return await db.CompanyNameVariations.AsNoTracking().Where(v => v.Identifier == id && v.RemovedUtc == null)
            .OrderBy(v => v.CompanyNameVariationId).ToListAsync(ct);
    }
}
