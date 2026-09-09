using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.McaFilings;

public record FilingDocumentContext(string OriginalFileName, string? FormType, string ExtractedText);

public record ExtractionOutcome(
    string SchemaName, string? ExtractedJson, string? RawModelResponse,
    ExtractionValidationStatus ValidationStatus, string? ValidationErrors,
    ExtractionStatus Status, string? FailureReason);

/// <summary>Calls Gemini 2.5 Flash-Lite via Vertex AI (Google.GenAI SDK — API surface confirmed by
/// reflecting on the installed package rather than assumed from memory/docs, per the plan's explicit
/// caution about this SDK's history of changing patterns) to extract structured data for one filing,
/// grouping all of its documents' text into a single prompt/call rather than one call per document.</summary>
public class VertexAiExtractionService
{
    public const string ModelId = "gemini-2.5-flash-lite";
    public const string PromptVersion = "1.0";
    private const int MaxContextCharsPerDocument = 40_000; // guards prompt size for very large native-text documents

    private readonly GenAiClient _client;
    private readonly ILogger<VertexAiExtractionService> _logger;

    public VertexAiExtractionService(string projectId, string location, string credentialsPath, ILogger<VertexAiExtractionService> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath)
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    public async Task<ExtractionOutcome> ExtractAsync(
        string srn, FilingCategory category, string? dominantFormType,
        IReadOnlyList<FilingDocumentContext> documents, CancellationToken ct)
    {
        var (schemaName, jsonShape) = SelectSchema(category, dominantFormType);
        var prompt = BuildPrompt(srn, dominantFormType, documents, jsonShape);

        string rawResponse;
        try
        {
            var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
            var response = await _client.Models.GenerateContentAsync(ModelId, prompt, config, ct);
            rawResponse = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vertex AI call failed for filing {Srn}", srn);
            return new ExtractionOutcome(schemaName, null, null, ExtractionValidationStatus.Invalid, ex.Message, ExtractionStatus.Failed, ex.Message);
        }

        return Validate(schemaName, rawResponse);
    }

    private static (string SchemaName, string JsonShape) SelectSchema(FilingCategory category, string? formType)
    {
        var form = formType?.ToLowerInvariant() ?? "";
        return category switch
        {
            FilingCategory.Charge => (FilingSchemaNames.Charge, ChargeExtractionResult.JsonShape),
            FilingCategory.Constitutional => (FilingSchemaNames.Constitutional, ConstitutionalExtractionResult.JsonShape),
            FilingCategory.Compliance when form.Contains("adt") => (FilingSchemaNames.Auditor, AuditorExtractionResult.JsonShape),
            FilingCategory.Compliance when form.Contains("inc-22") || form.Contains("inc-28") =>
                (FilingSchemaNames.RegisteredOffice, RegisteredOfficeExtractionResult.JsonShape),
            FilingCategory.Compliance when form.Contains("pas-3") =>
                (FilingSchemaNames.CapitalAllotment, CapitalAllotmentExtractionResult.JsonShape),
            FilingCategory.Compliance => (FilingSchemaNames.CorporateResolutionFiling, CorporateResolutionExtractionResult.JsonShape),
            _ => (FilingSchemaNames.CorporateResolutionFiling, CorporateResolutionExtractionResult.JsonShape)
        };
    }

    private static string BuildPrompt(string srn, string? dominantFormType, IReadOnlyList<FilingDocumentContext> documents, string jsonShape)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are extracting structured facts from an Indian MCA (Ministry of Corporate Affairs) filing.");
        sb.AppendLine($"Filing SRN: {srn}. Dominant form type: {dominantFormType ?? "unknown"}.");
        sb.AppendLine();
        sb.AppendLine("STRICT RULES:");
        sb.AppendLine("- Use null for any field not explicitly stated in the text below. Never infer or guess a value.");
        sb.AppendLine("- For every non-null field, list the page number(s) (1-indexed, as shown in the '--- Page N' markers) where you found it.");
        sb.AppendLine("- Only use facts from the documents below, which all belong to this one filing. Do not bring in outside knowledge.");
        sb.AppendLine("- Respond with ONLY a JSON object matching this exact shape (no markdown fences, no commentary):");
        sb.AppendLine(jsonShape);
        sb.AppendLine();
        sb.AppendLine("=== FILING DOCUMENTS ===");
        foreach (var doc in documents)
        {
            var text = doc.ExtractedText.Length > MaxContextCharsPerDocument
                ? doc.ExtractedText[..MaxContextCharsPerDocument] + "\n[...truncated...]"
                : doc.ExtractedText;
            sb.AppendLine($"--- Document: {doc.OriginalFileName} ({doc.FormType ?? "unclassified"}) ---");
            sb.AppendLine(text);
        }

        return sb.ToString();
    }

    private static ExtractionOutcome Validate(string schemaName, string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new ExtractionOutcome(schemaName, null, rawResponse, ExtractionValidationStatus.Invalid, "Empty model response.", ExtractionStatus.Failed, "Empty model response.");

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var hasAnyValue = doc.RootElement.EnumerateObject()
                .Any(prop => prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("value", out var v)
                    && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));

            var validationStatus = hasAnyValue ? ExtractionValidationStatus.Valid : ExtractionValidationStatus.PartialWarnings;
            var validationErrors = hasAnyValue ? null : "Every field came back null — likely no extractable content for this schema.";

            return new ExtractionOutcome(schemaName, rawResponse, rawResponse, validationStatus, validationErrors, ExtractionStatus.Success, null);
        }
        catch (JsonException ex)
        {
            return new ExtractionOutcome(schemaName, null, rawResponse, ExtractionValidationStatus.Invalid, $"Model did not return valid JSON: {ex.Message}", ExtractionStatus.Failed, "Invalid JSON response.");
        }
    }
}
