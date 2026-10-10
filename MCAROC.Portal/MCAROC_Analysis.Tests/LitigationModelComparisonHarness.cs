using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AiUsage;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Dev-only: runs the real case-analysis prompt on a sample of already-analysed cases with several models and writes the validated
/// results next to the stored baseline. Does nothing unless MCAROC_COMPARE_KEY (a service-account file) is set; it spends real money.
/// Env: MCAROC_COMPARE_KEY, MCAROC_COMPARE_OUT (result file), MCAROC_COMPARE_N (sample size, default 60), MCAROC_COMPARE_MODELS (comma list),
/// MCAROC_COMPARE_REQUEST (request id, default 4), MCAROC_COMPARE_CONN (connection string).</summary>
public class LitigationModelComparisonHarness
{
    private static string? Env(string name) => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [Fact]
    public async Task Compare_case_analysis_models()
    {
        var keyPath = Env("MCAROC_COMPARE_KEY");
        if (keyPath is null) return;   // not asked for: a normal test run skips this
        var outPath = Env("MCAROC_COMPARE_OUT") ?? Path.Combine(Path.GetTempPath(), "model-comparison.json");
        var sample = int.Parse(Env("MCAROC_COMPARE_N") ?? "60");
        var requestId = long.Parse(Env("MCAROC_COMPARE_REQUEST") ?? "4");
        var models = (Env("MCAROC_COMPARE_MODELS") ?? "gemini-3.5-flash-lite,gemini-3.1-flash-lite").Split(',');
        var conn = Env("MCAROC_COMPARE_CONN") ?? "Server=.\\SQLEXPRESS;Database=MCAROC_Analysis;Trusted_Connection=True;TrustServerCertificate=True;";

        var projectId = JsonDocument.Parse(System.IO.File.ReadAllText(keyPath)).RootElement.GetProperty("project_id").GetString()!;
        var credential = GoogleCredential.FromFile(keyPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        var gemini = new GenAiClient(vertexAI: true, project: projectId, location: "global", credential: credential,
            httpOptions: new HttpOptions { Timeout = 600_000 });

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(conn).Options;
        await using var db = new AppDbContext(options);

        // Context exactly as the orchestrator loads it.
        var companyName = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.CompanyName).SingleAsync();
        var names = new List<string> { companyName };
        foreach (var json in await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == requestId).Select(j => j.KeywordsJson).ToListAsync())
            foreach (var k in LitigationKeyword.ParseJson(json))
                if (!names.Contains(k.Value, StringComparer.OrdinalIgnoreCase)) names.Add(k.Value);
        var documents = await db.LitigationOrderDocuments.AsNoTracking()
            .Where(d => d.Order!.Case!.RequestId == requestId && d.ExtractedText != null && d.ExtractedText != "").ToListAsync();
        var context = new LitigationAiAnalysisOrchestrator.AnalysisContext(
            documents.GroupBy(d => d.LitigationCaseOrderId).ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.LitigationOrderDocumentId).First()),
            companyName, names, DateOnly.FromDateTime(DateTime.UtcNow));

        // Baseline: the newest completed analysis per case from the retiring model.
        var baselines = await (from c in db.LitigationCaseAiAnalyses.AsNoTracking()
                               join r in db.LitigationAiAnalysisRuns.AsNoTracking() on c.LitigationAiAnalysisRunId equals r.LitigationAiAnalysisRunId
                               where r.RequestId == requestId && r.ModelId == "gemini-2.5-flash" && c.Status == LitigationAiAnalysisItemStatus.Completed && c.AnalysisJson != null
                               select new { c.LitigationCaseId, c.AnalysisJson, c.LitigationAiAnalysisRunId }).ToListAsync();
        var baselineByCase = baselines.GroupBy(b => b.LitigationCaseId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LitigationAiAnalysisRunId).First().AnalysisJson!);

        var cases = await db.LitigationCases.AsNoTracking().Include(c => c.Orders).Where(c => c.RequestId == requestId && baselineByCase.Keys.Contains(c.LitigationCaseId)).ToListAsync();
        var prepared = new List<(LitigationCase Case, CaseAnalysisInput Input, string Baseline, string Tier, bool Restraint, bool Lis)>();
        foreach (var c in cases)
        {
            var input = context.Build(c);
            if (LitigationCaseAnalysisGroups.Plan(input, 100_000, 80_000).Count != 1 || input.CaseJson.Length > 60_000) continue;   // single-call cases only
            var b = JsonNode.Parse(baselineByCase[c.LitigationCaseId])!;
            prepared.Add((c, input, baselineByCase[c.LitigationCaseId], b["risk"]?.ToString() ?? "?",
                b["active_restraint"]?.GetValue<bool>() == true, b["lis_pendens"]?.GetValue<bool>() == true));
        }

        // Stratified sample: spread over the baseline tiers, with every restraint / lis pendens case favoured, and over short and long cases.
        var rng = new Random(20261009);
        var picked = new List<(LitigationCase Case, CaseAnalysisInput Input, string Baseline, string Tier, bool Restraint, bool Lis)>();
        foreach (var tier in prepared.GroupBy(p => p.Tier).OrderBy(g => g.Key))
        {
            var quota = Math.Max(1, sample / 4);
            var flagged = tier.Where(p => p.Restraint || p.Lis).OrderBy(_ => rng.Next()).Take(quota / 2);
            var rest = tier.Where(p => !(p.Restraint || p.Lis)).OrderBy(p => p.Input.CaseJson.Length).ToList();
            var spread = Enumerable.Range(0, Math.Min(quota, rest.Count)).Select(i => rest[i * rest.Count / Math.Min(quota, rest.Count)]);
            picked.AddRange(flagged.Concat(spread).DistinctBy(p => p.Case.LitigationCaseId));
        }
        picked = picked.DistinctBy(p => p.Case.LitigationCaseId).Take(sample).ToList();

        var pricing = new AiPricingOptions();
        var results = new List<JsonObject>();
        var gate = new SemaphoreSlim(int.Parse(Env("MCAROC_COMPARE_PAR") ?? "4"));
        var tasks = picked.SelectMany(p => models.Select(m => (p, m))).Select(async job =>
        {
            await gate.WaitAsync();
            try
            {
                var (p, model) = job;
                var prompt = LitigationCaseAnalysisPrompt.Build(p.Input.CaseJson);
                var sw = Stopwatch.StartNew();
                string raw = ""; int inTok = 0, outTok = 0; string? error = null;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    var response = await gemini.Models.GenerateContentAsync(model, prompt, new GenerateContentConfig { ResponseMimeType = "application/json" }, cts.Token);
                    raw = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "";
                    inTok = response.UsageMetadata?.PromptTokenCount ?? 0;
                    outTok = (response.UsageMetadata?.CandidatesTokenCount ?? 0) + (response.UsageMetadata?.ThoughtsTokenCount ?? 0);
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message[..Math.Min(200, ex.Message.Length)]; }
                sw.Stop();

                var baseline = context.Baseline(p.Case);
                var valid = error is null ? LitigationCaseAnalysisValidator.Validate(raw, p.Input, baseline.Tier, baseline.Trigger)
                    : new CaseAnalysisValidation(false, null, error, []);
                var analysis = valid.AnalysisJson is null ? null : JsonNode.Parse(valid.AnalysisJson);
                var notes = (analysis?["validation_notes"] as JsonArray)?.Select(n => n?.ToString() ?? "").ToList() ?? [];
                var cost = AiCostCalculator.CostUsd(new AiUsageRecord { Model = model, InputTokens = inTok, OutputTokens = outTok }, pricing);
                var row = new JsonObject
                {
                    ["case_id"] = p.Case.LitigationCaseId, ["model"] = model, ["accepted"] = valid.IsAccepted, ["reject_reason"] = valid.RejectReason ?? error,
                    ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1), ["input_tokens"] = inTok, ["output_tokens"] = outTok, ["cost_usd"] = (double?)cost,
                    ["risk"] = analysis?["risk"]?.ToString(), ["restraint"] = analysis?["active_restraint"]?.GetValue<bool>(),
                    ["lis"] = analysis?["lis_pendens"]?.GetValue<bool>(), ["direction"] = analysis?["legal_exposure_direction"]?.ToString(),
                    ["nature"] = analysis?["case_nature"]?.ToString(),
                    ["evidence_kept"] = (analysis?["evidence"] as JsonArray)?.Count ?? 0,
                    ["evidence_dropped"] = notes.Count(n => n.StartsWith("evidence[", StringComparison.Ordinal) && n.Contains("dropped", StringComparison.Ordinal)),
                    ["figure_flags"] = (analysis?["unknowns"] as JsonArray)?.Count(u => u?.ToString().Contains("not confirmed by the printed", StringComparison.Ordinal) == true) ?? 0,
                    ["summary"] = analysis?["summary"]?.ToString(),
                    ["baseline_tier"] = p.Tier, ["baseline_restraint"] = p.Restraint, ["baseline_lis"] = p.Lis,
                    ["record_baseline_tier"] = baseline.Tier.ToString(),
                    ["case_chars"] = p.Input.CaseJson.Length, ["orders"] = p.Input.Orders.Count
                };
                var b = JsonNode.Parse(p.Baseline)!;
                row["baseline_direction"] = b["legal_exposure_direction"]?.ToString();
                row["baseline_nature"] = b["case_nature"]?.ToString();
                row["baseline_evidence"] = (b["evidence"] as JsonArray)?.Count ?? 0;
                row["baseline_summary"] = b["summary"]?.ToString();
                lock (results) results.Add(row);
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);

        System.IO.File.WriteAllText(outPath, new JsonArray(results.OrderBy(r => (long)r["case_id"]!).ThenBy(r => r["model"]!.ToString()).Cast<JsonNode>().ToArray())
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotEmpty(results);
    }

    /// <summary>Dev-only, no model calls: how the priority score splits a request's cases. Set MCAROC_PRIORITY_OUT to run.</summary>
    [Fact]
    public async Task Show_case_priority_split()
    {
        var outPath = Env("MCAROC_PRIORITY_OUT");
        if (outPath is null) return;
        var requestId = long.Parse(Env("MCAROC_COMPARE_REQUEST") ?? "4");
        var conn = Env("MCAROC_COMPARE_CONN") ?? "Server=.\\SQLEXPRESS;Database=MCAROC_Analysis;Trusted_Connection=True;TrustServerCertificate=True;";
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(conn).Options);
        var company = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.CompanyName).SingleAsync();
        var names = new List<string> { company };
        var context = new LitigationAiAnalysisOrchestrator.AnalysisContext(new Dictionary<long, LitigationOrderDocument>(), company, names, DateOnly.FromDateTime(DateTime.UtcNow));
        var cases = await db.LitigationCases.AsNoTracking().Where(c => c.RequestId == requestId).ToListAsync();
        var rows = cases.Select(c => (Case: c, P: context.Priority(c))).ToList();
        var lines = new List<string> { $"cases={rows.Count}" };
        foreach (var g in rows.GroupBy(r => r.P.Priority).OrderByDescending(g => g.Key)) lines.Add($"{g.Key}: {g.Count()}");
        foreach (var g in rows.GroupBy(r => r.P.Reasons.Split(';')[0]).OrderByDescending(g => g.Count())) lines.Add($"side '{g.Key}': {g.Count()} (A {g.Count(x => x.P.Priority == LitigationAnalysisPriority.A)})");
        lines.Add("-- sample A reasons --");
        lines.AddRange(rows.Where(r => r.P.Priority == LitigationAnalysisPriority.A).GroupBy(r => r.P.Reasons).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Count()} x {g.Key}"));
        lines.Add("-- sample B reasons --");
        lines.AddRange(rows.Where(r => r.P.Priority == LitigationAnalysisPriority.B).GroupBy(r => r.P.Reasons).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Count()} x {g.Key}"));
        System.IO.File.WriteAllLines(outPath, lines);
        Assert.NotEmpty(rows);
    }
}
