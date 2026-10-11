using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>What makes a purpose's spend grow: the filings read, the court orders read, the cases analysed, the questions asked, or nothing about size
/// (a fixed cost per assignment).</summary>
public enum AiCostDriver { FilingDocument, Order, Case, Question, Assignment }

public sealed record AiCostLine(
    string Purpose, AiCostDriver Driver, int Calls, long InputTokens, long OutputTokens, decimal CostUsd, bool HasUnpriced, bool HasEstimatedTokens = false);

/// <summary>Whether the usage log is complete: rows still waiting to be saved, and rows given up on since the app started (the totals may be low by their cost).</summary>
public sealed record AiUsageRecordingStatus(int Pending, long Failed)
{
    public bool Incomplete => Failed > 0;
}

/// <summary>How big an assignment is, in the units its cost scales with.</summary>
public sealed record AiCostVolumes(int FilingDocuments, int Orders, int Cases, int Questions = 0);

/// <summary>Dollars per filing document, per order, per case and per Copilot question, plus what an assignment costs whatever its size.
/// <see cref="Recorded"/> names the drivers whose rate comes from recorded usage; the others are the built-in estimates, used until usage is recorded.</summary>
public sealed record AiUnitRates(
    decimal PerFilingDocument, decimal PerOrder, decimal PerCase, decimal PerQuestion, decimal Fixed, IReadOnlySet<AiCostDriver> Recorded);

public sealed record AiCostReport(
    long RequestId, IReadOnlyList<AiCostLine> Lines, decimal TotalUsd, decimal TotalInr, bool HasUnpriced, bool HasUsage,
    AiCostVolumes Volumes, AiUnitRates Rates, AiUsageRecordingStatus Recording);

public sealed record AiCostEstimate(decimal FilingDocumentsUsd, decimal OrdersUsd, decimal CasesUsd, decimal QuestionsUsd, decimal FixedUsd)
{
    public decimal TotalUsd => FilingDocumentsUsd + OrdersUsd + CasesUsd + QuestionsUsd + FixedUsd;
}

/// <summary>Reads recorded model usage back as a cost: per purpose for one assignment, and as per-unit rates that estimate a new assignment from how many
/// documents, cases and questions it has.</summary>
public sealed class AiCostReportService(AppDbContext db, IOptions<AiPricingOptions> pricing, AiUsageQueue? queue = null)
{
    /// <summary>Per-unit rates used for a driver until usage has been recorded for it: estimates from a 645-document, 3,499-order, 698-case company priced at
    /// list rates, and from the Copilot calls measured on it (about three model calls for two questions).</summary>
    public static readonly AiUnitRates DefaultRates = new(
        PerFilingDocument: 0.0022m, PerOrder: 0.0005m, PerCase: 0.008m, PerQuestion: 0.015m, Fixed: 0.35m, Recorded: new HashSet<AiCostDriver>());

    public static AiCostDriver DriverOf(string purpose) => purpose switch
    {
        AiUsagePurposes.FilingExtraction or AiUsagePurposes.EmbeddingFilings => AiCostDriver.FilingDocument,
        AiUsagePurposes.EmbeddingOrders or AiUsagePurposes.OrderClassification => AiCostDriver.Order,
        AiUsagePurposes.CaseAnalysis => AiCostDriver.Case,
        AiUsagePurposes.Copilot => AiCostDriver.Question,
        _ => AiCostDriver.Assignment
    };

    public async Task<AiCostReport> GetAsync(long requestId, CancellationToken ct)
    {
        var records = await db.AiUsageRecords.AsNoTracking().Where(r => r.RequestId == requestId).ToListAsync(ct);
        var lines = Summarize(records);
        var volumes = await VolumesAsync(requestId, ct);
        var total = lines.Sum(l => l.CostUsd);
        // Rates shown beside the assignment are the pooled ones (more data than any single request), not this request's own
        return new AiCostReport(requestId, lines, total, total * pricing.Value.UsdToInr, lines.Any(l => l.HasUnpriced), records.Count > 0, volumes, await UnitRatesAsync(ct),
            new AiUsageRecordingStatus(queue?.Pending ?? 0, queue?.Failed ?? 0));
    }

    public IReadOnlyList<AiCostLine> Summarize(IEnumerable<AiUsageRecord> records) =>
        records.GroupBy(r => r.Purpose).Select(g =>
        {
            var priced = g.Select(r => AiCostCalculator.CostUsd(r, pricing.Value)).ToList();
            return new AiCostLine(g.Key, DriverOf(g.Key), g.Count(), g.Sum(r => (long)r.InputTokens), g.Sum(r => (long)r.OutputTokens + r.ThoughtTokens),
                priced.Sum(c => c ?? 0m), priced.Any(c => c is null), g.Any(r => r.TokensEstimated));
        }).OrderByDescending(l => l.CostUsd).ToList();

    private async Task<AiCostVolumes> VolumesAsync(long requestId, CancellationToken ct) => new(
        await db.McaFilingDocuments.AsNoTracking().CountAsync(d => d.RequestId == requestId && d.DuplicateOfDocumentId == null, ct),
        await db.LitigationOrderDocuments.AsNoTracking().CountAsync(d => d.Order!.Case!.RequestId == requestId && d.ExtractedText != null, ct),
        await db.LitigationCases.AsNoTracking().CountAsync(c => c.RequestId == requestId && c.MergedIntoCaseId == null, ct),
        await db.ChatMessages.AsNoTracking().CountAsync(m => m.Role == ChatRole.User && db.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId), ct));

    /// <summary>Per-unit rates across every assignment that has recorded usage: the total cost of a driver divided by the total units of that driver, counting
    /// only assignments that have the unit (a request with no litigation does not dilute the per-case rate). A driver with no recorded spend keeps its
    /// built-in estimate, so a few Copilot questions alone never turn the per-document rate into zero.</summary>
    public async Task<AiUnitRates> UnitRatesAsync(CancellationToken ct)
    {
        var all = await db.AiUsageRecords.AsNoTracking().Where(r => r.RequestId != null).ToListAsync(ct);
        if (all.Count == 0) return DefaultRates;

        var cost = new Dictionary<AiCostDriver, decimal>();
        var units = new Dictionary<AiCostDriver, long>();
        var requests = all.GroupBy(r => r.RequestId!.Value).ToList();
        foreach (var request in requests)
        {
            var lines = Summarize(request);
            var v = await VolumesAsync(request.Key, ct);
            // Usage is only recorded from when recording began, so questions asked before then have no spend to divide: count those since the first one recorded
            var firstCopilotCall = request.Where(r => DriverOf(r.Purpose) == AiCostDriver.Question).Select(r => (DateTime?)r.CreatedUtc).Min();
            var questions = firstCopilotCall is { } since
                ? await db.ChatMessages.AsNoTracking().CountAsync(m => m.Role == ChatRole.User && m.CreatedDate >= since.AddMinutes(-1)
                    && db.ChatSessions.Any(c => c.ChatSessionId == m.ChatSessionId && c.RequestId == request.Key), ct)
                : 0;
            void Add(AiCostDriver d, int unitCount)
            {
                var spent = lines.Where(l => l.Driver == d).Sum(l => l.CostUsd);
                if (unitCount <= 0 || spent <= 0) return;   // no spend for this driver here, or nothing to divide by
                cost[d] = cost.GetValueOrDefault(d) + spent;
                units[d] = units.GetValueOrDefault(d) + unitCount;
            }
            Add(AiCostDriver.FilingDocument, v.FilingDocuments);
            Add(AiCostDriver.Order, v.Orders);
            Add(AiCostDriver.Case, v.Cases);
            Add(AiCostDriver.Question, questions);
            Add(AiCostDriver.Assignment, 1);
        }

        var recorded = units.Keys.ToHashSet();
        decimal Rate(AiCostDriver d, decimal fallback) => recorded.Contains(d) ? cost[d] / units[d] : fallback;
        return new(
            Rate(AiCostDriver.FilingDocument, DefaultRates.PerFilingDocument), Rate(AiCostDriver.Order, DefaultRates.PerOrder),
            Rate(AiCostDriver.Case, DefaultRates.PerCase), Rate(AiCostDriver.Question, DefaultRates.PerQuestion),
            Rate(AiCostDriver.Assignment, DefaultRates.Fixed), recorded);
    }

    public static AiCostEstimate Estimate(AiCostVolumes volumes, AiUnitRates rates) => new(
        volumes.FilingDocuments * rates.PerFilingDocument, volumes.Orders * rates.PerOrder, volumes.Cases * rates.PerCase,
        volumes.Questions * rates.PerQuestion, rates.Fixed);
}
