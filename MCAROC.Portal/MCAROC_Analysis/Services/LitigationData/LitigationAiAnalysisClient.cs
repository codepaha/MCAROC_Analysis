using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.AiUsage;

namespace MCAROC_Analysis.Services.LitigationData;

public record LitigationAiCallResult(bool Success, string RawResponse, string? FailureReason);

/// <summary>Small seam around Vertex AI so the durable orchestration can be tested without credentials.
/// Credentials are supplied only by configuration at composition time and are never persisted with a run.</summary>
public interface ILitigationAiAnalysisClient
{
    /// <summary><paramref name="modelId"/> overrides the client's default model for this one call.</summary>
    Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct, string? modelId = null);
}

public sealed class VertexLitigationAiAnalysisClient : ILitigationAiAnalysisClient
{
    public const string ModelId = "gemini-3.1-flash-lite";
    private const int HttpTimeoutMilliseconds = 1_200_000;
    private readonly GenAiClient _client;
    private readonly ILogger<VertexLitigationAiAnalysisClient> _logger;

    public VertexLitigationAiAnalysisClient(string projectId, string location, string credentialsPath,
        ILogger<VertexLitigationAiAnalysisClient> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath)
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        // The SDK's HTTP layer gives up after about 100 seconds by default, whatever timeout a call asks for: a case call that reads many
        // orders runs longer than that. The per-call limit is enforced by CallAsync's own timeout, so this only has to be higher.
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential,
            httpOptions: new HttpOptions { Timeout = HttpTimeoutMilliseconds });
    }

    private static string PurposeOf(string prompt) =>
        prompt.StartsWith(LitigationOrderClassifier.PromptMarker, StringComparison.Ordinal) ? AiUsagePurposes.OrderClassification
        : prompt.StartsWith(LitigationCaseAnalysisPrompt.Marker, StringComparison.Ordinal) ? AiUsagePurposes.CaseAnalysis
        : AiUsagePurposes.LitigationPortfolio;

    /// <summary>Waits before a retry of a call that failed for a reason that passes (quota, a busy or briefly unavailable service, a dropped connection).</summary>
    private static readonly TimeSpan[] TransientRetryDelays = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>True for an error worth trying again: rate limits, server errors, overload, timeouts and network faults. Not for a bad request,
    /// a permission error or a refused prompt, which would fail the same way again.</summary>
    internal static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException or IOException or TimeoutException) return true;
        var message = ex.Message;
        return new[] { "429", "500", "502", "503", "504", "RESOURCE_EXHAUSTED", "UNAVAILABLE", "INTERNAL", "DEADLINE_EXCEEDED", "overloaded", "try again" }
            .Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct, string? modelId = null)
    {
        for (var attempt = 0; ; attempt++)
        try
        {
            var (_, text, timedOut) = await CalculationAiAuditTimeoutRunner.RunAsync(async innerCt =>
            {
                var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
                var model = string.IsNullOrWhiteSpace(modelId) ? ModelId : modelId;
                var response = await _client.Models.GenerateContentAsync(model, prompt, config, innerCt);
                AiUsageLog.RecordGeneration(PurposeOf(prompt), model, response.UsageMetadata);
                return response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
            }, TimeSpan.FromSeconds(timeoutSeconds), ct);
            return timedOut
                ? new(false, string.Empty, $"Vertex AI call timed out after {timeoutSeconds}s.")
                : new(true, text ?? string.Empty, null);
        }
        catch (Exception ex) when (attempt < TransientRetryDelays.Length && IsTransient(ex) && !ct.IsCancellationRequested)
        {
            // Jittered, so the parallel calls of a run do not all come back at the same instant.
            var delay = TransientRetryDelays[attempt] + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
            _logger.LogInformation("Litigation Gemini call hit a passing error ({Error}); trying again in {Seconds:0}s", ex.Message, delay.TotalSeconds);
            await Task.Delay(delay, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Litigation Gemini analysis call failed");
            return new(false, string.Empty, ex.Message);
        }
    }
}
