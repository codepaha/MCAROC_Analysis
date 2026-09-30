using System.Text.Json;
using System.Threading.Channels;
using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.PropertyParticulars;

public sealed class PropertyParticularsExtractionOptions
{
    public const string SectionName = "PropertyParticularsExtraction";
    /// <summary>Master switch. Even when true, nothing is scheduled unless Vertex AI is configured
    /// (GoogleCloud:ProjectId and GoogleCloud:CredentialsPath).</summary>
    public bool Enabled { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed record PropertyParticularsAiCallResult(bool Success, string RawResponse, string? FailureReason);

/// <summary>Seam around Vertex AI so extraction can be tested without credentials.</summary>
public interface IPropertyParticularsAiClient
{
    Task<PropertyParticularsAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct);
}

public sealed class VertexPropertyParticularsAiClient : IPropertyParticularsAiClient
{
    private readonly GenAiClient _client;
    private readonly ILogger<VertexPropertyParticularsAiClient> _logger;

    public VertexPropertyParticularsAiClient(string projectId, string location, string credentialsPath, ILogger<VertexPropertyParticularsAiClient> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    public async Task<PropertyParticularsAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var (_, text, timedOut) = await CalculationAiAuditTimeoutRunner.RunAsync(async innerCt =>
            {
                var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
                var response = await _client.Models.GenerateContentAsync(PropertyParticularsAi.ModelId, prompt, config, innerCt);
                return response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
            }, TimeSpan.FromSeconds(timeoutSeconds), ct);
            return timedOut
                ? new(false, string.Empty, $"Vertex AI call timed out after {timeoutSeconds}s.")
                : new(true, text ?? string.Empty, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Property particulars Gemini extraction call failed");
            return new(false, string.Empty, ex.Message);
        }
    }
}

/// <summary>Wake-up queue only; the durable state is the PropertyParticularsExtractions table, which startup recovery
/// re-reads after any restart.</summary>
public sealed class PropertyParticularsExtractionQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long extractionId) => _channel.Writer.TryWrite(extractionId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Schedules and runs the Gemini property-particulars extraction. Scheduling is per request (after each
/// ingestion, and lazily for requests ingested before this existed) but storage is per distinct text, so wording
/// repeated across modifications or requests costs one call. Processing is lease-fenced like the litigation AI
/// runs: a stale worker can never publish, and a lost lease or restart simply leaves the row claimable again.</summary>
public class PropertyParticularsExtractionService(
    AppDbContext db, PropertyParticularsExtractionQueue queue, IOptions<PropertyParticularsExtractionOptions> options,
    IConfiguration configuration, IServiceProvider services, ILogger<PropertyParticularsExtractionService> logger)
{
    private int LeaseSeconds => options.Value.TimeoutSeconds + 60;

    /// <summary>False when disabled or when Vertex AI isn't configured — then nothing is scheduled and the UI stays on
    /// the deterministic reading, rather than piling up rows that can only fail.</summary>
    public bool IsActive =>
        options.Value.Enabled
        && !string.IsNullOrWhiteSpace(configuration["GoogleCloud:ProjectId"])
        && !string.IsNullOrWhiteSpace(configuration["GoogleCloud:CredentialsPath"]);

    /// <summary>Creates a Pending extraction for every distinct, not-yet-extracted particulars text in the request's
    /// charges and wakes the worker. Idempotent: an existing row (any status) for the same text is left alone, and a
    /// concurrent scheduler losing the unique-index race just skips that text. Returns how many rows it created.</summary>
    public virtual async Task<int> ScheduleForRequestAsync(long requestId, CancellationToken ct)
    {
        if (!IsActive) return 0;

        // Only the request's current (latest completed) ingestion — a superseded run's wording is never shown.
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);
        if (runId is null) return 0;
        var texts = await db.RocChargeEvents.AsNoTracking()
            .Where(e => e.RequestId == requestId && e.IngestionRunId == runId && e.PropertyParticulars != null)
            .Select(e => new { e.PropertyParticulars, e.PropertyType })
            .ToListAsync(ct);
        var byHash = texts
            .Where(t => PropertyParticularsAi.IsExtractable(t.PropertyParticulars))
            .GroupBy(t => PropertyParticularsAi.HashOf(t.PropertyParticulars!, t.PropertyType))
            .ToDictionary(g => g.Key, g => g.First());
        if (byHash.Count == 0) return 0;

        var hashes = byHash.Keys.ToList();
        var existing = await db.PropertyParticularsExtractions.AsNoTracking()
            .Where(x => x.PromptVersion == PropertyParticularsAi.PromptVersion && hashes.Contains(x.TextHash))
            .Select(x => x.TextHash).ToListAsync(ct);

        var created = 0;
        foreach (var (hash, text) in byHash.Where(kv => !existing.Contains(kv.Key)))
        {
            var row = new PropertyParticularsExtraction
            {
                TextHash = hash, PromptVersion = PropertyParticularsAi.PromptVersion, ModelId = PropertyParticularsAi.ModelId,
                SourceText = text.PropertyParticulars!, PropertyType = text.PropertyType, CreatedUtc = DateTime.UtcNow
            };
            db.PropertyParticularsExtractions.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                queue.Enqueue(row.PropertyParticularsExtractionId);
                created++;
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
            {
                db.Entry(row).State = EntityState.Detached; // another scheduler created it first
            }
        }
        return created;
    }

    /// <summary>Completed extractions for the texts in these charge events, keyed by text hash — what the charge drawer
    /// and dossier read. Only hashes of the given texts are ever queried.</summary>
    public static async Task<IReadOnlyDictionary<string, PropertyParticularsExtraction>> LoadCompletedAsync(
        AppDbContext db, IEnumerable<RocChargeEvent> events, CancellationToken ct)
    {
        var hashes = events.Where(e => PropertyParticularsAi.IsExtractable(e.PropertyParticulars))
            .Select(e => PropertyParticularsAi.HashOf(e.PropertyParticulars!, e.PropertyType)).Distinct().ToList();
        if (hashes.Count == 0) return new Dictionary<string, PropertyParticularsExtraction>();
        return await db.PropertyParticularsExtractions.AsNoTracking()
            .Where(x => x.PromptVersion == PropertyParticularsAi.PromptVersion && x.Status == PropertyParticularsExtractionStatus.Completed
                && hashes.Contains(x.TextHash))
            .ToDictionaryAsync(x => x.TextHash, ct);
    }

    public virtual async Task ProcessAsync(long extractionId, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var claimed = await db.PropertyParticularsExtractions
            .Where(x => x.PropertyParticularsExtractionId == extractionId && x.Status == PropertyParticularsExtractionStatus.Pending
                && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, PropertyParticularsExtractionStatus.InProgress)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LeaseToken, token)
                .SetProperty(x => x.LeaseExpiresUtc, now.AddSeconds(LeaseSeconds)), ct);
        if (claimed == 0) return;

        var row = await db.PropertyParticularsExtractions.AsNoTracking().SingleAsync(x => x.PropertyParticularsExtractionId == extractionId, ct);
        PropertyParticularsAiCallResult response;
        try
        {
            var client = services.GetRequiredService<IPropertyParticularsAiClient>();
            response = await client.CallAsync(PropertyParticularsAi.BuildPrompt(row.SourceText, row.PropertyType), options.Value.TimeoutSeconds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response = new(false, string.Empty, ex.Message);
        }

        if (!response.Success)
        {
            await FailOrRetryAsync(row, token, response.FailureReason ?? "Gemini call failed.", ct);
            return;
        }

        var validation = PropertyParticularsAi.Validate(response.RawResponse, row.SourceText);
        var status = validation.IsAccepted ? PropertyParticularsExtractionStatus.Completed : PropertyParticularsExtractionStatus.Failed;
        var published = await db.PropertyParticularsExtractions
            .Where(x => x.PropertyParticularsExtractionId == extractionId && x.LeaseToken == token && x.Status == PropertyParticularsExtractionStatus.InProgress)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.RawResponseJson, response.RawResponse)
                .SetProperty(x => x.ResponseHash, string.IsNullOrWhiteSpace(response.RawResponse) ? null : PropertyParticularsAi.ComputeHash(response.RawResponse))
                .SetProperty(x => x.ExtractionJson, validation.Result is null ? null : PropertyParticularsAi.Serialize(validation.Result))
                .SetProperty(x => x.RejectedFieldsJson, validation.RejectedFields.Count == 0 ? null : JsonSerializer.Serialize(validation.RejectedFields))
                .SetProperty(x => x.FailureReason, validation.FailureReason)
                .SetProperty(x => x.CompletedUtc, DateTime.UtcNow)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct);
        if (published == 0)
            logger.LogInformation("Property particulars extraction {Id} lease lost; result not published.", extractionId);
        else if (validation.RejectedFields.Count > 0)
            logger.LogInformation("Property particulars extraction {Id}: {Count} ungrounded field(s) dropped.", extractionId, validation.RejectedFields.Count);
    }

    private async Task FailOrRetryAsync(PropertyParticularsExtraction row, Guid token, string reason, CancellationToken ct)
    {
        // row was read after the claim, so AttemptCount already includes this attempt.
        var retry = row.AttemptCount < options.Value.MaxAttempts;
        var next = DateTime.UtcNow.AddSeconds(10 * Math.Pow(3, row.AttemptCount - 1));
        var updated = await db.PropertyParticularsExtractions
            .Where(x => x.PropertyParticularsExtractionId == row.PropertyParticularsExtractionId && x.LeaseToken == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, retry ? PropertyParticularsExtractionStatus.Pending : PropertyParticularsExtractionStatus.Failed)
                .SetProperty(x => x.FailureReason, reason.Length > 1000 ? reason[..1000] : reason)
                .SetProperty(x => x.NextAttemptUtc, retry ? next : (DateTime?)null)
                .SetProperty(x => x.CompletedUtc, retry ? (DateTime?)null : DateTime.UtcNow)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct);
        if (updated == 1 && retry) ScheduleWake(row.PropertyParticularsExtractionId, next, ct);
    }

    /// <summary>Startup: expired InProgress rows go back to Pending; every Pending row is woken (now or when due).</summary>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.PropertyParticularsExtractions
            .Where(x => x.Status == PropertyParticularsExtractionStatus.InProgress && (x.LeaseExpiresUtc == null || x.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PropertyParticularsExtractionStatus.Pending).SetProperty(x => x.LeaseToken, (Guid?)null), ct);
        var pending = await db.PropertyParticularsExtractions
            .Where(x => x.Status == PropertyParticularsExtractionStatus.Pending)
            .Select(x => new { x.PropertyParticularsExtractionId, x.NextAttemptUtc }).ToListAsync(ct);
        foreach (var p in pending) ScheduleWake(p.PropertyParticularsExtractionId, p.NextAttemptUtc ?? now, ct);
        return pending.Count;
    }

    private void ScheduleWake(long id, DateTime due, CancellationToken ct)
    {
        var delay = due - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero) { queue.Enqueue(id); return; }
        var q = queue;
        _ = Task.Run(async () => { try { await Task.Delay(delay, ct); q.Enqueue(id); } catch (OperationCanceledException) { } }, ct);
    }
}

public sealed class PropertyParticularsExtractionWorker(IServiceScopeFactory scopes, PropertyParticularsExtractionQueue queue,
    ILogger<PropertyParticularsExtractionWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<PropertyParticularsExtractionService>().RecoverAsync(stoppingToken);
            if (count > 0) logger.LogInformation("Recovered {Count} property particulars extraction(s)", count);
        }
        catch (Exception ex) { logger.LogError(ex, "Property particulars extraction startup recovery failed"); }
        await foreach (var id in queue.ReadAllAsync(stoppingToken)) _ = HandleAsync(id, stoppingToken);
    }

    private async Task HandleAsync(long id, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PropertyParticularsExtractionService>().ProcessAsync(id, ct);
        }
        catch (Exception ex) { logger.LogError(ex, "Unhandled property particulars extraction failure for {Id}", id); }
        finally { _concurrency.Release(); }
    }
}
