using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>
/// The questions the chat panel suggests for one request (#499): each only when the request holds the data that answers it, so a suggestion never leads
/// to "insufficient evidence" (the panel used to offer the same five to every company, EPFO included where there was none). Ordered by what a lender
/// usually asks first — charges and property, then identity, litigation, management, financials — at most <see cref="MaxSuggestions"/>.
/// Document requests among them ("Show the charge registration certificates") are answered from the document index, with no AI call.
/// Remaining places are filled from the question repository: what many clients ask and usually get answered with evidence.
/// </summary>
public sealed class ChatSuggestions(AppDbContext db, QuestionRepositoryOptions? repository = null)
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

        var shownCases = await db.LitigationCases.AsNoTracking().Where(c => c.RequestId == requestId && c.MergedIntoCaseId == null).Where(LitigationAttribution.IsShown)
            .Select(c => new { c.CaseStatus, c.CaseStage, c.Direction, c.PetitionersJson, c.RespondentsJson }).ToListAsync(ct);
        if (shownCases.Count > 0)
        {
            // Which side the company is on, as the litigation report decides it (review of #508): "against the company" only when a pending case is.
            var names = await CompanyNamesAsync(requestId, run, ct);
            var pending = shownCases.Where(c => LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage) == LitigationCaseStatusBucket.Pending).ToList();
            if (pending.Any(c => LitigationCasePresentation.Side(c.Direction, c.PetitionersJson, c.RespondentsJson, names) == LitigationCompanySide.AgainstCompany))
                s.Add("Which pending cases are against the company, and in which courts?");
            else if (pending.Count > 0)
                s.Add("Which cases are still pending, and in which courts?");
            else
                s.Add("Summarise the litigation involving the company.");
        }

        if (await db.Directors.AsNoTracking().AnyAsync(d => d.RequestId == requestId && d.IngestionRunId == run, ct))
            s.Add(await db.Directors.AsNoTracking().AnyAsync(d => d.RequestId == requestId && d.IngestionRunId == run && d.CessationDate != null, ct)
                ? "Who are the current directors, and who resigned recently?"
                : "Who are the current directors?");

        // Distinct years, not rows: a year can have a standalone and a consolidated row (review of #508).
        var years = await db.FinancialYearData.AsNoTracking().Where(f => f.RequestId == requestId && f.IngestionRunId == run)
            .Select(f => f.FinancialYear).Distinct().CountAsync(ct);
        if (years >= 3) s.Add("How have revenue, profit and borrowings changed over the last three years?");
        else if (years == 2) s.Add("How did revenue, profit and borrowings change between the last two years?");

        if (types.Contains("Board Resolution")) s.Add("Show the board resolutions");
        if (types.Contains("MoA")) s.Add("What is the company's main object as per the MoA?");
        // Only when a payment was actually late, by the same test as the EPFO rules (paid after the due date) (review of #508).
        if (await db.EpfoContributions.AsNoTracking().AnyAsync(e => e.RequestId == requestId && e.IngestionRunId == run
                && e.PaymentDueDate != null && e.PaymentDate != null && e.PaymentDate > e.PaymentDueDate, ct))
            s.Add("Which EPFO payments were paid late, and by how many days?");

        // Cross-client questions (#499) only fill places the request's own data left, and only for a request with some data (an empty one gets none).
        if (repository is not null && s.Count is > 0 and < MaxSuggestions)
        {
            var seen = new HashSet<string>(s, StringComparer.OrdinalIgnoreCase);
            foreach (var q in await QuestionRepositoryService.FrequentQuestionsAsync(db, repository, MaxSuggestions, ct))
                if (s.Count < MaxSuggestions && seen.Add(q)) s.Add(q);
        }

        return s.Take(MaxSuggestions).ToList();
    }

    /// <summary>The company's current and former names, which decide its side in a case.</summary>
    private Task<List<string>> CompanyNamesAsync(long requestId, long run, CancellationToken ct) =>
        LitigationCompanyNames.ForRequestAsync(db, requestId, ct);

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
