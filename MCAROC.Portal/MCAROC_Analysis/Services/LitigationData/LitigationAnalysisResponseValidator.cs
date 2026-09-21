using System.Text.Json;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Rejects model output unless every claimed evidence reference is one of the exact bounded excerpts
/// persisted with the prompt. This makes a citation a real database address, not model-authored decoration.</summary>
public static class LitigationAnalysisResponseValidator
{
    public static LitigationAnalysisValidationResult ValidatePortfolio(string rawJson, ISet<long> allowedIds)
    {
        PortfolioResponse? response;
        try { response = JsonSerializer.Deserialize<PortfolioResponse>(rawJson, Options); }
        catch (JsonException ex) { return LitigationAnalysisValidationResult.Rejected($"Invalid JSON: {ex.Message}"); }
        if (response is null || response.Status is not ("Completed" or "InsufficientEvidence") || string.IsNullOrWhiteSpace(response.Summary))
            return LitigationAnalysisValidationResult.Rejected("Missing or unsupported portfolio status/summary.");
        var ids = response.CaseAnalysisIds ?? [];
        if (response.Status == "Completed" && ids.Count == 0) return LitigationAnalysisValidationResult.Rejected("Completed portfolio output has no case references.");
        if (ids.Any(id => !allowedIds.Contains(id))) return LitigationAnalysisValidationResult.Rejected("Portfolio referenced analysis not persisted with this run.");
        return LitigationAnalysisValidationResult.Accepted(JsonSerializer.Serialize(response, Options));
    }
    public static LitigationAnalysisValidationResult ValidateCase(string rawJson, LitigationAnalysisEvidence evidence)
    {
        CaseResponse? response;
        try { response = JsonSerializer.Deserialize<CaseResponse>(rawJson, Options); }
        catch (JsonException ex) { return LitigationAnalysisValidationResult.Rejected($"Invalid JSON: {ex.Message}"); }
        if (response is null || string.IsNullOrWhiteSpace(response.Status)) return LitigationAnalysisValidationResult.Rejected("Missing status.");
        if (response.Status is not ("Completed" or "InsufficientEvidence")) return LitigationAnalysisValidationResult.Rejected("Unsupported status.");
        if (string.IsNullOrWhiteSpace(response.Summary)) return LitigationAnalysisValidationResult.Rejected("Missing summary.");

        var allowed = evidence.Excerpts.Select(x => (x.LitigationCaseOrderId, x.LitigationOrderDocumentId, x.PageNumber, x.ChunkIndex)).ToHashSet();
        var refs = response.EvidenceReferences ?? [];
        if (response.Status == "Completed" && refs.Count == 0) return LitigationAnalysisValidationResult.Rejected("Completed output has no evidence references.");
        if (response.Status == "InsufficientEvidence" && evidence.Excerpts.Count > 0 && refs.Count == 0)
            return LitigationAnalysisValidationResult.Rejected("InsufficientEvidence output omitted available evidence.");
        if (refs.Any(r => !allowed.Contains((r.LitigationCaseOrderId, r.LitigationOrderDocumentId, r.PageNumber, r.ChunkIndex))))
            return LitigationAnalysisValidationResult.Rejected("Response referenced evidence not present in the prompt.");

        return LitigationAnalysisValidationResult.Accepted(JsonSerializer.Serialize(response, Options));
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record CaseResponse(string? Status, string? Summary, List<string>? Unknowns, List<EvidenceReference>? EvidenceReferences);
    private sealed record EvidenceReference(long LitigationCaseOrderId, long LitigationOrderDocumentId, int PageNumber, int ChunkIndex);
    private sealed record PortfolioResponse(string? Status, string? Summary, List<string>? Unknowns, List<long>? CaseAnalysisIds);
}

public sealed record LitigationAnalysisValidationResult(bool IsAccepted, string? AnalysisJson, string? RejectReason)
{
    public static LitigationAnalysisValidationResult Accepted(string analysisJson) => new(true, analysisJson, null);
    public static LitigationAnalysisValidationResult Rejected(string reason) => new(false, null, reason);
}
