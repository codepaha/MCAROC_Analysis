using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Extensions.Logging;
using MCAROC_Analysis.Services.AiUsage;

namespace MCAROC_Analysis.Services.Chat;

public record ResolvedCitation(
    string SourceType, long? ChunkId, string? DocumentName, int? PageNumber, string? EntityType, long? EntityId,
    string Label, long? DocumentId = null, long? LitigationCaseId = null, long? LitigationCaseOrderId = null);

public record ChatCompletionResult(string Answer, bool InsufficientEvidence, List<ResolvedCitation> CitedSources);

/// <summary>Calls Gemini 2.5 Flash-Lite via Vertex AI (same construction pattern as
/// VertexAiExtractionService/AiCrossSectionAnalysisService) to answer one chat question, grounded strictly
/// in the supplied [FACT]/[SOURCE] blocks, and validates the response before it's shown.</summary>
public class ChatCompletionService
{
    /// <summary>The model that answers unless <c>Chat:CompletionModel</c> names another.</summary>
    public const string ModelId = "gemini-3.1-flash-lite";
    public const string PromptVersion = "1.3";
    private const string InsufficientAnswer = "I could not verify this from the uploaded records.";

    internal static ChatCompletionResult InsufficientEvidenceResult() =>
        new(InsufficientAnswer, true, []);

    private readonly string _modelId = ModelId;

    /// <summary>The model this instance asks, recorded with every answer.</summary>
    public virtual string Model => _modelId;

    private readonly GenAiClient _client;
    private readonly ILogger<ChatCompletionService> _logger;

    protected ChatCompletionService()
    {
        _client = null!;
        _logger = null!;
    }

    protected ChatCompletionService(ILogger<ChatCompletionService> logger)
    {
        _client = null!;
        _logger = logger;
    }

    public ChatCompletionService(string projectId, string location, string credentialsPath, ILogger<ChatCompletionService> logger, string? modelId = null)
    {
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(modelId)) _modelId = modelId.Trim();
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    public virtual async Task<ChatCompletionResult> CompleteAsync(
        string companyName, RetrievalContext context, IReadOnlyList<ChatMessage> history, string question, CancellationToken ct)
    {
        var prompt = BuildPrompt(companyName, context, history, question);

        var rawResponse = await GenerateRawAsync(prompt, ct);
        var (result, outcome) = ValidateWithOutcome(rawResponse, context.Sources);
        if (!IsUnusable(outcome)) return result;

        // The model did not say the evidence was insufficient; its reply was unusable (empty, not the JSON asked for, a blank answer, or no valid
        // citation). A small model does this now and then on a long prompt, and the fixed "could not verify" message would then be shown although the
        // sources support an answer. Ask once more, and say why.
        // The reply text itself is never logged: it can quote the client's records. Only the outcome and the lengths are.
        _logger?.LogWarning("Chat reply was unusable ({Outcome}, {Length} characters); asking once more.", outcome, rawResponse.Length);
        // The retry regenerates from the evidence. It deliberately does NOT hand the earlier answer back for "citing": a tag is checked only
        // for existing, not for supporting the claim, so an unsupported answer re-sent with a valid tag would look sourced.
        var retryPrompt = prompt + "\n\nYour previous reply could not be used (" + Describe(outcome) + "). Answer again from the [FACT]/[SOURCE] blocks above only, "
            + "and list in citedTags the tag of every block that supports each claim you make. Reply with ONLY the JSON object. "
            + "If the blocks do not support an answer, set insufficientEvidence=true.";
        var retryResponse = await GenerateRawAsync(retryPrompt, ct);
        (result, outcome) = ValidateWithOutcome(retryResponse, context.Sources);
        if (IsUnusable(outcome))
            _logger?.LogWarning("Chat reply was unusable again ({Outcome}, {Length} characters); showing the fixed message.", outcome, retryResponse.Length);
        return result;
    }

    /// <summary>One call to the model, returning its raw text. A seam so the retry can be tested without Vertex.</summary>
    protected virtual async Task<string> GenerateRawAsync(string prompt, CancellationToken ct)
    {
        try
        {
            var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
            var response = await _client.Models.GenerateContentAsync(_modelId, prompt, config, ct);
            AiUsageLog.RecordGeneration(AiUsagePurposes.Copilot, _modelId, response.UsageMetadata);
            return response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Vertex AI chat completion call failed");
            throw;
        }
    }

    private static bool IsUnusable(ChatReplyOutcome outcome) =>
        outcome is ChatReplyOutcome.Empty or ChatReplyOutcome.Unparseable or ChatReplyOutcome.Uncited or ChatReplyOutcome.BlankAnswer;

    private static string Describe(ChatReplyOutcome outcome) => outcome switch
    {
        ChatReplyOutcome.Empty => "it was empty",
        ChatReplyOutcome.Unparseable => "it was not the JSON object asked for",
        ChatReplyOutcome.BlankAnswer => "its answer was blank",
        _ => "it cited no tag from the blocks above"
    };

    private static string? TryReadAnswer(string raw)
    {
        try { return JsonSerializer.Deserialize<AiChatResponseDto>(raw, JsonOptions)?.Answer; }
        catch (JsonException) { return null; }
    }

    internal static string BuildPrompt(string companyName, RetrievalContext context, IReadOnlyList<ChatMessage> history, string question)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a document-grounded assistant answering questions about \"{companyName}\" for a BFSI credit analyst.");
        sb.AppendLine("STRICT RULES:");
        sb.AppendLine("- Answer ONLY from the [FACT]/[SOURCE] blocks below. Never use outside knowledge.");
        sb.AppendLine("- If they do not directly support an answer, set insufficientEvidence=true and answer \"I could not verify this from the uploaded records.\"");
        sb.AppendLine("- Cite every material component of your answer, not just one supporting source (e.g. a question combining revenue and charge data should cite both).");
        sb.AppendLine("- Cite only the tags given (e.g. \"F1\", \"D2\") in citedTags — never invent a filename, page, or entity id yourself.");
        sb.AppendLine("- Whenever you answer (insufficientEvidence=false), citedTags MUST list at least one tag: an answer with an empty citedTags is discarded and the user sees nothing.");
        sb.AppendLine("- Distinguish current vs. historical facts, and filed-by vs. filed-against for litigation, explicitly in your answer.");
        sb.AppendLine("- \"O\" sources are an exact, structured order-outcome lookup. When the question asks which or how many orders had an outcome, list every matching O source (not a sample), cite each, and cite and repeat the coverage note's wording if it says the list is not exhaustive.");
        sb.AppendLine("- \"C\" facts are the litigation record of a case the question names (status, hearing dates, newest orders). For current status, next hearing or the latest orders, answer from C and the newest-dated L sources and state the order dates; never present an older order as the latest.");
        sb.AppendLine("- \"M\" facts are exact computed metrics. For a count, total, ratio or trend, quote the M value and cite it rather than recomputing it from other facts.");
        sb.AppendLine("- Facts of the Documents domain list the filing documents whose own text names the property the question describes. Read their header: " +
            "(1) \"N document(s) on file whose own text describes the property ... This is every such document\" is the complete list for the current filing batch. When the question asks which documents, charges or satisfactions concern that property, list EVERY \"Document:\" fact by name with its charge number, holder and status (say Satisfied for a released charge) and cite each; do not add a \"may not be complete\" caveat for that list, and never answer that no charge was satisfied while such a fact says otherwise. " +
            "(2) \"At least N document(s) ... reached its limit\" is NOT complete: list what is given and say plainly that there may be more. " +
            "(3) \"Candidate document (possible match only, identity not established)\" facts are only possible matches (one shared identifier, an area figure alone, or a number shared across kinds of identifier): list them separately as possible, say identity is not established, and never present their charge as a charge on this property. " +
            "(4) \"The question names only one identifier\" means the documents cannot be established as being about the property: say so and offer them only as candidates.");
        sb.AppendLine("- If an \"S\" source is present and your list or count relies on D or L sources (other than the complete property-documents list above), cite S1 and say plainly that the list may not be complete.");
        sb.AppendLine("- Never treat earlier chat turns as evidence — only the [FACT]/[SOURCE] blocks below, freshly retrieved for this question.");
        sb.AppendLine($"- Document index status for this request: {context.IndexingStatusLabel}. If not Complete, never conclude a document 'does not exist' — only that it was 'not found in the currently indexed records.'");
        sb.AppendLine("- Respond with ONLY a JSON object matching this exact shape (no markdown fences, no commentary):");
        sb.AppendLine("""{ "answer": "string", "citedTags": ["string"], "insufficientEvidence": false }""");
        sb.AppendLine();

        if (history.Count > 0)
        {
            sb.AppendLine("=== RECENT CHAT HISTORY (conversational context only — never evidence) ===");
            foreach (var m in history)
                sb.AppendLine($"{m.Role}: {m.MessageText}");
            sb.AppendLine();
        }

        sb.AppendLine("=== FACTS AND SOURCES ===");
        foreach (var s in context.Sources)
        {
            sb.AppendLine(s.Type is SourceType.StructuredFact or SourceType.Metric or SourceType.LitigationCase ? $"[FACT {s.Tag}] {s.DisplayLabel}" : $"[SOURCE {s.Tag}] {s.DisplayLabel}");
            sb.AppendLine(s.Text);
        }

        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        return sb.ToString();
    }

    /// <summary>Why a reply was or was not usable. Only the first three say the model failed to follow the format; <see cref="ModelSaidInsufficient"/>
    /// is the model honestly reporting that the sources do not support an answer, which is never retried.</summary>
    public enum ChatReplyOutcome { Valid, ModelSaidInsufficient, Empty, Unparseable, Uncited, BlankAnswer }

    internal static ChatCompletionResult Validate(string rawResponse, IReadOnlyList<RetrievedSource> sources) =>
        ValidateWithOutcome(rawResponse, sources).Result;

    internal static (ChatCompletionResult Result, ChatReplyOutcome Outcome) ValidateWithOutcome(string rawResponse, IReadOnlyList<RetrievedSource> sources)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return (new ChatCompletionResult(InsufficientAnswer, true, []), ChatReplyOutcome.Empty);

        AiChatResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AiChatResponseDto>(rawResponse, JsonOptions);
        }
        catch (JsonException)
        {
            return (new ChatCompletionResult(InsufficientAnswer, true, []), ChatReplyOutcome.Unparseable);
        }
        if (dto is null)
            return (new ChatCompletionResult(InsufficientAnswer, true, []), ChatReplyOutcome.Unparseable);

        var sourcesByTag = sources.ToDictionary(s => s.Tag);
        var validTags = (dto.CitedTags ?? []).Where(sourcesByTag.ContainsKey).Distinct().ToList();

        // Simplified citation rule: insufficientEvidence == false requires at least one valid citation,
        // full stop — no separate attempt to classify whether the answer "contains a factual claim" (round
        // 3 judged that distinction unnecessary and harder to get right than the simple binary).
        if (!dto.InsufficientEvidence && validTags.Count == 0)
            return (new ChatCompletionResult(InsufficientAnswer, true, []), ChatReplyOutcome.Uncited);

        // Always the fixed message and no citations here, never dto.Answer verbatim — the model flagging
        // insufficientEvidence=true doesn't stop it from also writing an uncited, unvalidated answer
        // string alongside that flag, and displaying that text would show an arbitrary uncited claim.
        if (dto.InsufficientEvidence)
            return (new ChatCompletionResult(InsufficientAnswer, true, []), ChatReplyOutcome.ModelSaidInsufficient);

        // A supported answer needs words. A blank one is a failed reply, and the fixed message written out as the "answer" is the model declining,
        // not an answer to show as supported with citations.
        if (string.IsNullOrWhiteSpace(dto.Answer))
            return (new ChatCompletionResult(InsufficientAnswer, true, []), ChatReplyOutcome.BlankAnswer);
        if (string.Equals(dto.Answer.Trim(), InsufficientAnswer, StringComparison.OrdinalIgnoreCase))
            return (new ChatCompletionResult(InsufficientAnswer, true, []), ChatReplyOutcome.ModelSaidInsufficient);

        var citations = validTags.Select(tag =>
        {
            var s = sourcesByTag[tag];
            return s.Type switch
            {
                SourceType.DocumentChunk =>
                    new ResolvedCitation("DocumentChunk", s.ChunkId, s.DocumentName, s.PageNumber, null, null, s.DisplayLabel, s.DocumentId),
                SourceType.LitigationChunk =>
                    new ResolvedCitation("LitigationChunk", s.ChunkId, s.DocumentName, s.PageNumber, null, null, s.DisplayLabel, s.DocumentId,
                        s.LitigationCaseId, s.LitigationCaseOrderId),
                SourceType.OrderOutcome =>
                    new ResolvedCitation("OrderOutcome", null, null, null, s.EntityType, s.EntityId, s.DisplayLabel, s.DocumentId,
                        s.LitigationCaseId, s.LitigationCaseOrderId),
                SourceType.Metric =>
                    new ResolvedCitation("Metric", null, null, null, s.EntityType, null, s.DisplayLabel),
                SourceType.LitigationCase =>
                    new ResolvedCitation("LitigationCase", null, null, null, s.EntityType, s.EntityId, s.DisplayLabel, null, s.LitigationCaseId),
                SourceType.SearchCoverage =>
                    new ResolvedCitation("SearchCoverage", null, null, null, s.EntityType, null, s.DisplayLabel),
                // A fact read from a document page (a certificate of change of name) keeps that page, so its citation opens it.
                _ => new ResolvedCitation("StructuredFact", null, s.DocumentName, s.PageNumber, s.EntityType, s.EntityId, s.DisplayLabel, s.DocumentId)
            };
        }).ToList();

        return (new ChatCompletionResult(dto.Answer, false, citations), ChatReplyOutcome.Valid);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private record AiChatResponseDto(
        [property: JsonPropertyName("answer")] string? Answer,
        [property: JsonPropertyName("citedTags")] List<string>? CitedTags,
        [property: JsonPropertyName("insufficientEvidence")] bool InsufficientEvidence);
}
