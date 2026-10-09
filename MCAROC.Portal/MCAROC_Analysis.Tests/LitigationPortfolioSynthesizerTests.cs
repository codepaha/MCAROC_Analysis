using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Valid answers to any of the three portfolio prompts (the single prompt, one batch of cases, or the combine step), built from the prompt's own ids.</summary>
internal static class PortfolioAnswers
{
    public static string? For(string prompt)
    {
        var reduce = prompt.Contains("combining summaries", StringComparison.Ordinal);
        var batch = prompt.Contains("small batch", StringComparison.Ordinal);
        if (!reduce && !batch && !prompt.Contains("portfolio-level", StringComparison.Ordinal)) return null;
        var ids = reduce
            ? Regex.Matches(prompt[prompt.IndexOf("Batch summaries:", StringComparison.Ordinal)..], @"""caseAnalysisIds"":\[([\d,]+)\]").SelectMany(m => m.Groups[1].Value.Split(','))
            : Regex.Matches(prompt, @"""LitigationCaseAiAnalysisId"":(\d+)").Select(m => m.Groups[1].Value);
        return "{\"status\":\"Completed\",\"summary\":\"Portfolio.\",\"unknowns\":[],\"caseAnalysisIds\":[" + string.Join(",", ids.Distinct()) + "]}";
    }
}

public class LitigationPortfolioSynthesizerTests
{
    private static string AnalysisFor(int i) => new JsonObject
    {
        ["status"] = "Completed", ["prompt_version"] = "2.1", ["case_key"] = (100 + i).ToString(), ["risk"] = i % 4 == 0 ? "R3" : "R1",
        ["summary"] = $"Case {i}.",
        ["evidence"] = new JsonArray(new JsonObject { ["claim"] = "outcome", ["order_id"] = 1, ["quote"] = "The petition is dismissed.", ["page"] = 1 }),
        ["outcome"] = new JsonObject { ["final_order_summary"] = "Dismissed." }
    }.ToJsonString();

    private static List<LitigationCaseAiAnalysis> Rows(int count) => Enumerable.Range(1, count).Select(i => new LitigationCaseAiAnalysis
    {
        LitigationCaseAiAnalysisId = i, LitigationCaseId = 100 + i, Status = LitigationAiAnalysisItemStatus.Completed, AnalysisJson = AnalysisFor(i)
    }).ToList();

    /// <summary>Answers batch prompts with a summary citing the ids they carry, and combine prompts with a summary citing the ids of the summaries they carry.</summary>
    private sealed class FakeClient(Func<string, int, LitigationAiCallResult?>? intercept = null) : ILitigationAiAnalysisClient
    {
        public int BatchCalls, CombineCalls;
        private int _total;

        public Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct, string? modelId = null)
        {
            var isBatch = prompt.Contains("small batch", StringComparison.Ordinal);
            if (isBatch) Interlocked.Increment(ref BatchCalls); else Interlocked.Increment(ref CombineCalls);
            if (intercept?.Invoke(prompt, Interlocked.Increment(ref _total)) is { } custom) return Task.FromResult(custom);
            var ids = isBatch
                ? Regex.Matches(prompt, @"""LitigationCaseAiAnalysisId"":(\d+)").Select(m => m.Groups[1].Value)
                : Regex.Matches(prompt[prompt.IndexOf("Batch summaries:", StringComparison.Ordinal)..], @"""caseAnalysisIds"":\[([\d,]+)\]").SelectMany(m => m.Groups[1].Value.Split(','));
            return Task.FromResult(new LitigationAiCallResult(true,
                "{\"status\":\"Completed\",\"summary\":\"Synthesis.\",\"unknowns\":[],\"caseAnalysisIds\":[" + string.Join(",", ids.Distinct()) + "]}", null));
        }
    }


    private static LitigationPortfolioSynthesizer Synth(ILitigationAiAnalysisClient client, int batch = 6, int fanIn = 20) =>
        new(client, new LitigationAiAnalysisOptions { PortfolioBatchSize = batch, PortfolioFanIn = fanIn, CaseConcurrency = 2 });

    [Fact]
    public async Task Fourteen_cases_are_read_six_at_a_time_then_combined()
    {
        var client = new FakeClient();
        var result = await Synth(client).RunAsync(Rows(14), totalCases: 14, () => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, client.BatchCalls);      // 6 + 6 + 2
        Assert.Equal(1, client.CombineCalls);
        var coverage = JsonNode.Parse(result.Evidence)!["coverage"]!;
        Assert.Equal(14, coverage["casesAnalysed"]!.GetValue<int>());
        Assert.Equal(14, coverage["casesSummarised"]!.GetValue<int>());
        Assert.Equal(0, coverage["casesNotSummarised"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_highest_risk_cases_are_read_together_first()
    {
        var prompts = new List<string>();
        var client = new FakeClient((p, _) => { if (p.Contains("small batch")) lock (prompts) prompts.Add(p); return null; });
        await Synth(client, batch: 4).RunAsync(Rows(8), 8, () => Task.CompletedTask, CancellationToken.None);
        // ids 4 and 8 are R3; they share a batch that carries the order quote too
        var withBoth = prompts.Single(p => p.Contains("\"LitigationCaseAiAnalysisId\":4,") && p.Contains("\"LitigationCaseAiAnalysisId\":8,"));
        Assert.Contains("The petition is dismissed.", withBoth);
    }

    [Fact]
    public async Task A_batch_that_cannot_be_summarised_is_disclosed_and_the_rest_still_produce_a_summary()
    {
        // the first two calls (both attempts of one batch) fail; everything else works
        var client = new FakeClient((p, n) => p.Contains("small batch") && n <= 2 ? new LitigationAiCallResult(false, "", "503") : null);
        var result = await Synth(client).RunAsync(Rows(14), 14, () => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Success);
        var coverage = JsonNode.Parse(result.Evidence)!["coverage"]!;
        Assert.True(coverage["casesNotSummarised"]!.GetValue<int>() > 0);
        Assert.Equal(14, coverage["casesAnalysed"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_malformed_batch_reply_is_asked_for_once_more()
    {
        var client = new FakeClient((p, n) => p.Contains("small batch") && n == 1 ? new LitigationAiCallResult(true, "not json", null) : null);
        var result = await Synth(client).RunAsync(Rows(7), 7, () => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, JsonNode.Parse(result.Evidence)!["coverage"]!["casesNotSummarised"]!.GetValue<int>());
    }

    [Fact]
    public async Task When_every_batch_fails_the_synthesis_fails_instead_of_inventing_one()
    {
        var client = new FakeClient((p, _) => new LitigationAiCallResult(false, "", "503"));
        var result = await Synth(client).RunAsync(Rows(14), 14, () => Task.CompletedTask, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.AnalysisJson);
        Assert.Equal(0, client.CombineCalls);
    }

    [Fact]
    public async Task A_final_summary_citing_a_case_it_was_not_given_is_rejected()
    {
        var client = new FakeClient((p, _) => p.Contains("small batch") ? null
            : new LitigationAiCallResult(true, """{"status":"Completed","summary":"x","unknowns":[],"caseAnalysisIds":[999]}""", null));
        var result = await Synth(client).RunAsync(Rows(14), 14, () => Task.CompletedTask, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Many_batches_are_combined_in_rounds()
    {
        var client = new FakeClient();
        var result = await Synth(client, batch: 2, fanIn: 3).RunAsync(Rows(20), 20, () => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(10, client.BatchCalls);
        Assert.True(client.CombineCalls > 2);   // 10 -> 4 -> 2, then the final
    }
}
