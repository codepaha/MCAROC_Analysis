using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Epic #195's order-outcome classification: one bounded prompt per order document, and a fail-closed
/// validator in the same shape as <see cref="LitigationAnalysisResponseValidator"/>. The model only labels what
/// the supplied excerpts show; code decides what is persisted. Order prose is too free-form for keyword rules
/// ("stay was vacated" vs "stay granted" — negation matters), which is why this is a validated model call rather
/// than a <c>ChargeSecurityClassifier</c>-style rule table.</summary>
public static class LitigationOrderClassifier
{
    public const string PromptVersion = "1.0";

    /// <summary>Order sheets are usually a few pages; this bound keeps one call small while covering the vast
    /// majority of orders whole. Anything longer is flagged <see cref="LitigationOrderEvidence.Truncated"/>.</summary>
    internal const int MaxChunksPerOrder = 24;
    private const int MaxCharsPerChunk = 3000;

    /// <summary>The prompt marker the orchestrator's test doubles key on — kept as a constant so the prompt and
    /// anything recognising it cannot drift apart.</summary>
    internal const string PromptMarker = "You are classifying the outcome of ONE court order";

    public static LitigationOrderEvidence BuildEvidence(IReadOnlyCollection<LitigationOrderChunk> documentChunks)
    {
        var first = documentChunks.First();
        var ordered = documentChunks.OrderBy(c => c.PageNumber).ThenBy(c => c.ChunkIndex).ToList();
        var excerpts = ordered.Take(MaxChunksPerOrder)
            .Select(c => new LitigationOrderExcerpt(c.PageNumber, c.ChunkIndex,
                c.ChunkText.Length <= MaxCharsPerChunk ? c.ChunkText : c.ChunkText[..MaxCharsPerChunk]))
            .ToList();
        var truncated = ordered.Count > MaxChunksPerOrder || ordered.Take(MaxChunksPerOrder).Any(c => c.ChunkText.Length > MaxCharsPerChunk);
        return new LitigationOrderEvidence(first.LitigationOrderDocumentId, first.LitigationCaseOrderId, first.LitigationCaseId,
            first.CaseNumber, first.Court, first.OrderDate, first.OrderType, truncated, excerpts);
    }

    public static string SerializeEvidence(LitigationOrderEvidence evidence) => JsonSerializer.Serialize(evidence, JsonOptions);

    public static string BuildPrompt(LitigationOrderEvidence evidence)
    {
        var outcomeNames = string.Join("|", Enum.GetNames<LitigationOrderOutcome>());
        return $$"""
            {{PromptMarker}} for a BFSI analyst. Use only the supplied excerpts of this order.
            Label only what THIS order itself grants, imposes or decides. A party's prayer, an argument, a recital of an
            earlier order, or relief merely sought is NOT an outcome.
            Outcome types:
            - FinePenalty: the order imposes a fine, penalty or costs on a party. Give fineAmount in rupees only if the order states it.
            - StayGranted: the order grants or continues a stay.
            - StayVacated: the order vacates, lifts, sets aside or refuses a stay. Never label this StayGranted.
            - PossessionOrder: the order directs delivery or taking of possession of property.
            - Injunction: the order grants an injunction (temporary or permanent) other than a stay.
            - Dismissal: the order dismisses the case, petition or application.
            - DisposedSettled: the order disposes of the matter as settled, withdrawn or otherwise finally disposed (not dismissed).
            - InterimRelief: the order grants interim relief not covered above.
            - AdjournedNoSubstantiveOrder: the order only adjourns or lists the matter, with no substantive direction.
            An order can have several outcomes. Every outcome must cite one or more excerpts by their exact pageNumber and chunkIndex.
            If the excerpts do not show what the order decided, return status "InsufficientEvidence" with no outcomes.
            Respond only with JSON of this shape:
            {"status":"Completed|InsufficientEvidence","confidence":"High|Medium|Low","outcomes":[{"type":"{{outcomeNames}}","fineAmount":null,"evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}
            Order:
            {{SerializeEvidence(evidence)}}
            """;
    }

    public static LitigationOrderClassificationResult Validate(string rawJson, LitigationOrderEvidence evidence)
    {
        Response? response;
        try { response = JsonSerializer.Deserialize<Response>(rawJson, JsonOptions); }
        catch (JsonException ex) { return LitigationOrderClassificationResult.Rejected($"Invalid JSON: {ex.Message}"); }
        if (response is null || response.Status is not ("Completed" or "InsufficientEvidence"))
            return LitigationOrderClassificationResult.Rejected("Missing or unsupported status.");

        ClassificationConfidence? confidence = null;
        if (response.Confidence is not null)
        {
            if (!TryParseName<ClassificationConfidence>(response.Confidence, out var parsed))
                return LitigationOrderClassificationResult.Rejected("Unsupported confidence.");
            confidence = parsed;
        }

        var outcomes = response.Outcomes ?? [];
        if (response.Status == "InsufficientEvidence")
            return outcomes.Count > 0
                ? LitigationOrderClassificationResult.Rejected("InsufficientEvidence output also claimed outcomes.")
                : LitigationOrderClassificationResult.Insufficient(confidence, JsonSerializer.Serialize(new NormalizedClassification("InsufficientEvidence", confidence?.ToString(), []), JsonOptions));

        if (outcomes.Count == 0)
            return LitigationOrderClassificationResult.Rejected("Completed output has no outcomes.");

        var allowed = evidence.Excerpts.Select(x => (x.PageNumber, x.ChunkIndex)).ToHashSet();
        var normalized = new List<NormalizedOutcome>();
        decimal? fineAmount = null;
        foreach (var o in outcomes)
        {
            if (o.Type is null || !TryParseName<LitigationOrderOutcome>(o.Type, out var type))
                return LitigationOrderClassificationResult.Rejected($"Unsupported outcome type '{o.Type}'.");
            if (normalized.Any(n => n.Type == type.ToString()))
                return LitigationOrderClassificationResult.Rejected($"Outcome type {type} listed twice.");
            var refs = o.EvidenceReferences ?? [];
            if (refs.Count == 0)
                return LitigationOrderClassificationResult.Rejected($"Outcome {type} has no evidence references.");
            if (refs.Any(r => !allowed.Contains((r.PageNumber, r.ChunkIndex))))
                return LitigationOrderClassificationResult.Rejected($"Outcome {type} referenced evidence not present in the prompt.");
            if (o.FineAmount is not null)
            {
                if (type != LitigationOrderOutcome.FinePenalty)
                    return LitigationOrderClassificationResult.Rejected($"fineAmount given on a non-fine outcome ({type}).");
                if (o.FineAmount <= 0)
                    return LitigationOrderClassificationResult.Rejected("fineAmount must be positive.");
                fineAmount = o.FineAmount;
            }
            normalized.Add(new NormalizedOutcome(type.ToString(), o.FineAmount,
                refs.Select(r => new EvidenceReference(r.PageNumber, r.ChunkIndex)).Distinct().ToList()));
        }

        var types = normalized.Select(n => Enum.Parse<LitigationOrderOutcome>(n.Type)).ToList();
        return LitigationOrderClassificationResult.Accepted(types, fineAmount, confidence,
            JsonSerializer.Serialize(new NormalizedClassification("Completed", confidence?.ToString(), normalized), JsonOptions));
    }

    public static string SerializeOutcomeTypes(IEnumerable<LitigationOrderOutcome> outcomes) =>
        JsonSerializer.Serialize(outcomes.Select(o => o.ToString()));

    /// <summary>Reads <see cref="LitigationOrderClassification.OutcomeTypesJson"/>, skipping any name this build
    /// doesn't know rather than failing — a row written by a newer taxonomy must not break an older reader.</summary>
    public static IReadOnlyList<LitigationOrderOutcome> ParseOutcomeTypes(string outcomeTypesJson)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(outcomeTypesJson) ?? [])
                .Select(n => TryParseName<LitigationOrderOutcome>(n, out var o) ? o : (LitigationOrderOutcome?)null)
                .OfType<LitigationOrderOutcome>().ToList();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Names only — <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would also accept "3"
    /// and silently map a numeric string to whatever member sits at that position.</summary>
    private static bool TryParseName<T>(string value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        var name = Enum.GetNames<T>().FirstOrDefault(n => string.Equals(n, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out parsed);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Response(string? Status, string? Confidence, List<ResponseOutcome>? Outcomes);
    private sealed record ResponseOutcome(string? Type, decimal? FineAmount, List<EvidenceReference>? EvidenceReferences);
    private sealed record EvidenceReference(int PageNumber, int ChunkIndex);
    private sealed record NormalizedOutcome(string Type, decimal? FineAmount, List<EvidenceReference> EvidenceReferences);
    private sealed record NormalizedClassification(string Status, string? Confidence, List<NormalizedOutcome> Outcomes);
}

public sealed record LitigationOrderEvidence(long LitigationOrderDocumentId, long LitigationCaseOrderId, long LitigationCaseId,
    string? CaseNumber, string? Court, string? OrderDate, string? OrderType, bool Truncated, IReadOnlyList<LitigationOrderExcerpt> Excerpts);
public sealed record LitigationOrderExcerpt(int PageNumber, int ChunkIndex, string Text);

public sealed record LitigationOrderClassificationResult(
    bool IsAccepted, LitigationAiAnalysisItemStatus Status, IReadOnlyList<LitigationOrderOutcome> Outcomes, decimal? FineAmount,
    ClassificationConfidence? Confidence, string? ClassificationJson, string? RejectReason)
{
    public static LitigationOrderClassificationResult Accepted(IReadOnlyList<LitigationOrderOutcome> outcomes, decimal? fineAmount,
        ClassificationConfidence? confidence, string json) =>
        new(true, LitigationAiAnalysisItemStatus.Completed, outcomes, fineAmount, confidence, json, null);
    public static LitigationOrderClassificationResult Insufficient(ClassificationConfidence? confidence, string json) =>
        new(true, LitigationAiAnalysisItemStatus.InsufficientEvidence, [], null, confidence, json, null);
    public static LitigationOrderClassificationResult Rejected(string reason) =>
        new(false, LitigationAiAnalysisItemStatus.Failed, [], null, null, null, reason);
}
