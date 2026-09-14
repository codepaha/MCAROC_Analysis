using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;

namespace MCAROC_Analysis.Services.CalculationAssurance;

public record CalculationAiAuditCallResult(bool Success, string RawResponse, string? FailureReason);

/// <summary>Calls Gemini 2.5 Flash-Lite via Vertex AI for the #164 second-line calculation review — same
/// construction pattern as AiCrossSectionAnalysisService/VertexAiExtractionService/ChatCompletionService
/// (per-class GoogleCredential/GenAiClient setup, no shared interface; revisit only if a 5th caller
/// appears). One call per CalculationAiAuditRun.</summary>
public class CalculationAiAuditService
{
    public const string ModelId = "gemini-2.5-flash-lite";

    private readonly GenAiClient _client;
    private readonly ILogger<CalculationAiAuditService> _logger;

    public CalculationAiAuditService(string projectId, string location, string credentialsPath, ILogger<CalculationAiAuditService> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    public async Task<CalculationAiAuditCallResult> CallAsync(string prompt, CancellationToken ct)
    {
        try
        {
            var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
            var response = await _client.Models.GenerateContentAsync(ModelId, prompt, config, ct);
            var rawResponse = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "";
            return new CalculationAiAuditCallResult(true, rawResponse, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vertex AI calculation-assurance audit call failed");
            return new CalculationAiAuditCallResult(false, "", ex.Message);
        }
    }
}
