using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Chat;

public record ResolvedCitation(string SourceType, long? ChunkId, string? DocumentName, int? PageNumber, string? EntityType, long? EntityId, string Label, long? DocumentId = null);

public record ChatCompletionResult(string Answer, bool InsufficientEvidence, List<ResolvedCitation> CitedSources);

/// <summary>Calls Gemini 2.5 Flash-Lite via Vertex AI (same construction pattern as
/// VertexAiExtractionService/AiCrossSectionAnalysisService) to answer one chat question, grounded strictly
/// in the supplied [FACT]/[SOURCE] blocks, and validates the response before it's shown.</summary>
public class ChatCompletionService
{
    public const string ModelId = "gemini-2.5-flash-lite";
    public const string PromptVersion = "1.0";
    private const string InsufficientAnswer = "I could not verify this from the uploaded records.";

    private readonly GenAiClient _client;
    private readonly ILogger<ChatCompletionService> _logger;

    protected ChatCompletionService()
    {
        _client = null!;
        _logger = null!;
    }

    public ChatCompletionService(string projectId, string location, string credentialsPath, ILogger<ChatCompletionService> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    public virtual async Task<ChatCompletionResult> CompleteAsync(
        string companyName, RetrievalContext context, IReadOnlyList<ChatMessage> history, string question, CancellationToken ct)
    {
        var prompt = BuildPrompt(companyName, context, history, question);

        string rawResponse;
        try
        {
            var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
            var response = await _client.Models.GenerateContentAsync(ModelId, prompt, config, ct);
            rawResponse = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vertex AI chat completion call failed");
            throw;
        }

        return Validate(rawResponse, context.Sources);
    }

    private static string BuildPrompt(string companyName, RetrievalContext context, IReadOnlyList<ChatMessage> history, string question)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a document-grounded assistant answering questions about \"{companyName}\" for a BFSI credit analyst.");
        sb.AppendLine("STRICT RULES:");
        sb.AppendLine("- Answer ONLY from the [FACT]/[SOURCE] blocks below. Never use outside knowledge.");
        sb.AppendLine("- If they do not directly support an answer, set insufficientEvidence=true and answer \"I could not verify this from the uploaded records.\"");
        sb.AppendLine("- Cite every material component of your answer, not just one supporting source (e.g. a question combining revenue and charge data should cite both).");
        sb.AppendLine("- Cite only the tags given (e.g. \"F1\", \"D2\") in citedTags — never invent a filename, page, or entity id yourself.");
        sb.AppendLine("- Distinguish current vs. historical facts, and filed-by vs. filed-against for litigation, explicitly in your answer.");
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
            sb.AppendLine(s.Type == SourceType.StructuredFact ? $"[FACT {s.Tag}] {s.DisplayLabel}" : $"[SOURCE {s.Tag}] {s.DisplayLabel}");
            sb.AppendLine(s.Text);
        }

        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        return sb.ToString();
    }

    internal static ChatCompletionResult Validate(string rawResponse, IReadOnlyList<RetrievedSource> sources)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new ChatCompletionResult(InsufficientAnswer, true, []);

        AiChatResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AiChatResponseDto>(rawResponse, JsonOptions);
        }
        catch (JsonException)
        {
            return new ChatCompletionResult(InsufficientAnswer, true, []);
        }
        if (dto is null)
            return new ChatCompletionResult(InsufficientAnswer, true, []);

        var sourcesByTag = sources.ToDictionary(s => s.Tag);
        var validTags = (dto.CitedTags ?? []).Where(sourcesByTag.ContainsKey).Distinct().ToList();

        // Simplified citation rule: insufficientEvidence == false requires at least one valid citation,
        // full stop — no separate attempt to classify whether the answer "contains a factual claim" (round
        // 3 judged that distinction unnecessary and harder to get right than the simple binary).
        if (!dto.InsufficientEvidence && validTags.Count == 0)
            return new ChatCompletionResult(InsufficientAnswer, true, []);

        // Always the fixed message and no citations here, never dto.Answer verbatim — the model flagging
        // insufficientEvidence=true doesn't stop it from also writing an uncited, unvalidated answer
        // string alongside that flag, and displaying that text would show an arbitrary uncited claim.
        if (dto.InsufficientEvidence)
            return new ChatCompletionResult(InsufficientAnswer, true, []);

        var citations = validTags.Select(tag =>
        {
            var s = sourcesByTag[tag];
            return s.Type == SourceType.DocumentChunk
                ? new ResolvedCitation("DocumentChunk", s.ChunkId, s.DocumentName, s.PageNumber, null, null, s.DisplayLabel, s.DocumentId)
                : new ResolvedCitation("StructuredFact", null, null, null, s.EntityType, s.EntityId, s.DisplayLabel);
        }).ToList();

        return new ChatCompletionResult(dto.Answer ?? InsufficientAnswer, false, citations);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private record AiChatResponseDto(
        [property: JsonPropertyName("answer")] string? Answer,
        [property: JsonPropertyName("citedTags")] List<string>? CitedTags,
        [property: JsonPropertyName("insufficientEvidence")] bool InsufficientEvidence);
}
