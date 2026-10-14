using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>
/// Classifies stored filing documents again when <see cref="FilingClassifier.Version"/> moves (#499): from the text already extracted, so no OCR and no
/// AI call. It changes the labels only (category, form type, confidence, method, rule) on the document and on its text chunks, which the chat filters by;
/// it never changes the AI extraction status, so a document that now falls in an extraction category is not extracted retroactively (no surprise cost).
/// A document still being processed is left to the processor, which stamps the current version itself.
/// </summary>
public sealed class FilingReclassifier(AppDbContext db, ILogger<FilingReclassifier> logger)
{
    private const string UnclassifiedReason = "Could not classify this document.";

    private static readonly FilingDocumentProcessingStatus[] InProgress =
        [FilingDocumentProcessingStatus.Discovered, FilingDocumentProcessingStatus.Classified, FilingDocumentProcessingStatus.TextExtracting];

    /// <summary>Re-classifies up to <paramref name="batchSize"/> documents older than the current classifier. Returns how many were looked at and how many
    /// changed; 0 looked at means none are left.</summary>
    public async Task<(int Seen, int Changed)> ReclassifyBatchAsync(int batchSize, CancellationToken ct)
    {
        var docs = await db.McaFilingDocuments
            .Where(d => d.ClassifierVersion < FilingClassifier.Version && !InProgress.Contains(d.ProcessingStatus))
            .OrderBy(d => d.FilingDocumentId).Take(batchSize)
            .Select(d => new { Doc = d, d.Filing!.OuterCategoryFolder })
            .ToListAsync(ct);
        var changed = 0;
        foreach (var x in docs)
        {
            var d = x.Doc;
            string? text = null;
            if (d.ExtractedTextPath is { } path && File.Exists(path))
            {
                try { text = await File.ReadAllTextAsync(path, ct); }
                catch (IOException ex) { logger.LogWarning(ex, "Could not read the text of document {DocumentId}; classified by its name only", d.FilingDocumentId); }
            }
            var r = FilingClassifier.Classify(x.OuterCategoryFolder ?? "", d.SourceFolder ?? "", d.OriginalFileName, text);
            if (r.Category != d.Category || r.FormType != d.FormType || r.Confidence != d.ClassificationConfidence || r.Method != d.ClassificationMethod)
            {
                changed++;
                d.Category = r.Category;
                d.FormType = r.FormType;
                d.ClassificationConfidence = r.Confidence;
                d.ClassificationMethod = r.Method;
                d.MatchedRule = r.MatchedRule;
                if (r.Category != FilingCategory.Unclassified && d.ManualReviewReason == UnclassifiedReason)
                {
                    d.ManualReviewRequired = false;
                    d.ManualReviewReason = null;
                }
                await db.DocumentChunks.Where(c => c.FilingDocumentId == d.FilingDocumentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Category, r.Category).SetProperty(c => c.FormType, r.FormType), ct);
            }
            d.ClassifierVersion = FilingClassifier.Version;
        }
        await db.SaveChangesAsync(ct);
        return (docs.Count, changed);
    }
}

public sealed class FilingReclassificationOptions
{
    public const string SectionName = "FilingReclassification";
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 200;
    public int StartDelaySeconds { get; set; } = 60;
    /// <summary>The AI fallback for documents the rules cannot place (#499). Labels only; one small call per distinct PDF.</summary>
    public bool AiFallbackEnabled { get; set; } = true;
    public int AiBatchSize { get; set; } = 20;
    /// <summary>At most this many document-type calls in any 24 hours (one per distinct PDF; about $0.0003 each).</summary>
    public int AiMaxCallsPerDay { get; set; } = 300;
    /// <summary>How often a pass looks for documents to classify again or to type (new imports between passes are typed on the next one).</summary>
    public int PollMinutes { get; set; } = 10;
}

/// <summary>Every <see cref="FilingReclassificationOptions.PollMinutes"/>: classifies again every stored document older than the current classifier, then
/// types the documents the rules could not place with <see cref="FilingAiClassifier"/>. A failed pass is logged and the next one tries again.</summary>
public sealed class FilingReclassificationWorker(IServiceScopeFactory scopes, IOptions<FilingReclassificationOptions> options, ILogger<FilingReclassificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, opts.StartDelaySeconds)), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int seen = 0, changed = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var scope = scopes.CreateScope();
                    var (batchSeen, batchChanged) = await scope.ServiceProvider.GetRequiredService<FilingReclassifier>().ReclassifyBatchAsync(Math.Max(1, opts.BatchSize), stoppingToken);
                    seen += batchSeen; changed += batchChanged;
                    if (batchSeen == 0) break;
                }
                if (seen > 0)
                    logger.LogInformation("Filing documents re-classified to classifier version {Version}: {Seen} checked, {Changed} changed", FilingClassifier.Version, seen, changed);

                while (opts.AiFallbackEnabled && !stoppingToken.IsCancellationRequested)
                {
                    using var scope = scopes.CreateScope();
                    var (settled, _, _) = await scope.ServiceProvider.GetRequiredService<FilingAiClassifier>().ClassifyBatchAsync(Math.Max(1, opts.AiBatchSize), stoppingToken, Math.Max(0, opts.AiMaxCallsPerDay));
                    if (settled == 0) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Filing classification pass failed; the next pass tries again."); }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, opts.PollMinutes)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
