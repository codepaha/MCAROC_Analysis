using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.PropertyParticulars;

public sealed class OrderPropertyReadingOptions
{
    public const string SectionName = "OrderPropertyReading";

    /// <summary>Off, or without Vertex AI configured, nothing is scheduled and the pipeline does not wait for readings.</summary>
    public bool Enabled { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Model calls a day (IST) across all requests; past it the rest wait for the next day. Godrej (about 750 cases) needs about 170.</summary>
    public int MaxCallsPerDay { get; set; } = 3000;
    public int Concurrency { get; set; } = 3;
    public int ScanSeconds { get; set; } = 30;
    public int ScheduleBatch { get; set; } = 200;
}

/// <summary>What one order's reading is built from: the pages sent, the prompt, and the hash that lets an identical input reuse a reading.</summary>
public sealed record OrderPropertyInput(IReadOnlyList<OrderPage> Pages, string Prompt, string InputHash);

/// <summary>Schedules and runs the reading of the properties each court order names (#554). Scheduling is per order document of a case the request
/// shows; an order none of whose pages may name a property is settled without a call. Processing is lease-fenced like the charge property
/// extraction: a stale worker never publishes, and a lost lease or restart leaves the row claimable again. An input another order already had
/// (same pages, case and company) takes that reading without a call.</summary>
public sealed class OrderPropertyReadingService(
    AppDbContext db, IOptions<OrderPropertyReadingOptions> options, IConfiguration configuration, IServiceProvider services,
    ILogger<OrderPropertyReadingService> logger)
{
    private int LeaseSeconds => options.Value.TimeoutSeconds + 60;

    public bool IsActive => IsActiveFor(options.Value, configuration);

    public static bool IsActiveFor(OrderPropertyReadingOptions options, IConfiguration configuration) =>
        options.Enabled
        && !string.IsNullOrWhiteSpace(configuration["GoogleCloud:ProjectId"])
        && !string.IsNullOrWhiteSpace(configuration["GoogleCloud:CredentialsPath"]);

    /// <summary>The order documents a reading is due for: downloaded, with text, on a case the request shows, and not read yet.</summary>
    internal IQueryable<LitigationOrderDocument> Unread() =>
        db.LitigationOrderDocuments.AsNoTracking()
            .Where(d => d.Status == LitigationOrderDocumentStatus.Downloaded && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted
                && d.ExtractedText != null && d.Order!.CollapsedIntoOrderId == null && !d.Order.IsSuperseded
                && d.Order.Case!.MergedIntoCaseId == null
                && (d.Order.Case.AttributionOutcome == null || d.Order.Case.AttributionOutcome == LitigationAttributionOutcome.Attributed)
                && !db.LitigationOrderPropertyReadings.Any(r => r.LitigationOrderDocumentId == d.LitigationOrderDocumentId));

    /// <summary>Creates a reading row for up to <see cref="OrderPropertyReadingOptions.ScheduleBatch"/> unread orders. Idempotent: a concurrent
    /// scheduler losing the unique-index race skips that order. Returns the rows created that need a call.</summary>
    /// <param name="requestId">Only that request's orders (to read one company first); every request's when null.</param>
    public async Task<IReadOnlyList<long>> ScheduleAsync(CancellationToken ct, long? requestId = null)
    {
        if (!IsActive) return [];
        var unread = Unread();
        if (requestId is { } rid) unread = unread.Where(d => d.Order!.Case!.RequestId == rid);
        var documents = await unread.OrderBy(d => d.LitigationOrderDocumentId).Take(options.Value.ScheduleBatch)
            .Include(d => d.Order!).ThenInclude(o => o.Case!).ToListAsync(ct);
        var names = new Dictionary<long, IReadOnlyList<string>>();
        var pending = new List<long>();
        foreach (var d in documents)
        {
            var c = d.Order!.Case!;
            if (!names.TryGetValue(c.RequestId, out var companyNames)) names[c.RequestId] = companyNames = await ChargeLitigationService.CompanyNamesAsync(db, c.RequestId, ct);
            var input = BuildInput(d, c, companyNames);
            var row = new LitigationOrderPropertyReading
            {
                LitigationOrderDocumentId = d.LitigationOrderDocumentId, RequestId = c.RequestId, InputHash = input.InputHash,
                PromptVersion = OrderPropertiesAi.PromptVersion, ModelId = OrderPropertiesAi.ModelId, CreatedUtc = DateTime.UtcNow,
                PagesSentJson = JsonSerializer.Serialize(input.Pages.Select(p => p.Number)),
                Status = input.Pages.Count == 0 ? LitigationOrderPropertyReadingStatus.NoPropertyPages : LitigationOrderPropertyReadingStatus.Pending,
                CompletedUtc = input.Pages.Count == 0 ? DateTime.UtcNow : null
            };
            db.LitigationOrderPropertyReadings.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                if (row.Status == LitigationOrderPropertyReadingStatus.Pending) pending.Add(row.LitigationOrderPropertyReadingId);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
            {
                db.Entry(row).State = EntityState.Detached;   // another scheduler created it first
            }
        }
        return pending;
    }

    /// <summary>The pages that may name a property, and the prompt and hash built from them and the case.</summary>
    public static OrderPropertyInput BuildInput(LitigationOrderDocument document, LitigationCase c, IReadOnlyCollection<string> companyNames)
    {
        var pages = LitigationOrderAddressMatcher.PagesOf(document.ExtractedText ?? "")
            .Select(p => new OrderPage(p.PageNumber, p.Text)).Where(p => OrderPropertiesAi.MayNameProperty(p.Text)).ToList();
        var prompt = OrderPropertiesAi.BuildPrompt(companyNames, c.Court, c.CaseType,
            LitigationPartyNames.Parse(c.PetitionersJson), LitigationPartyNames.Parse(c.RespondentsJson), pages);
        // The prompt carries every input (pages, case, company, prompt version wording), so equal prompts are equal inputs.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OrderPropertiesAi.PromptVersion + "\n" + prompt)));
        return new OrderPropertyInput(pages, prompt, hash);
    }

    /// <summary>The Pending readings due now, oldest first.</summary>
    public async Task<IReadOnlyList<long>> DueAsync(int max, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.LitigationOrderPropertyReadings.AsNoTracking()
            .Where(x => x.Status == LitigationOrderPropertyReadingStatus.Pending && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now))
            .OrderBy(x => x.LitigationOrderPropertyReadingId).Select(x => x.LitigationOrderPropertyReadingId).Take(max).ToListAsync(ct);
    }

    public async Task ProcessAsync(long readingId, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var claimed = await db.LitigationOrderPropertyReadings
            .Where(x => x.LitigationOrderPropertyReadingId == readingId && x.Status == LitigationOrderPropertyReadingStatus.Pending
                && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, LitigationOrderPropertyReadingStatus.InProgress)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LeaseToken, token)
                .SetProperty(x => x.LeaseExpiresUtc, now.AddSeconds(LeaseSeconds)), ct);
        if (claimed == 0) return;
        var row = await db.LitigationOrderPropertyReadings.AsNoTracking().SingleAsync(x => x.LitigationOrderPropertyReadingId == readingId, ct);

        // Same input as an order already read: take that reading, no call.
        var same = await db.LitigationOrderPropertyReadings.AsNoTracking()
            .Where(x => x.InputHash == row.InputHash && x.PromptVersion == row.PromptVersion && x.Status == LitigationOrderPropertyReadingStatus.Completed
                && x.LitigationOrderPropertyReadingId != readingId)
            .OrderBy(x => x.LitigationOrderPropertyReadingId).FirstOrDefaultAsync(ct);
        if (same is not null)
        {
            await PublishAsync(readingId, token, LitigationOrderPropertyReadingStatus.Completed, same.RawResponseJson, same.ResultJson,
                same.RejectedFieldsJson, null, same.LitigationOrderPropertyReadingId, ct);
            return;
        }

        if (await CallsTodayAsync(ct) >= options.Value.MaxCallsPerDay)
        {
            await DeferAsync(row, token, Ist.NextMidnightUtc(DateTime.UtcNow), "Daily limit of order property readings reached; continues tomorrow.", ct);
            return;
        }

        var document = await db.LitigationOrderDocuments.AsNoTracking().Include(d => d.Order!).ThenInclude(o => o.Case!)
            .SingleOrDefaultAsync(d => d.LitigationOrderDocumentId == row.LitigationOrderDocumentId, ct);
        if (document?.ExtractedText is null)
        {
            await PublishAsync(readingId, token, LitigationOrderPropertyReadingStatus.Failed, null, null, null, "The order's text is no longer stored.", null, ct);
            return;
        }
        var input = BuildInput(document, document.Order!.Case!, await ChargeLitigationService.CompanyNamesAsync(db, row.RequestId, ct));
        if (input.Pages.Count == 0)
        {
            await PublishAsync(readingId, token, LitigationOrderPropertyReadingStatus.NoPropertyPages, null, null, null, null, null, ct);
            return;
        }

        PropertyParticularsAiCallResult response;
        try
        {
            response = await services.GetRequiredService<IPropertyParticularsAiClient>().CallAsync(input.Prompt, options.Value.TimeoutSeconds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response = new(false, string.Empty, ex.Message);
        }
        if (!response.Success)
        {
            await FailOrRetryAsync(row, token, response.FailureReason ?? "The call failed.", ct);
            return;
        }

        var validation = OrderPropertiesAi.Validate(response.RawResponse, input.Pages);
        await PublishAsync(readingId, token,
            validation.IsAccepted ? LitigationOrderPropertyReadingStatus.Completed : LitigationOrderPropertyReadingStatus.Failed,
            response.RawResponse, validation.Result is null ? null : OrderPropertiesAi.Serialize(validation.Result),
            validation.RejectedFields.Count == 0 ? null : JsonSerializer.Serialize(validation.RejectedFields), validation.FailureReason, null, ct);
        if (validation.RejectedFields.Count > 0)
            logger.LogInformation("Order property reading {Id}: {Count} ungrounded value(s) dropped.", readingId, validation.RejectedFields.Count);
    }

    /// <summary>Calls started today (IST): readings that were claimed and not answered from another reading.</summary>
    private async Task<int> CallsTodayAsync(CancellationToken ct)
    {
        var since = Ist.StartOfTodayUtc(DateTime.UtcNow);
        return await db.LitigationOrderPropertyReadings.AsNoTracking().CountAsync(x => x.ReusedFromReadingId == null && x.AttemptCount > 0
            && ((x.CompletedUtc != null && x.CompletedUtc >= since && x.RawResponseJson != null) || x.Status == LitigationOrderPropertyReadingStatus.InProgress), ct);
    }

    private async Task PublishAsync(long readingId, Guid token, LitigationOrderPropertyReadingStatus status, string? raw, string? result, string? rejected,
        string? failure, long? reusedFrom, CancellationToken ct)
    {
        var published = await db.LitigationOrderPropertyReadings
            .Where(x => x.LitigationOrderPropertyReadingId == readingId && x.LeaseToken == token && x.Status == LitigationOrderPropertyReadingStatus.InProgress)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.RawResponseJson, raw)
                .SetProperty(x => x.ResponseHash, string.IsNullOrWhiteSpace(raw) ? null : PropertyParticularsAi.ComputeHash(raw))
                .SetProperty(x => x.ResultJson, result)
                .SetProperty(x => x.RejectedFieldsJson, rejected)
                .SetProperty(x => x.FailureReason, failure)
                .SetProperty(x => x.ReusedFromReadingId, reusedFrom)
                .SetProperty(x => x.CompletedUtc, DateTime.UtcNow)
                .SetProperty(x => x.NextAttemptUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct);
        if (published == 0) logger.LogInformation("Order property reading {Id} lease lost; result not published.", readingId);
    }

    private async Task FailOrRetryAsync(LitigationOrderPropertyReading row, Guid token, string reason, CancellationToken ct)
    {
        // row was read after the claim, so AttemptCount already includes this attempt.
        if (row.AttemptCount < options.Value.MaxAttempts)
        {
            await DeferAsync(row, token, DateTime.UtcNow.AddSeconds(10 * Math.Pow(3, row.AttemptCount - 1)), reason, ct);
            return;
        }
        await PublishAsync(row.LitigationOrderPropertyReadingId, token, LitigationOrderPropertyReadingStatus.Failed, null, null, null,
            reason.Length > 1000 ? reason[..1000] : reason, null, ct);
    }

    private async Task DeferAsync(LitigationOrderPropertyReading row, Guid token, DateTime next, string reason, CancellationToken ct) =>
        await db.LitigationOrderPropertyReadings
            .Where(x => x.LitigationOrderPropertyReadingId == row.LitigationOrderPropertyReadingId && x.LeaseToken == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, LitigationOrderPropertyReadingStatus.Pending)
                .SetProperty(x => x.FailureReason, reason.Length > 1000 ? reason[..1000] : reason)
                .SetProperty(x => x.NextAttemptUtc, next)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct);

    /// <summary>Expired InProgress rows go back to Pending (a restart or a lost worker).</summary>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.LitigationOrderPropertyReadings
            .Where(x => x.Status == LitigationOrderPropertyReadingStatus.InProgress && (x.LeaseExpiresUtc == null || x.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LitigationOrderPropertyReadingStatus.Pending).SetProperty(x => x.LeaseToken, (Guid?)null), ct);
    }

    /// <summary>The completed readings of these order documents, by document.</summary>
    public static async Task<IReadOnlyDictionary<long, OrderPropertiesResult>> LoadAsync(AppDbContext db, IReadOnlyCollection<long> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0) return new Dictionary<long, OrderPropertiesResult>();
        var rows = await db.LitigationOrderPropertyReadings.AsNoTracking()
            .Where(x => documentIds.Contains(x.LitigationOrderDocumentId) && x.Status == LitigationOrderPropertyReadingStatus.Completed && x.ResultJson != null)
            .Select(x => new { x.LitigationOrderDocumentId, x.ResultJson }).ToListAsync(ct);
        return rows.Select(r => (r.LitigationOrderDocumentId, Result: OrderPropertiesAi.Deserialize(r.ResultJson)))
            .Where(r => r.Result is not null).ToDictionary(r => r.LitigationOrderDocumentId, r => r.Result!);
    }
}

/// <summary>Runs the scheduling and the readings: every <see cref="OrderPropertyReadingOptions.ScanSeconds"/>, recovers lost leases, schedules
/// newly readable orders and reads what is due, a few at a time.</summary>
public sealed class OrderPropertyReadingWorker(IServiceScopeFactory scopes, IOptions<OrderPropertyReadingOptions> options,
    ILogger<OrderPropertyReadingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Order property reading pass failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.ScanSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task PassAsync(CancellationToken ct)
    {
        IReadOnlyList<long> due;
        using (var scope = scopes.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<OrderPropertyReadingService>();
            if (!service.IsActive) return;
            await service.RecoverAsync(ct);
            await service.ScheduleAsync(ct);
            due = await service.DueAsync(options.Value.Concurrency * 20, ct);
        }
        // Each reading in its own scope (its own DbContext), a few at a time.
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.Concurrency), CancellationToken = ct },
            async (id, token) =>
            {
                using var scope = scopes.CreateScope();
                try { await scope.ServiceProvider.GetRequiredService<OrderPropertyReadingService>().ProcessAsync(id, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Order property reading {Id} failed", id); }
            });
    }
}
