using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Builds bounded, provenance-addressable LIT-05 model inputs. The model receives no implied facts:
/// every order excerpt carries the persisted case/order/page/chunk identity it must cite, and a case with no
/// extracted order text is deliberately represented as insufficient evidence rather than guessed from case
/// metadata.</summary>
public static class LitigationAnalysisPromptBuilder
{
    public const string PromptVersion = "1.0";
    private const int MaxChunksPerCase = 12;
    private const int MaxCharsPerChunk = 3000;

    public static LitigationAnalysisEvidence BuildCaseEvidence(LitigationCase @case, IEnumerable<LitigationOrderChunk> chunks)
    {
        var excerpts = chunks.OrderBy(c => c.LitigationCaseOrderId).ThenBy(c => c.PageNumber).ThenBy(c => c.ChunkIndex)
            .Take(MaxChunksPerCase)
            .Select(c => new LitigationEvidenceExcerpt(c.LitigationCaseOrderId, c.LitigationOrderDocumentId,
                c.PageNumber, c.ChunkIndex, Truncate(c.ChunkText, MaxCharsPerChunk)))
            .ToList();

        var evidence = new LitigationAnalysisEvidence(@case.LitigationCaseId, @case.Cnr, @case.Court,
            @case.CaseNumber, @case.ProceedingType, @case.CaseStatus, excerpts);
        return evidence;
    }

    public static string SerializeEvidence(LitigationAnalysisEvidence evidence) =>
        JsonSerializer.Serialize(evidence, JsonOptions);

    public static string ComputeHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string BuildCasePrompt(LitigationAnalysisEvidence evidence)
    {
        var evidenceJson = SerializeEvidence(evidence);
        return $$"""
            You are reviewing one litigation case for a BFSI analyst. Use only the supplied persisted evidence.
            Do not infer facts, outcomes, liability, intent, or current status from missing order text.
            If no excerpts are supplied, return status "InsufficientEvidence" and state that no order text was retained.
            Every non-unknown conclusion must cite one or more exact evidence references from the supplied excerpts.
            Respond only with JSON of this shape:
            {"status":"Completed|InsufficientEvidence","summary":"string","unknowns":["string"],"evidenceReferences":[{"litigationCaseOrderId":1,"litigationOrderDocumentId":1,"pageNumber":1,"chunkIndex":0}]}
            Evidence:
            {{evidenceJson}}
            """;
    }

    public static string BuildPortfolioBatchPrompt(string batchJson) => $$"""
        You are reading a small batch of persisted, validated litigation case analyses for one company, as part of a portfolio synthesis. Each
        carries its summary, risk tier, the company's side, any restraint, the outcome of the final order, and quotes taken verbatim from the
        orders. Write one summary of this batch: what the cases are about, which are the most serious and why, who they run against, any
        restraint or lis pendens, and how the disposed ones ended. Use only what the entries state; do not add a fact, amount, outcome or
        conclusion not supported by an exact caseAnalysisId. Failed and InsufficientEvidence entries are unknowns, not negative findings.
        Respond only with JSON:
        {"status":"Completed|InsufficientEvidence","summary":"string","unknowns":["string"],"caseAnalysisIds":[1]}
        Batch:
        {{batchJson}}
        """;

    public static string BuildPortfolioReducePrompt(string summariesJson, string? coverageJson)
    {
        var coverageLine = coverageJson is null ? "" :
            "A coverage object says how many cases exist and how many were summarised: state that plainly in the summary, and treat cases not summarised as unknowns, not as negative findings." + Environment.NewLine
            + "Coverage: " + coverageJson;
        return $$"""
        You are combining summaries of batches of litigation case analyses for one company into a single portfolio-level synthesis. Each batch
        summary lists the caseAnalysisIds it covers. Use only the summaries below: keep the most serious matters first, group similar matters,
        and do not add a fact, outcome, trend or risk conclusion that no batch states. Cite only caseAnalysisIds that appear in the batches.
        {{coverageLine}}
        Respond only with JSON:
        {"status":"Completed|InsufficientEvidence","summary":"string","unknowns":["string"],"caseAnalysisIds":[1]}
        Batch summaries:
        {{summariesJson}}
        """;
    }

    public static string BuildPortfolioPrompt(string persistedCaseAnalysesJson, bool withCoverage) => withCoverage
        ? $$"""
        You are preparing a portfolio-level litigation synthesis for a company with many cases. Use only the persisted, validated case
        analyses below, which are the highest-risk ones. A coverage object says how many cases exist and how many are shown: state that
        plainly in the summary, and treat the cases not shown as unknowns, not as negative findings. Do not add a fact, outcome, trend, or
        risk conclusion not supported by an exact caseAnalysisId. Failed and InsufficientEvidence entries are unknowns.
        Respond only with JSON:
        {"status":"Completed|InsufficientEvidence","summary":"string","unknowns":["string"],"caseAnalysisIds":[1]}
        Persisted case analyses:
        {{persistedCaseAnalysesJson}}
        """
        : BuildPortfolioPrompt(persistedCaseAnalysesJson);

    public static string BuildPortfolioPrompt(string persistedCaseAnalysesJson) => $$"""
        You are preparing a portfolio-level litigation synthesis. Use only the persisted, validated case
        analyses below. Do not add a fact, outcome, trend, or risk conclusion not supported by an exact
        caseAnalysisId. Failed and InsufficientEvidence entries are unknowns, not negative findings.
        Respond only with JSON:
        {"status":"Completed|InsufficientEvidence","summary":"string","unknowns":["string"],"caseAnalysisIds":[1]}
        Persisted case analyses:
        {{persistedCaseAnalysesJson}}
        """;

    private static string Truncate(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

public sealed record LitigationAnalysisEvidence(long LitigationCaseId, string? Cnr, string? Court,
    string? CaseNumber, string? ProceedingType, string? CaseStatus, IReadOnlyList<LitigationEvidenceExcerpt> Excerpts);
public sealed record LitigationEvidenceExcerpt(long LitigationCaseOrderId, long LitigationOrderDocumentId,
    int PageNumber, int ChunkIndex, string Text);
