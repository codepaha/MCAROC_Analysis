using System.Text.Json;
using System.Text.Json.Nodes;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.LitigationData;

/// <param name="Evidence">What the final summary was built from: the coverage and each batch's summary. Saved with the portfolio analysis.</param>
public sealed record PortfolioSynthesisResult(bool Success, string Evidence, string PromptKey, string? RawResponse, string? AnalysisJson, string? FailureReason);

/// <summary>Synthesises a company's case analyses in small batches instead of one prompt. A batch is a handful of cases (default 6, the highest risk
/// first) that the model reads together with the verified order quotes behind each, so what it says about them is grounded in the orders and
/// not only in the one-line summaries. The batch summaries are then combined, in further rounds if there are many, into the final summary.
/// Every step is checked the same way the single prompt was: it may cite only case analyses it was given.</summary>
public sealed class LitigationPortfolioSynthesizer(ILitigationAiAnalysisClient client, LitigationAiAnalysisOptions options)
{
    private const int BatchAttempts = 2;

    private sealed record BatchSummary(int Batch, IReadOnlyList<long> Ids, string Summary, IReadOnlyList<string> Unknowns);

    public async Task<PortfolioSynthesisResult> RunAsync(IReadOnlyList<LitigationCaseAiAnalysis> rows, int totalCases, Func<Task> renew, CancellationToken ct)
    {
        var usable = rows.Where(x => x.Status == LitigationAiAnalysisItemStatus.Completed)
            .OrderByDescending(RiskRank).ThenBy(x => x.LitigationCaseAiAnalysisId).ToList();
        var batchSize = Math.Max(2, options.PortfolioBatchSize);
        var batches = usable.Chunk(batchSize).ToList();

        // Map: each batch on its own, a few at a time.
        var summaries = new BatchSummary?[batches.Count];
        var gate = new SemaphoreSlim(Math.Max(1, options.CaseConcurrency));
        await Task.WhenAll(batches.Select(async (batch, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await renew();
                summaries[i] = await SummariseBatchAsync(i + 1, batch, ct);
            }
            finally { gate.Release(); }
        }));
        await renew();

        var good = summaries.OfType<BatchSummary>().ToList();
        var failedCases = batches.Select((b, i) => (b, i)).Where(x => summaries[x.i] is null).Sum(x => x.b.Length);
        if (good.Count == 0) return Failed("No batch of case analyses could be summarised.", "[]");

        var coverage = new
        {
            totalCases, casesAnalysed = usable.Count, casesSummarised = good.Sum(b => b.Ids.Count), casesNotSummarised = failedCases,
            batchSize, note = "Cases not analysed were ranked lower by a fixed rule (the company as claimant, no financial act) and keep their court-record risk tier; they are not negative findings."
        };
        var evidence = JsonSerializer.Serialize(new { coverage, batches = good.Select(b => new { b.Batch, caseAnalysisIds = b.Ids, b.Summary, b.Unknowns }) });

        // Reduce: combine batch summaries, in rounds when there are more than fit in one prompt.
        var level = good;
        var fanIn = Math.Max(2, options.PortfolioFanIn);
        while (level.Count > fanIn)
        {
            var next = new List<BatchSummary>();
            foreach (var (group, n) in level.Chunk(fanIn).Select((g, n) => (g, n)))
            {
                await renew();
                var merged = await ReduceAsync(group, n + 1, null, ct);
                if (merged is null) return Failed("A group of batch summaries could not be combined.", evidence);
                next.Add(merged);
            }
            level = next;
        }
        await renew();
        // The final answer may cite only the cases whose summaries it was given: a batch that failed is not among them.
        var supplied = level.SelectMany(b => b.Ids).ToHashSet();
        var final = await ReduceFinalAsync(level, coverage, supplied, ct);
        if (final.Analysis is null) return Failed(final.Reason ?? "The portfolio summary could not be produced.", evidence);
        return new(true, evidence, LitigationAnalysisPromptBuilder.ComputeHash(evidence), final.Raw, final.Analysis, null);
    }

    private PortfolioSynthesisResult Failed(string reason, string evidence) => new(false, evidence, LitigationAnalysisPromptBuilder.ComputeHash(evidence), null, null, reason);

    private async Task<BatchSummary?> SummariseBatchAsync(int number, LitigationCaseAiAnalysis[] batch, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(batch.Select(x => new
        {
            x.LitigationCaseAiAnalysisId, x.LitigationCaseId, AnalysisJson = LitigationCaseAnalysisProjection.ForPortfolioBatch(x.AnalysisJson)
        }));
        var prompt = LitigationAnalysisPromptBuilder.BuildPortfolioBatchPrompt(json);
        var allowed = batch.Select(x => x.LitigationCaseAiAnalysisId).ToHashSet();
        for (var attempt = 0; attempt < BatchAttempts; attempt++)
        {
            var response = await client.CallAsync(prompt, options.TimeoutSeconds, ct);
            if (!response.Success) continue;
            var valid = LitigationAnalysisResponseValidator.ValidatePortfolio(response.RawResponse, allowed);
            if (valid.IsAccepted && Read(number, valid.AnalysisJson) is { } summary) return summary;
        }
        return null;
    }

    private async Task<BatchSummary?> ReduceAsync(IReadOnlyList<BatchSummary> items, int number, object? coverage, CancellationToken ct)
    {
        var allowed = items.SelectMany(i => i.Ids).ToHashSet();
        var prompt = LitigationAnalysisPromptBuilder.BuildPortfolioReducePrompt(Serialise(items), coverage is null ? null : JsonSerializer.Serialize(coverage));
        for (var attempt = 0; attempt < BatchAttempts; attempt++)
        {
            var response = await client.CallAsync(prompt, options.TimeoutSeconds, ct);
            if (!response.Success) continue;
            var valid = LitigationAnalysisResponseValidator.ValidatePortfolio(response.RawResponse, allowed);
            if (valid.IsAccepted && Read(number, valid.AnalysisJson) is { } merged) return merged;
        }
        return null;
    }

    private async Task<(string? Raw, string? Analysis, string? Reason)> ReduceFinalAsync(IReadOnlyList<BatchSummary> items, object coverage, ISet<long> allowedIds, CancellationToken ct)
    {
        var prompt = LitigationAnalysisPromptBuilder.BuildPortfolioReducePrompt(Serialise(items), JsonSerializer.Serialize(coverage));
        string? reason = null;
        for (var attempt = 0; attempt < BatchAttempts; attempt++)
        {
            var response = await client.CallAsync(prompt, options.TimeoutSeconds, ct);
            if (!response.Success) { reason = response.FailureReason; continue; }
            var valid = LitigationAnalysisResponseValidator.ValidatePortfolio(response.RawResponse, allowedIds);
            if (valid.IsAccepted) return (response.RawResponse, valid.AnalysisJson, null);
            reason = valid.RejectReason;
        }
        return (null, null, reason ?? "Portfolio AI call failed.");
    }

    private static string Serialise(IReadOnlyList<BatchSummary> items) =>
        JsonSerializer.Serialize(items.Select(i => new { i.Batch, caseAnalysisIds = i.Ids, i.Summary, i.Unknowns }));

    private static BatchSummary? Read(int number, string? analysisJson)
    {
        if (analysisJson is null) return null;
        try
        {
            var node = JsonNode.Parse(analysisJson);
            var summary = node?["summary"]?.ToString();
            if (string.IsNullOrWhiteSpace(summary)) return null;
            var ids = (node!["caseAnalysisIds"] as JsonArray)?.Select(n => n!.GetValue<long>()).ToList() ?? [];
            var unknowns = (node["unknowns"] as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList() ?? [];
            return new BatchSummary(number, ids, summary, unknowns);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>R3 first: the risk the validated analysis settled on, 0 when it has none.</summary>
    internal static int RiskRank(LitigationCaseAiAnalysis row)
    {
        try { return JsonNode.Parse(row.AnalysisJson ?? "{}")?["risk"]?.ToString() switch { "R3" => 3, "R2" => 2, "R1" => 1, _ => 0 }; }
        catch (JsonException) { return 0; }
    }
}
