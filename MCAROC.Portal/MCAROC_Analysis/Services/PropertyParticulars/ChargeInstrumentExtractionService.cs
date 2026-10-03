using System.Threading.Channels;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.PropertyParticulars;

public sealed class ChargeInstrumentExtractionOptions
{
    public const string SectionName = "ChargeInstrumentExtraction";
    /// <summary>Master switch. Even when true, nothing is scheduled unless Vertex AI is configured
    /// (GoogleCloud:ProjectId and GoogleCloud:CredentialsPath).</summary>
    public bool Enabled { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
    /// <summary>A deed can run to hundreds of pages, so the call gets longer than a short particulars text does.</summary>
    public int TimeoutSeconds { get; set; } = 240;
    /// <summary>A document longer than this (characters of extracted text) is not sent; it fails with that reason rather than
    /// being cut, since a quote from half a document would silently miss the rest.</summary>
    public int MaxDocumentChars { get; set; } = 600_000;
}

/// <summary>Wake-up queue only; the durable state is the ChargeInstrumentExtractions table, which startup recovery re-reads
/// after any restart.</summary>
public sealed class ChargeInstrumentExtractionQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long extractionId) => _channel.Writer.TryWrite(extractionId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>#364 part 2: reads the property passages out of the charge documents that #377 linked to a charge — deeds,
/// instruments, scanned CHG-1 forms and letters — when their property is not in readable form data (those are read exactly by
/// <see cref="ChargeForms"/>). One extraction per document: an open charge's and a satisfied charge's documents alike, since a
/// past charge traces assets the company holds or held. Every passage is grounded in the document's own text and carries the page
/// it starts on. Processing is lease-fenced like the other Gemini extractions: a stale worker can never publish.</summary>
public class ChargeInstrumentExtractionService(
    AppDbContext db, ChargeInstrumentExtractionQueue queue, IOptions<ChargeInstrumentExtractionOptions> options,
    IConfiguration configuration, IServiceProvider services, ILogger<ChargeInstrumentExtractionService> logger)
{
    private int LeaseSeconds => options.Value.TimeoutSeconds + 60;

    /// <summary>False when disabled or when Vertex AI isn't configured — then nothing is scheduled.</summary>
    public bool IsActive =>
        options.Value.Enabled
        && !string.IsNullOrWhiteSpace(configuration["GoogleCloud:ProjectId"])
        && !string.IsNullOrWhiteSpace(configuration["GoogleCloud:CredentialsPath"]);

    /// <summary>Creates an extraction for every document of the batch that is linked to a charge and has none yet. A document
    /// whose form data is read exactly (XFA) is skipped. A document that is the same PDF as one already extracted in this request
    /// (a refreshed batch holds the same files) takes that result with no model call. Idempotent: an existing row (any status)
    /// is left alone, and a concurrent scheduler losing the unique-index race just skips that document. Returns rows created.</summary>
    public virtual async Task<int> ScheduleForBatchAsync(long batchId, CancellationToken ct)
    {
        if (!IsActive) return 0;

        var linkedIds = await db.ChargeDocumentLinks.AsNoTracking().Where(l => l.BatchId == batchId)
            .Select(l => l.FilingDocumentId).Distinct().ToListAsync(ct);
        if (linkedIds.Count == 0) return 0;

        var docs = await db.McaFilingDocuments.AsNoTracking()
            .Where(d => linkedIds.Contains(d.FilingDocumentId) && d.ExtractedTextPath != null && d.TextExtractionMethod != TextExtractionMethod.Xfa)
            .Select(d => new { d.FilingDocumentId, d.RequestId, d.FileHash, d.ExtractedTextPath })
            .ToListAsync(ct);
        var have = (await db.ChargeInstrumentExtractions.AsNoTracking()
            .Where(x => x.PromptVersion == ChargeInstrumentAi.PromptVersion && linkedIds.Contains(x.FilingDocumentId))
            .Select(x => x.FilingDocumentId).ToListAsync(ct)).ToHashSet();

        var created = 0;
        foreach (var d in docs.Where(d => !have.Contains(d.FilingDocumentId)))
        {
            // A form read exactly from its own data is part 1's; nothing to quote.
            if (await XfaFormReader.ReadSidecarAsync(d.ExtractedTextPath!, ct) is { Count: > 0 }) continue;

            var earlier = string.IsNullOrEmpty(d.FileHash) ? null : await (
                from x in db.ChargeInstrumentExtractions.AsNoTracking()
                join f in db.McaFilingDocuments.AsNoTracking() on x.FilingDocumentId equals f.FilingDocumentId
                where x.RequestId == d.RequestId && f.FileHash == d.FileHash && x.PromptVersion == ChargeInstrumentAi.PromptVersion
                    && x.Status == ChargeInstrumentExtractionStatus.Completed && x.ResultJson != null
                orderby x.ChargeInstrumentExtractionId descending
                select new { x.ChargeInstrumentExtractionId, x.ResultJson, x.RejectedFieldsJson }).FirstOrDefaultAsync(ct);

            var row = new ChargeInstrumentExtraction
            {
                RequestId = d.RequestId, FilingDocumentId = d.FilingDocumentId, PromptVersion = ChargeInstrumentAi.PromptVersion,
                ModelId = PropertyParticularsAi.ModelId, CreatedUtc = DateTime.UtcNow
            };
            if (earlier is not null)
            {
                row.Status = ChargeInstrumentExtractionStatus.Completed;
                row.ResultJson = earlier.ResultJson;
                row.RejectedFieldsJson = earlier.RejectedFieldsJson;
                row.ReusedFromExtractionId = earlier.ChargeInstrumentExtractionId;
                row.CompletedUtc = DateTime.UtcNow;
            }
            db.ChargeInstrumentExtractions.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                if (earlier is null) queue.Enqueue(row.ChargeInstrumentExtractionId);
                created++;
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
            {
                db.Entry(row).State = EntityState.Detached; // another scheduler created it first
            }
        }
        return created;
    }

    /// <summary>Schedules the request's authoritative filing batch (see <see cref="ScheduleForBatchAsync"/>), so a prompt-version
    /// change re-reads the documents and a batch whose links were already built still gets its extractions. Cheap when nothing is
    /// missing: three queries.</summary>
    public virtual async Task<int> ScheduleForRequestAsync(long requestId, CancellationToken ct)
    {
        if (!IsActive) return 0;
        var batch = await McaFilingBatchResolver.GetAuthoritativeBatchAsync(db, requestId, ct);
        return batch is null || batch.Status is not (FilingBatchStatus.Completed or FilingBatchStatus.CompletedWithErrors)
            ? 0
            : await ScheduleForBatchAsync(batch.BatchId, ct);
    }

    /// <summary>The completed passages for these documents, keyed by document id — what the charge drawer reads.</summary>
    public static async Task<IReadOnlyDictionary<long, ChargeInstrumentAiResult>> LoadAsync(
        AppDbContext db, IReadOnlyCollection<long> filingDocumentIds, CancellationToken ct)
    {
        var result = new Dictionary<long, ChargeInstrumentAiResult>();
        if (filingDocumentIds.Count == 0) return result;
        var rows = await db.ChargeInstrumentExtractions.AsNoTracking()
            .Where(x => x.PromptVersion == ChargeInstrumentAi.PromptVersion && x.Status == ChargeInstrumentExtractionStatus.Completed
                && filingDocumentIds.Contains(x.FilingDocumentId))
            .Select(x => new { x.FilingDocumentId, x.ResultJson }).ToListAsync(ct);
        foreach (var r in rows)
            if (ChargeInstrumentAi.Deserialize(r.ResultJson) is { Passages.Count: > 0 } parsed)
                result[r.FilingDocumentId] = parsed;
        return result;
    }

    public virtual async Task ProcessAsync(long extractionId, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var claimed = await db.ChargeInstrumentExtractions
            .Where(x => x.ChargeInstrumentExtractionId == extractionId && x.Status == ChargeInstrumentExtractionStatus.Pending
                && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, ChargeInstrumentExtractionStatus.InProgress)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LeaseToken, token)
                .SetProperty(x => x.LeaseExpiresUtc, now.AddSeconds(LeaseSeconds)), ct);
        if (claimed == 0) return;

        // From here the row is claimed: whatever goes wrong — reading the document, an unreadable answer, a database blip — must
        // reach the fenced retry/failure path, never leave it InProgress with nothing to wake it.
        try
        {
            await ProcessClaimedAsync(extractionId, token, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Charge instrument extraction {Id} failed while processing", extractionId);
            var attempts = await db.ChargeInstrumentExtractions.AsNoTracking()
                .Where(x => x.ChargeInstrumentExtractionId == extractionId).Select(x => x.AttemptCount).FirstOrDefaultAsync(CancellationToken.None);
            await FailOrRetryAsync(new ChargeInstrumentExtraction { ChargeInstrumentExtractionId = extractionId, AttemptCount = attempts }, token,
                $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task ProcessClaimedAsync(long extractionId, Guid token, CancellationToken ct)
    {
        var row = await db.ChargeInstrumentExtractions.AsNoTracking().SingleAsync(x => x.ChargeInstrumentExtractionId == extractionId, ct);
        var textPath = await db.McaFilingDocuments.AsNoTracking().Where(d => d.FilingDocumentId == row.FilingDocumentId)
            .Select(d => d.ExtractedTextPath).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(textPath) || !File.Exists(textPath))
        {
            await FinishAsync(row, token, ChargeInstrumentExtractionStatus.Failed, null, null, null, "The document's extracted text is not available.", ct);
            return;
        }
        var documentText = await File.ReadAllTextAsync(textPath, ct);
        if (documentText.Length > options.Value.MaxDocumentChars)
        {
            await FinishAsync(row, token, ChargeInstrumentExtractionStatus.Failed, null, null, null,
                $"The document's text ({documentText.Length:N0} characters) is longer than the {options.Value.MaxDocumentChars:N0} limit.", ct);
            return;
        }

        PropertyParticularsAiCallResult response;
        try
        {
            var client = services.GetRequiredService<IPropertyParticularsAiClient>();
            response = await client.CallAsync(ChargeInstrumentAi.BuildPrompt(documentText), options.Value.TimeoutSeconds, ct);
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

        var validation = ChargeInstrumentAi.Validate(response.RawResponse, documentText);
        if (!validation.IsAccepted)
        {
            // Malformed JSON (a quote or bracket from garbled scanned text breaking the answer) or an answer of the wrong shape:
            // the model's next attempt is usually well formed, so try again up to MaxAttempts before giving up.
            await FailOrRetryAsync(row, token, validation.FailureReason ?? "The answer could not be read.", ct);
            return;
        }
        await FinishAsync(row, token,
            validation.IsAccepted ? ChargeInstrumentExtractionStatus.Completed : ChargeInstrumentExtractionStatus.Failed,
            response.RawResponse,
            validation.Result is null ? null : ChargeInstrumentAi.Serialize(validation.Result),
            validation.Rejected.Count == 0 ? null : JsonSerializer.Serialize(validation.Rejected),
            validation.FailureReason, ct);
        if (validation.Rejected.Count > 0)
            logger.LogInformation("Charge instrument extraction {Id}: {Count} ungrounded passage(s) dropped.", extractionId, validation.Rejected.Count);
    }

    private async Task FinishAsync(ChargeInstrumentExtraction row, Guid token, ChargeInstrumentExtractionStatus status,
        string? raw, string? resultJson, string? rejectedJson, string? failure, CancellationToken ct)
    {
        var published = await db.ChargeInstrumentExtractions
            .Where(x => x.ChargeInstrumentExtractionId == row.ChargeInstrumentExtractionId && x.LeaseToken == token
                && x.Status == ChargeInstrumentExtractionStatus.InProgress)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.RawResponseJson, raw)
                .SetProperty(x => x.ResultJson, resultJson)
                .SetProperty(x => x.RejectedFieldsJson, rejectedJson)
                .SetProperty(x => x.FailureReason, failure)
                .SetProperty(x => x.CompletedUtc, DateTime.UtcNow)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct);
        if (published == 0)
            logger.LogInformation("Charge instrument extraction {Id} lease lost; result not published.", row.ChargeInstrumentExtractionId);
    }

    private async Task FailOrRetryAsync(ChargeInstrumentExtraction row, Guid token, string reason, CancellationToken ct)
    {
        // row was read after the claim, so AttemptCount already includes this attempt.
        var retry = row.AttemptCount < options.Value.MaxAttempts;
        var next = DateTime.UtcNow.AddSeconds(10 * Math.Pow(3, row.AttemptCount - 1));
        var updated = await db.ChargeInstrumentExtractions
            .Where(x => x.ChargeInstrumentExtractionId == row.ChargeInstrumentExtractionId && x.LeaseToken == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, retry ? ChargeInstrumentExtractionStatus.Pending : ChargeInstrumentExtractionStatus.Failed)
                .SetProperty(x => x.FailureReason, reason.Length > 1000 ? reason[..1000] : reason)
                .SetProperty(x => x.NextAttemptUtc, retry ? next : (DateTime?)null)
                .SetProperty(x => x.CompletedUtc, retry ? (DateTime?)null : DateTime.UtcNow)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct);
        if (updated == 1 && retry) ScheduleWake(row.ChargeInstrumentExtractionId, next, ct);
    }

    /// <summary>Startup: expired InProgress rows go back to Pending; every Pending row is woken (now or when due).</summary>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.ChargeInstrumentExtractions
            .Where(x => x.Status == ChargeInstrumentExtractionStatus.InProgress && (x.LeaseExpiresUtc == null || x.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChargeInstrumentExtractionStatus.Pending).SetProperty(x => x.LeaseToken, (Guid?)null), ct);
        var pending = await db.ChargeInstrumentExtractions
            .Where(x => x.Status == ChargeInstrumentExtractionStatus.Pending)
            .Select(x => new { x.ChargeInstrumentExtractionId, x.NextAttemptUtc }).ToListAsync(ct);
        foreach (var p in pending) ScheduleWake(p.ChargeInstrumentExtractionId, p.NextAttemptUtc ?? now, ct);
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

public sealed class ChargeInstrumentExtractionWorker(IServiceScopeFactory scopes, ChargeInstrumentExtractionQueue queue,
    ILogger<ChargeInstrumentExtractionWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<ChargeInstrumentExtractionService>().RecoverAsync(stoppingToken);
            if (count > 0) logger.LogInformation("Recovered {Count} charge instrument extraction(s)", count);
        }
        catch (Exception ex) { logger.LogError(ex, "Charge instrument extraction startup recovery failed"); }
        await foreach (var id in queue.ReadAllAsync(stoppingToken)) _ = HandleAsync(id, stoppingToken);
    }

    private async Task HandleAsync(long id, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ChargeInstrumentExtractionService>().ProcessAsync(id, ct);
        }
        catch (Exception ex) { logger.LogError(ex, "Unhandled charge instrument extraction failure for {Id}", id); }
        finally { _concurrency.Release(); }
    }
}
