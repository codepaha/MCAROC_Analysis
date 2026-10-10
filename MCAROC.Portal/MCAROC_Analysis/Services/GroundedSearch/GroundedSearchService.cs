using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Services.AiUsage;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.GroundedSearch;

public sealed record GroundedSource(string Title, string Uri);

/// <param name="QuotaExhausted">The month's search allowance is used up; nothing was sent.</param>
public sealed record GroundedSearchResult(
    bool Success, string Text, IReadOnlyList<string> Queries, IReadOnlyList<GroundedSource> Sources, string? FailureReason, bool QuotaExhausted = false);

public interface IGroundedSearchService
{
    /// <summary>Answers <paramref name="prompt"/> using Google Search results and returns the sources it drew on. Refused (QuotaExhausted) once the month's
    /// query allowance is spent. The answer is plain text: the search tool cannot be combined with a JSON-only reply, so callers that need structure
    /// should pass the text to a second (cheap, ungrounded) call.</summary>
    Task<GroundedSearchResult> SearchAsync(string prompt, CancellationToken ct);
}

/// <summary>Google Search grounding on Vertex AI for adverse-news and sentiment lookups. Only Gemini models can ground; the calls are counted against
/// <see cref="GroundedSearchOptions.MonthlyQueryLimit"/> through <see cref="GroundingQuota"/> so the free allowance is never exceeded.</summary>
public sealed class GroundedSearchService : IGroundedSearchService
{
    private readonly GenAiClient _client;
    private readonly GroundingQuota _quota;
    private readonly IOptions<GroundedSearchOptions> _options;
    private readonly ILogger<GroundedSearchService> _logger;

    public GroundedSearchService(string projectId, string credentialsPath, GroundingQuota quota, IOptions<GroundedSearchOptions> options,
        ILogger<GroundedSearchService> logger)
    {
        _quota = quota;
        _options = options;
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        // The Gemini 3.x models are served from the global location only.
        _client = new GenAiClient(vertexAI: true, project: projectId, location: "global", credential: credential);
    }

    public async Task<GroundedSearchResult> SearchAsync(string prompt, CancellationToken ct)
    {
        var reserved = await _quota.TryReserveAsync(ct);
        if (reserved is null)
        {
            _logger.LogWarning("Grounded search refused: the monthly allowance of {Limit} queries is used up.", _quota.Limit);
            return new(false, "", [], [], "The monthly web-search allowance has been used up; searches resume next month.", QuotaExhausted: true);
        }

        var queries = 0;
        var model = _options.Value.Model;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.Value.TimeoutSeconds));
            // The API cannot cap how many queries a call runs, so the model is told the limit; the quota reserves the same number up front.
            var reserve = _options.Value.ReservedQueriesPerCall;
            var config = new GenerateContentConfig
            {
                Temperature = 0, Tools = [new Tool { GoogleSearch = new GoogleSearch() }],
                SystemInstruction = new Content { Parts = [new Part { Text = $"Run at most {reserve} web searches in total. Combine related questions into one search." }] }
            };
            var response = await _client.Models.GenerateContentAsync(model, prompt, config, timeout.Token);
            var candidate = response.Candidates?.FirstOrDefault();
            var metadata = candidate?.GroundingMetadata;
            var searched = metadata?.WebSearchQueries ?? [];
            queries = searched.Count;
            if (queries > reserved.Value)
                _logger.LogWarning("Grounded search ran {Queries} queries against a reserve of {Reserved}; the extra are counted against the month.", queries, reserved.Value);
            AiUsageLog.RecordGeneration(AiUsagePurposes.GroundedSearch, model, response.UsageMetadata, groundingQueries: queries);
            var text = string.Concat(candidate?.Content?.Parts?.Where(p => p.Thought != true).Select(p => p.Text) ?? []);
            var sources = (metadata?.GroundingChunks ?? [])
                .Where(c => c.Web?.Uri is not null)
                .Select(c => new GroundedSource(c.Web!.Title ?? c.Web.Uri!, c.Web.Uri!))
                .DistinctBy(s => s.Uri)
                .ToList();
            return new(true, text, searched.ToList(), sources, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Grounded search call failed");
            return new(false, "", [], [], "The web search did not complete.");
        }
        finally
        {
            _quota.Complete(reserved.Value, queries);
        }
    }
}
