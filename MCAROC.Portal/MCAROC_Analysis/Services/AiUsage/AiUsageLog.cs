using Google.GenAI.Types;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>Records the usage of every paid model call. The clients call this after each response; it never throws and never delays the call (the
/// row is written in the background), so accounting can fail without failing the work it measures. Until <see cref="Configure(IServiceScopeFactory, ILogger)"/>
/// runs (unit tests, tools) it does nothing.</summary>
public static class AiUsageLog
{
    private static Func<AiUsageRecord, Task>? _writer;
    private static ILogger? _logger;

    /// <summary>Writes through a fresh scope per record, so it is safe from any thread and from the singleton clients.</summary>
    public static void Configure(IServiceScopeFactory scopes, ILogger logger)
    {
        _logger = logger;
        _writer = async record =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiUsageRecords.Add(record);
            await db.SaveChangesAsync();
        };
    }

    /// <summary>Test seam: replaces the database writer.</summary>
    public static void Configure(Func<AiUsageRecord, Task>? writer, ILogger? logger = null)
    {
        _writer = writer;
        _logger = logger;
    }

    /// <summary>A generation. <paramref name="usage"/> is what the provider reported with the response (null when the call failed before one).</summary>
    public static void RecordGeneration(string defaultPurpose, string model, GenerateContentResponseUsageMetadata? usage, bool succeeded = true) =>
        Write(new AiUsageRecord
        {
            Purpose = AiUsageContext.Purpose ?? defaultPurpose, Model = model, Succeeded = succeeded,
            InputTokens = usage?.PromptTokenCount ?? 0, OutputTokens = usage?.CandidatesTokenCount ?? 0, ThoughtTokens = usage?.ThoughtsTokenCount ?? 0
        });

    /// <summary>An embedding batch. Tokens are estimated from the billable characters when the provider reports no count (about four characters a token).</summary>
    public static void RecordEmbedding(string defaultPurpose, string model, long billableCharacters, int? reportedTokens = null) =>
        Write(new AiUsageRecord
        {
            Purpose = AiUsageContext.Purpose ?? defaultPurpose, Model = model, BillableCharacters = billableCharacters,
            InputTokens = reportedTokens ?? (int)Math.Min(int.MaxValue, (billableCharacters + 3) / 4)
        });

    private static void Write(AiUsageRecord record)
    {
        var writer = _writer;
        if (writer is null) return;
        record.RequestId = AiUsageContext.RequestId;   // read here, in the calling context, not inside the background task
        record.CreatedUtc = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try { await writer(record); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Could not record AI usage for {Purpose}", record.Purpose); }
        });
    }
}
