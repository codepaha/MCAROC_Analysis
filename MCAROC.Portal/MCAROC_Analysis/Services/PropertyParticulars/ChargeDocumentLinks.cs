using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.PropertyParticulars;

/// <summary>What the linker knows about one (canonical) filing document: its hash and file name, and — for documents whose
/// form data is read (<see cref="ChargeForms.Candidates"/>) — its saved XFA fields and embedded-file hashes.</summary>
public sealed record ChargeLinkInput(
    long FilingDocumentId, string FileHash, string OriginalFileName,
    IReadOnlyList<XfaField>? Fields = null, IReadOnlyList<EmbeddedFileHash>? Embedded = null);

public sealed record ComputedChargeDocumentLink(string ChargeNumber, long FilingDocumentId, ChargeDocumentLinkMethod Method, long? LinkedFromDocumentId = null);

/// <summary>A document linked to a charge, as the charge drawer lists it.</summary>
public sealed record ChargeDocumentRow(
    long FilingDocumentId, string? FormType, string DisplayName, ChargeDocumentLinkMethod Method, long? LinkedFromDocumentId);

/// <summary>#377: ties every filing document of a batch to the charges of the request's register it belongs to — open and
/// satisfied alike, since past charges trace assets that are no longer collateral. Every method is exact: the charge ID a
/// form states, a creation form's unique date-and-amount match (#364's rule), an attachment byte-identical to a file
/// embedded in a linked form, or a charge ID in the file name that the document's own form data doesn't contradict.
/// Anything ambiguous stays unlinked.</summary>
public static partial class ChargeDocumentLinker
{
    /// <summary>Bumped when the rules change, so every batch's links are rebuilt.</summary>
    public const string Version = "1";

    internal static string Normalize(string chargeNumber) => chargeNumber.Trim().TrimStart('0');

    [GeneratedRegex(@"ChargeId-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameChargeId();

    public static IReadOnlyList<ComputedChargeDocumentLink> Compute(IReadOnlyList<ChargeLinkInput> documents, IReadOnlyCollection<RocCharge> charges)
    {
        var numbers = charges.Select(c => Normalize(c.RocChargeNumber)).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        var links = new Dictionary<(string Charge, long Doc), ComputedChargeDocumentLink>();
        void Add(ComputedChargeDocumentLink link) => links.TryAdd((link.ChargeNumber, link.FilingDocumentId), link);

        // 1. The charge ID the form states (field ChargeID: modification and satisfaction forms). Recorded even when the
        //    register doesn't hold it, so a file name can't link the document to a different charge (rule 3).
        var statedId = new Dictionary<long, string>();
        foreach (var d in documents)
        {
            var id = d.Fields?.FirstOrDefault(f => f.Name == "ChargeID" && f.Value.Trim().Length > 0)?.Value;
            if (id is null || Normalize(id) is not { Length: > 0 } n || !n.All(char.IsAsciiDigit)) continue;
            statedId[d.FilingDocumentId] = n;
            if (numbers.Contains(n)) Add(new(n, d.FilingDocumentId, ChargeDocumentLinkMethod.FormChargeId));
        }

        // 2. A creation form without an ID: its instrument date and amount match exactly one charge's creation event.
        var creationForms = documents
            .Where(d => d.Fields is { Count: > 0 } && !statedId.ContainsKey(d.FilingDocumentId))
            .Select(d => ChargeForms.FromFields(d.FilingDocumentId, d.Fields!))
            .OfType<ChargeFormRecord>()
            .Where(f => f.ChargeId is null)
            .ToList();
        var numberOfRow = charges.ToDictionary(c => c.ChargeId, c => Normalize(c.RocChargeNumber));
        foreach (var (chargeId, forms) in ChargeForms.Link(creationForms, charges))
            foreach (var form in forms.Where(f => f.Basis == ChargeFormLinkBasis.CreationDateAndAmount))
                Add(new(numberOfRow[chargeId], form.Form.FilingDocumentId, ChargeDocumentLinkMethod.CreationDateAndAmount));

        // 3. The file name states the charge ID, and the document's form data doesn't name a different one.
        foreach (var d in documents)
        {
            if (FileNameChargeId().Match(d.OriginalFileName) is not { Success: true } m) continue;
            var n = Normalize(m.Groups[1].Value);
            if (!numbers.Contains(n) || (statedId.TryGetValue(d.FilingDocumentId, out var stated) && stated != n)) continue;
            Add(new(n, d.FilingDocumentId, ChargeDocumentLinkMethod.FileName));
        }

        // 4. Attachments: a document byte-identical to a file embedded in a form linked above belongs to that form's charge.
        var byHash = documents.Where(d => d.FileHash.Length > 0)
            .GroupBy(d => d.FileHash, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var embeddedOf = documents.Where(d => d.Embedded is { Count: > 0 }).ToDictionary(d => d.FilingDocumentId, d => d.Embedded!);
        foreach (var formLink in links.Values.ToList())
        {
            if (!embeddedOf.TryGetValue(formLink.FilingDocumentId, out var embedded)) continue;
            foreach (var file in embedded)
                foreach (var attachment in byHash.GetValueOrDefault(file.Sha256) ?? [])
                    if (attachment.FilingDocumentId != formLink.FilingDocumentId)
                        Add(new(formLink.ChargeNumber, attachment.FilingDocumentId, ChargeDocumentLinkMethod.EmbeddedAttachment, formLink.FilingDocumentId));
        }

        return links.Values.OrderBy(l => l.ChargeNumber, StringComparer.Ordinal).ThenBy(l => l.FilingDocumentId).ToList();
    }

    /// <summary>What a batch's links are built from: the rules' version, the register (the request's latest completed
    /// ingestion run — a run's charges never change) and the batch's documents (any new or updated row changes it).</summary>
    internal static async Task<string> StampAsync(AppDbContext db, long batchId, long? runId, CancellationToken ct)
    {
        var docs = await db.McaFilingDocuments.AsNoTracking().Where(d => d.BatchId == batchId)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxId = g.Max(d => d.FilingDocumentId), MaxUpdated = g.Max(d => d.UpdatedAt) })
            .FirstOrDefaultAsync(ct);
        return $"v{Version}:r{runId ?? 0}:{docs?.Count ?? 0}:{docs?.MaxId ?? 0}:{docs?.MaxUpdated.Ticks ?? 0}";
    }

    /// <summary>"6367d1ef…c983v1-Form CHG-1-131015.pdf" → "Form CHG-1-131015": the export's id prefix and the extension
    /// are dropped; the rest is the file name as filed.</summary>
    public static string DisplayName(string originalFileName)
    {
        var name = Path.GetFileNameWithoutExtension(originalFileName.Replace('\\', '/').Split('/').Last());
        var m = ExportPrefix().Match(name);
        return m.Success && m.Length < name.Length ? name[m.Length..] : name;
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{32}v\d+(\.DUP\d+)?-")]
    private static partial Regex ExportPrefix();

    /// <summary>The documents linked to each charge of the request's authoritative batch, keyed by the register's charge
    /// number (leading zeros removed). When the stored links were built from other evidence (a newer register, new
    /// documents, or never) the batch is handed to <paramref name="builder"/>, and the current rows are shown meanwhile.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<ChargeDocumentRow>>> LoadAsync(
        AppDbContext db, long requestId, long? runId, CancellationToken ct, ChargeDocumentLinkBuilder? builder = null)
    {
        var batch = await McaFilingBatchResolver.GetAuthoritativeBatchAsync(db, requestId, ct);
        if (batch is null) return new Dictionary<string, IReadOnlyList<ChargeDocumentRow>>();

        if (builder is not null && batch.Status is FilingBatchStatus.Completed or FilingBatchStatus.CompletedWithErrors)
        {
            // Read fresh: the builder writes the stamp from its own context, so a tracked batch may hold an older value.
            var built = await db.McaFilingBatches.AsNoTracking().Where(b => b.BatchId == batch.BatchId).Select(b => b.ChargeLinksStamp).FirstOrDefaultAsync(ct);
            if (await StampAsync(db, batch.BatchId, runId, ct) != built)
                builder.Request(batch.BatchId);
        }

        var rows = await db.ChargeDocumentLinks.AsNoTracking()
            .Where(l => l.BatchId == batch.BatchId)
            .Select(l => new { l.RocChargeNumber, l.FilingDocumentId, l.Method, l.LinkedFromDocumentId, l.FilingDocument!.FormType, l.FilingDocument.OriginalFileName })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.RocChargeNumber, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ChargeDocumentRow>)g.Select(r => new ChargeDocumentRow(r.FilingDocumentId, r.FormType, DisplayName(r.OriginalFileName), r.Method, r.LinkedFromDocumentId))
                .OrderBy(r => r.FilingDocumentId).ToList(),
            StringComparer.Ordinal);
    }
}

/// <summary>#377: builds a batch's charge-document links off the request path: first saves any missing form data and
/// embedded-file hashes (<see cref="ChargeFormBackfill"/>), then links every document against the request's current
/// register and replaces the batch's rows in one transaction. One run per batch at a time.</summary>
public class ChargeDocumentLinkBuilder(IServiceScopeFactory scopes, ChargeFormBackfill backfill, ILogger<ChargeDocumentLinkBuilder> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task> _running = new();

    public virtual void Request(long batchId) =>
        _running.GetOrAdd(batchId, id => Task.Run(async () =>
        {
            try { await RunAsync(id, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Linking charge documents failed for batch {BatchId}", id); }
            finally { _running.TryRemove(id, out _); }
        }));

    internal async Task<int> RunAsync(long batchId, CancellationToken ct)
    {
        await backfill.RunSharedAsync(batchId);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var batch = await db.McaFilingBatches.AsNoTracking().FirstAsync(b => b.BatchId == batchId, ct);
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == batch.RequestId).Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);
        var stamp = await ChargeDocumentLinker.StampAsync(db, batchId, runId, ct);

        var charges = runId is { } run
            ? await db.RocCharges.AsNoTracking().Include(c => c.Events).Where(c => c.IngestionRunId == run).ToListAsync(ct)
            : [];
        var formDocIds = (await ChargeForms.Candidates(db, batchId).Select(d => d.FilingDocumentId).ToListAsync(ct)).ToHashSet();
        var documents = await db.McaFilingDocuments.AsNoTracking()
            .Where(d => d.BatchId == batchId && d.DuplicateOfDocumentId == null)
            .Select(d => new { d.FilingDocumentId, d.FileHash, d.OriginalFileName, d.ExtractedTextPath })
            .ToListAsync(ct);

        var inputs = new List<ChargeLinkInput>(documents.Count);
        foreach (var d in documents)
        {
            IReadOnlyList<XfaField>? fields = null;
            IReadOnlyList<EmbeddedFileHash>? embedded = null;
            if (formDocIds.Contains(d.FilingDocumentId) && d.ExtractedTextPath is { } text)
            {
                fields = await XfaFormReader.ReadSidecarAsync(text, ct);
                embedded = await EmbeddedFiles.ReadSidecarAsync(text, ct);
            }
            inputs.Add(new ChargeLinkInput(d.FilingDocumentId, d.FileHash, d.OriginalFileName, fields, embedded));
        }
        var links = ChargeDocumentLinker.Compute(inputs, charges);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ChargeDocumentLinks.Where(l => l.BatchId == batchId).ExecuteDeleteAsync(ct);
        db.ChargeDocumentLinks.AddRange(links.Select(l => new ChargeDocumentLink
        {
            RequestId = batch.RequestId, BatchId = batchId, RocChargeNumber = l.ChargeNumber, FilingDocumentId = l.FilingDocumentId,
            Method = l.Method, LinkedFromDocumentId = l.LinkedFromDocumentId
        }));
        await db.SaveChangesAsync(ct);
        await db.McaFilingBatches.Where(b => b.BatchId == batchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.ChargeLinksStamp, stamp), ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Linked {Links} charge document link(s) for batch {BatchId} ({Charges} charges)",
            links.Count, batchId, links.Select(l => l.ChargeNumber).Distinct().Count());
        return links.Count;
    }
}
