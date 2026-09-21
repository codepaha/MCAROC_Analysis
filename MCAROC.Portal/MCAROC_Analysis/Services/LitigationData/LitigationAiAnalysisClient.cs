using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Services.CalculationAssurance;

namespace MCAROC_Analysis.Services.LitigationData;

public record LitigationAiCallResult(bool Success, string RawResponse, string? FailureReason);

/// <summary>Small seam around Vertex AI so the durable orchestration can be tested without credentials.
/// Credentials are supplied only by configuration at composition time and are never persisted with a run.</summary>
public interface ILitigationAiAnalysisClient
{
    Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct);
}

public sealed class VertexLitigationAiAnalysisClient : ILitigationAiAnalysisClient
{
    public const string ModelId = "gemini-2.5-flash-lite";
    private readonly GenAiClient _client;
    private readonly ILogger<VertexLitigationAiAnalysisClient> _logger;

    public VertexLitigationAiAnalysisClient(string projectId, string location, string credentialsPath,
        ILogger<VertexLitigationAiAnalysisClient> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath)
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    public async Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var (_, text, timedOut) = await CalculationAiAuditTimeoutRunner.RunAsync(async innerCt =>
            {
                var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
                var response = await _client.Models.GenerateContentAsync(ModelId, prompt, config, innerCt);
                return response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
            }, TimeSpan.FromSeconds(timeoutSeconds), ct);
            return timedOut
                ? new(false, string.Empty, $"Vertex AI call timed out after {timeoutSeconds}s.")
                : new(true, text ?? string.Empty, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Litigation Gemini analysis call failed");
            return new(false, string.Empty, ex.Message);
        }
    }
}
