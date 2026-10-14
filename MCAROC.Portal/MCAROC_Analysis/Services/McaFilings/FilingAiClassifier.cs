using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AiUsage;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.McaFilings;

public sealed record AiClassification(string FormType, FilingCategory Category, string TitleQuote);

/// <summary>
/// The fallback for the filing documents the rules cannot place (#499): mostly generic "Optional attachment" files. One small call reads the top of the
/// document, its file name and folder, and must pick a type from <see cref="FilingClassifier.KnownTypes"/> and quote the title line it read. The answer
/// counts only if the type is on that list (its category comes from the list, never from the answer) and the quote is on the page; otherwise the document
/// stays as it was. The same PDF elsewhere (same file hash) reuses the answer; a document is tried once. Labels only, like <see cref="FilingReclassifier"/>:
/// no extraction is set off.
/// </summary>
public sealed partial class FilingAiClassifier(AppDbContext db, IServiceProvider services, ILogger<FilingAiClassifier> logger)
{
    public const string PromptMarker = "MCAROC DOCUMENT TYPE";
    public const string PromptVersion = "document-type-v2";
    public const string Method = "AiTitle";
    /// <summary>Only the top of the page is sent: the title is there, the rest would only cost tokens.</summary>
    internal const int TextChars = 1500;
    /// <summary>A page with less text than this is not worth a call.</summary>
    internal const int MinTextChars = 40;
    private const int MinQuoteChars = 8;
    private const int TimeoutSeconds = 60;

    private static readonly string[] Unplaced = ["NoMatch", "OuterFolderFallback"];

    /// <summary>Classifies up to <paramref name="batchSize"/> unplaced documents, making at most what is left of <paramref name="maxCallsPerDay"/> calls (counted
    /// over the last 24 hours from the AI usage records). Returns how many were settled (tried, typed or not), how many got a type, and how many calls were
    /// made. 0 settled means nothing more can be done now (none left, the day's calls used up, no AI client, or every call failed): stop until the next pass.
    /// <para>Cost (owner, 2026-10-14: no wasted calls): one call per distinct PDF ever — a copy reuses the first copy's answer, typed or not, whether
    /// stored earlier or in this same batch; nothing is sent for a page with almost no text; only the top of the page is sent.</para></summary>
    public async Task<(int Seen, int Typed, int Calls)> ClassifyBatchAsync(int batchSize, CancellationToken ct, int maxCallsPerDay = int.MaxValue)
    {
        var docs = await db.McaFilingDocuments
            .Where(d => d.AiClassifiedUtc == null && Unplaced.Contains(d.ClassificationMethod) && d.DuplicateOfDocumentId == null
                && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed && d.ExtractedTextPath != null)
            .OrderBy(d => d.FilingDocumentId).Take(batchSize)
            .Select(d => new { Doc = d, d.Filing!.OuterCategoryFolder })
            .ToListAsync(ct);
        if (docs.Count == 0) return (0, 0, 0);

        var since = DateTime.UtcNow.AddHours(-24);
        var callsLeft = maxCallsPerDay == int.MaxValue ? int.MaxValue
            : maxCallsPerDay - await db.AiUsageRecords.CountAsync(r => r.Purpose == AiUsagePurposes.DocumentClassification && r.CreatedUtc >= since, ct);

        ILitigationAiAnalysisClient? client = null;
        try { client = services.GetRequiredService<ILitigationAiAnalysisClient>(); }
        catch (Exception ex) { logger.LogInformation("Document type fallback skipped: the AI client is not configured ({Error})", ex.Message); }

        // Answers already given in this batch, by file hash (null = asked, no type): a second copy of a PDF never costs a second call.
        var answered = new Dictionary<string, AiClassification?>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);   // a PDF whose call failed: its copies wait for the next pass
        int typed = 0, calls = 0, settled = 0;
        foreach (var x in docs)
        {
            var d = x.Doc;
            AiClassification? result;
            if (d.FileHash is { Length: > 0 } hash && answered.TryGetValue(hash, out var inBatch))
                result = inBatch;
            else if (d.FileHash is { Length: > 0 } failedHash && failed.Contains(failedHash))
                continue;
            else if (await EarlierAnswerAsync(d, ct) is { } earlier)
                result = earlier.Answer;
            else
            {
                if (client is null || callsLeft <= 0) break;   // nothing to ask with, or the day's calls are used up: leave the rest for later
                string text;
                try { text = File.Exists(d.ExtractedTextPath) ? await File.ReadAllTextAsync(d.ExtractedTextPath!, ct) : ""; }
                catch (IOException) { text = ""; }
                var top = text.Length > TextChars ? text[..TextChars] : text;
                if (Flat(top).Length < MinTextChars) { d.AiClassifiedUtc = DateTime.UtcNow; settled++; continue; }   // nothing worth a call: tried

                LitigationAiCallResult call;
                using (AiUsageContext.Begin(d.RequestId, AiUsagePurposes.DocumentClassification))
                    call = await client.CallAsync(BuildPrompt(d.OriginalFileName, x.OuterCategoryFolder, top), TimeoutSeconds, ct);
                calls++;
                callsLeft--;
                if (!call.Success)
                {
                    logger.LogWarning("Document type fallback for document {DocumentId} failed ({Reason}); tried again later", d.FilingDocumentId, call.FailureReason);
                    if (d.FileHash is { Length: > 0 } fh) failed.Add(fh);
                    continue;   // not marked: tried again on a later pass
                }
                result = Check(call.RawResponse, top);
            }
            if (d.FileHash is { Length: > 0 } h) answered[h] = result;

            d.AiClassifiedUtc = DateTime.UtcNow;
            settled++;
            if (result is null) continue;
            typed++;
            d.Category = result.Category;
            d.FormType = result.FormType;
            d.ClassificationConfidence = ClassificationConfidence.Medium;
            d.ClassificationMethod = Method;
            d.MatchedRule = result.TitleQuote.Length > 300 ? result.TitleQuote[..300] : result.TitleQuote;
            if (d.ManualReviewReason == "Could not classify this document.") { d.ManualReviewRequired = false; d.ManualReviewReason = null; }
            await db.DocumentChunks.Where(c => c.FilingDocumentId == d.FilingDocumentId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Category, result.Category).SetProperty(c => c.FormType, result.FormType), ct);
        }
        await db.SaveChangesAsync(ct);
        if (calls > 0 || typed > 0)
            logger.LogInformation("Document type fallback: {Settled} documents settled, {Typed} typed, {Calls} calls", settled, typed, calls);
        return (settled, typed, calls);
    }

    private sealed record Earlier(AiClassification? Answer);

    /// <summary>The answer already given for the same PDF (same file hash) on another document: its type, or "tried, no type". Null = never asked.</summary>
    private async Task<Earlier?> EarlierAnswerAsync(McaFilingDocument d, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(d.FileHash)) return null;
        var other = await db.McaFilingDocuments.AsNoTracking()
            .Where(o => o.FileHash == d.FileHash && o.FilingDocumentId != d.FilingDocumentId && o.AiClassifiedUtc != null)
            .OrderByDescending(o => o.ClassificationMethod == Method)
            .Select(o => new { o.ClassificationMethod, o.FormType, o.MatchedRule }).FirstOrDefaultAsync(ct);
        if (other is null) return null;
        return other.ClassificationMethod == Method && other.FormType is { } t && FilingClassifier.KnownTypes.TryGetValue(t, out var category)
            ? new Earlier(new AiClassification(t, category, other.MatchedRule ?? ""))
            : new Earlier(null);
    }

    /// <summary>The answer, if it holds: a known type, and a title quote of at least <see cref="MinQuoteChars"/> characters that is on the page (case and
    /// spacing ignored) <b>and shows that type</b> (<see cref="FilingClassifier.QuoteShowsType"/>: a line such as "Government of India" is on the page but
    /// shows no type — review of #504). "Unknown" or anything else gives null.</summary>
    internal static AiClassification? Check(string raw, string pageText)
    {
        string formType, quote;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1) root = root[0];
            if (root.ValueKind != JsonValueKind.Object) return null;
            formType = root.TryGetProperty("formType", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()!.Trim() : "";
            quote = root.TryGetProperty("titleQuote", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString()!.Trim() : "";
        }
        catch (JsonException) { return null; }

        if (!FilingClassifier.KnownTypes.TryGetValue(formType, out var category)) return null;
        var flatQuote = Flat(quote);
        if (flatQuote.Length < MinQuoteChars || !Flat(pageText).Contains(flatQuote, StringComparison.Ordinal)) return null;
        if (!FilingClassifier.QuoteShowsType(formType, quote)) return null;
        return new AiClassification(formType, category, quote);
    }

    internal static string BuildPrompt(string fileName, string? folder, string pageText)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{PromptMarker} ({PromptVersion})");
        sb.AppendLine("This is a document attached to a filing with India's Ministry of Corporate Affairs. Say which type it is.");
        sb.AppendLine("Choose exactly one type from this list, written as listed, or \"Unknown\" if none fits or you are not sure:");
        foreach (var t in FilingClassifier.KnownTypes.Keys.OrderBy(k => k, StringComparer.Ordinal)) sb.AppendLine($"- {t}");
        sb.AppendLine();
        sb.AppendLine("Also copy, exactly as it appears in the text below, the title or heading line that names the type (for example \"Certificate of");
        sb.AppendLine("Registration of Charge\" or \"List of Allottees\"). A line that does not name the type, such as \"Government of India\", is not enough:");
        sb.AppendLine("if no line names the type, answer \"Unknown\".");
        sb.AppendLine("Answer with JSON only: {\"formType\": \"...\", \"titleQuote\": \"...\"}");
        sb.AppendLine();
        sb.AppendLine($"File name: {fileName}");
        if (!string.IsNullOrWhiteSpace(folder)) sb.AppendLine($"Folder: {folder}");
        sb.AppendLine("Text (start of the document):");
        sb.AppendLine(pageText);
        return sb.ToString();
    }

    private static string Flat(string s) => Spaces().Replace(s, " ").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
