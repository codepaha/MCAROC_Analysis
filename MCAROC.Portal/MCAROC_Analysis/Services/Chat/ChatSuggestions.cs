using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>
/// The questions the chat panel suggests for one request (#499): each only when the request holds the data that answers it, so a suggestion never leads
/// to "insufficient evidence" (the panel used to offer the same five to every company, EPFO included where there was none). Ordered by what a lender
/// usually asks first — charges and property, then identity, litigation, management, financials — at most <see cref="MaxSuggestions"/>.
/// Document requests among them ("Show the charge registration certificates") are answered from the document index, with no AI call.
/// </summary>
public sealed class ChatSuggestions(AppDbContext db)
{
    public const int MaxSuggestions = 6;

    public async Task<IReadOnlyList<string>> BuildAsync(long requestId, long? ingestionRunId, CancellationToken ct)
    {
        var s = new List<string>();
        var run = ingestionRunId ?? -1;

        var charges = await db.RocCharges.AsNoTracking().Where(c => c.RequestId == requestId && c.IngestionRunId == run)
            .GroupBy(_ => 1).Select(g => new { Open = g.Count(c => c.SatisfactionDate == null), All = g.Count() }).FirstOrDefaultAsync(ct);
        var types = await DocumentTypesAsync(requestId, ct);

        if (charges is { Open: > 0 }) s.Add("Which charges are still open, and with which lenders?");
        if (types.Contains("Certificate of Registration of Charge")) s.Add("Show the charge registration certificates");
        else if (types.Contains("Instrument of Charge") || types.Contains("Charge Instrument")) s.Add("Show the instruments of charge");

        if (await db.CompanyNameHistories.AsNoTracking().AnyAsync(h => h.RequestId == requestId && h.IngestionRunId == run, ct))
            s.Add("When did the company change its name, and what was it called before?");

        var shownCases = db.LitigationCases.AsNoTracking().Where(c => c.RequestId == requestId && c.MergedIntoCaseId == null).Where(LitigationAttribution.IsShown);
        if (await shownCases.AnyAsync(ct))
            s.Add(await shownCases.AnyAsync(c => c.CaseStatus != null && c.CaseStatus.ToUpper().Contains("PENDING"), ct)
                ? "Which pending cases are against the company, and in which courts?"
                : "Summarise the litigation involving the company.");

        if (await db.Directors.AsNoTracking().AnyAsync(d => d.RequestId == requestId && d.IngestionRunId == run, ct))
            s.Add(await db.Directors.AsNoTracking().AnyAsync(d => d.RequestId == requestId && d.IngestionRunId == run && d.CessationDate != null, ct)
                ? "Who are the current directors, and who resigned recently?"
                : "Who are the current directors?");

        if (await db.FinancialYearData.AsNoTracking().CountAsync(f => f.RequestId == requestId && f.IngestionRunId == run, ct) >= 2)
            s.Add("How have revenue, profit and borrowings changed over the last three years?");

        if (types.Contains("Board Resolution")) s.Add("Show the board resolutions");
        if (types.Contains("MoA")) s.Add("What is the company's main object as per the MoA?");
        if (await db.EpfoContributions.AsNoTracking().AnyAsync(e => e.RequestId == requestId && e.IngestionRunId == run, ct))
            s.Add("Are there any delayed EPFO payments?");

        return s.Take(MaxSuggestions).ToList();
    }

    /// <summary>The document types present in the request's authoritative filing batch.</summary>
    private async Task<HashSet<string>> DocumentTypesAsync(long requestId, CancellationToken ct)
    {
        var batch = await McaFilingBatchResolver.GetAuthoritativeBatchAsync(db, requestId, ct);
        if (batch is null) return [];
        return (await db.McaFilingDocuments.AsNoTracking()
                .Where(d => d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null && d.RetiredUtc == null && d.FormType != null)
                .Select(d => d.FormType!).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
    }
}
