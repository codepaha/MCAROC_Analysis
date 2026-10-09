using Google.GenAI.Types;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>Records the usage of every paid model call. The clients call this after each response; it only queues a row (see <see cref="AiUsageQueue"/>),
/// so accounting can never fail or delay the work it measures. Until <see cref="Configure"/> runs (unit tests, tools) it does nothing.</summary>
public static class AiUsageLog
{
    private static AiUsageQueue? _queue;

    public static void Configure(AiUsageQueue? queue) => _queue = queue;

    /// <summary>A generation. <paramref name="usage"/> is what the provider reported with the response (null when it reported none).</summary>
    public static void RecordGeneration(string defaultPurpose, string model, GenerateContentResponseUsageMetadata? usage, bool succeeded = true, int groundingQueries = 0) =>
        Write(new AiUsageRecord
        {
            Purpose = AiUsageContext.Purpose ?? defaultPurpose, Model = model, Succeeded = succeeded,
            InputTokens = usage?.PromptTokenCount ?? 0, OutputTokens = usage?.CandidatesTokenCount ?? 0, ThoughtTokens = usage?.ThoughtsTokenCount ?? 0,
            TokensEstimated = usage is null, GroundingQueries = groundingQueries
        });

    /// <summary>An embedding batch. <paramref name="reportedTokens"/> is the token count the provider reported for the batch; without it the tokens are
    /// estimated from the billable characters (about four characters a token) and the row is marked estimated, so the report never shows it as exact.</summary>
    public static void RecordEmbedding(string defaultPurpose, string model, long billableCharacters, int? reportedTokens = null) =>
        Write(new AiUsageRecord
        {
            Purpose = AiUsageContext.Purpose ?? defaultPurpose, Model = model, BillableCharacters = billableCharacters,
            InputTokens = reportedTokens ?? (int)Math.Min(int.MaxValue, (billableCharacters + 3) / 4),
            TokensEstimated = reportedTokens is null
        });

    private static void Write(AiUsageRecord record)
    {
        var queue = _queue;
        if (queue is null) return;
        record.RequestId = AiUsageContext.RequestId;   // read here, in the calling context
        record.CreatedUtc = DateTime.UtcNow;
        queue.Enqueue(record);
    }
}
