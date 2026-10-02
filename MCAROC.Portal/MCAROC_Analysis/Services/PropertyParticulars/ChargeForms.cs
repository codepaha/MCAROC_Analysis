using System.Globalization;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Services.PropertyParticulars;

/// <summary>What one charge e-form (Form 8 / CHG-1, filed as an XFA form) states, exactly as filed (#364). Every value comes
/// from a named form field (#369) — nothing is inferred. <see cref="PropertyParticulars"/> is the form's "particulars of the
/// property charged" (its repeated lines joined). <see cref="OwnedByCompany"/> is the form's answer to item 16(a), "whether
/// any of the property is <em>not</em> registered in the name of the company" (field <c>PropOwnCmp</c>): NO → true,
/// YES → false; null when unanswered. <see cref="RegisteredOwner"/> is item 16(b), in whose name it is registered.</summary>
public sealed record ChargeFormRecord(
    long FilingDocumentId, string? ChargeId, string? InstrumentDescription, DateOnly? InstrumentDate, decimal? AmountSecuredRupees,
    string? HolderName, string? PropertyParticulars, bool? OwnedByCompany, string? RegisteredOwner = null, string? FiledChargeType = null)
{
    /// <summary>The form's own filed type (field <c>ChargeType</c>): CRTN for a charge creation, MDFN for a modification.
    /// Null when the form doesn't state it — then it is never treated as a creation.</summary>
    public bool? IsCreation => FiledChargeType?.ToUpperInvariant() switch { "CRTN" => true, "MDFN" => false, _ => null };

    /// <summary>The amount as the charge register states it: rupees crore, two decimals.</summary>
    public decimal? AmountSecuredCrore => AmountSecuredRupees is { } r ? Math.Round(r / 10_000_000m, 2, MidpointRounding.AwayFromZero) : null;
}

/// <summary>How a charge form was matched to the register: by the charge ID it states (a modification form), or — for a
/// creation form, filed before the Registrar issues an ID — by its instrument date and amount, accepted only when exactly
/// one charge of the request has a creation event with that date and amount.</summary>
public enum ChargeFormLinkBasis { ChargeId, CreationDateAndAmount }

public sealed record LinkedChargeForm(ChargeFormRecord Form, ChargeFormLinkBasis Basis);

public static class ChargeForms
{
    /// <summary>Reads a charge form from the "Name: value" lines <see cref="XfaFormReader.ToText"/> writes. Null when the
    /// text is not a charge form.</summary>
    public static ChargeFormRecord? Read(long filingDocumentId, string extractedText)
    {
        var fields = new List<XfaField>();
        foreach (var raw in extractedText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0 || line.StartsWith("---", StringComparison.Ordinal)) continue;
            var name = line[..colon];
            if (name.Any(char.IsWhiteSpace)) continue; // field names never contain spaces
            fields.Add(new XfaField(name, name, line[(colon + 2)..].Trim()));
        }
        return FromFields(filingDocumentId, fields);
    }

    /// <summary>A charge form (Form 8 / CHG-1, MCA's "Form8" XFA schema) from its filed fields; null when the fields are not a
    /// charge form (no instrument or property-particulars field). Older form versions call the particulars field
    /// <c>PropParticlars</c>, newer ones <c>NewPropParticlars</c>.</summary>
    public static ChargeFormRecord? FromFields(long filingDocumentId, IReadOnlyList<XfaField> xfaFields)
    {
        var fields = xfaFields.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(f => f.Value).ToList(), StringComparer.Ordinal);
        if (!fields.ContainsKey("InstrumentDesc") && !fields.ContainsKey("NewPropParticlars") && !fields.ContainsKey("PropParticlars")) return null;

        string? One(string name) => fields.TryGetValue(name, out var v) && v.Count > 0 && v[0].Length > 0 ? v[0] : null;
        var particularLines = fields.GetValueOrDefault("NewPropParticlars") ?? fields.GetValueOrDefault("PropParticlars");
        var particulars = particularLines is null ? null : string.Join(" ", particularLines.Where(l => l.Length > 0));
        var registeredOwner = One("PropRegisteredName") is { } owner && !NoOwner(owner) ? owner : null;
        var holder = One("OptionalName") ?? (One("ChrgHldrName") is { } h && !h.Equals("Others", StringComparison.OrdinalIgnoreCase) ? h : null);
        return new ChargeFormRecord(
            filingDocumentId,
            One("ChargeID")?.TrimStart('0') is { Length: > 0 } id ? id : null,
            One("InstrumentDesc"),
            DateOnly.TryParseExact(One("InstrumentCrtModDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            decimal.TryParse(One("AmtSecured"), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null,
            holder,
            string.IsNullOrWhiteSpace(particulars) ? null : particulars,
            // Item 16(a) asks whether any property is NOT registered in the company's name: NO means it is the company's.
            One("PropOwnCmp")?.ToUpperInvariant() switch { "NO" => true, "YES" => false, _ => null },
            registeredOwner,
            One("ChargeType"));
    }

    private static bool NoOwner(string value) =>
        value.Trim().Trim('.', '-').ToUpperInvariant() is "" or "NIL" or "NA" or "N/A" or "NONE" or "NOT APPLICABLE";

    /// <summary>Matches forms to the request's charges. A form naming a charge ID links to that charge only. A form without
    /// one links only when the form itself is a creation (filed type CRTN) and exactly one charge has a creation event on
    /// the instrument date for the same amount (crore, two decimals). A modification or unknown-type form without an ID, and
    /// anything ambiguous or unmatched, is left unlinked — a form is never attached to a guessed charge.</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<LinkedChargeForm>> Link(IEnumerable<ChargeFormRecord> forms, IReadOnlyCollection<RocCharge> charges)
    {
        var byNumber = charges.GroupBy(c => c.RocChargeNumber.Trim().TrimStart('0')).ToDictionary(g => g.Key, g => g.ToList());
        var linked = new Dictionary<long, List<LinkedChargeForm>>();
        void Add(RocCharge charge, LinkedChargeForm form) =>
            (linked.TryGetValue(charge.ChargeId, out var list) ? list : linked[charge.ChargeId] = []).Add(form);

        foreach (var form in forms)
        {
            if (form.ChargeId is { } id)
            {
                if (byNumber.TryGetValue(id, out var withId) && withId.Count == 1) Add(withId[0], new(form, ChargeFormLinkBasis.ChargeId));
                continue;
            }
            if (form.IsCreation != true || form.InstrumentDate is not { } date || form.AmountSecuredCrore is not { } crore) continue;
            var candidates = charges.Where(c => c.Events.Any(e => e.EventType == ChargeEventType.Creation && e.EventDate == date
                && e.ChargeAmount is { } a && Math.Round(a, 2) == crore)).ToList();
            if (candidates.Count == 1) Add(candidates[0], new(form, ChargeFormLinkBasis.CreationDateAndAmount));
        }
        return linked.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<LinkedChargeForm>)kv.Value
            .OrderBy(f => f.Form.InstrumentDate ?? DateOnly.MinValue).ToList());
    }

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);


    /// <summary>The batch documents whose filed form data is read: its charge documents, plus any whose text came from XFA.</summary>
    internal static IQueryable<McaFilingDocument> Candidates(AppDbContext db, long batchId) =>
        db.McaFilingDocuments.AsNoTracking().Where(d => d.BatchId == batchId && d.DuplicateOfDocumentId == null && d.ExtractedTextPath != null
            && (d.Category == FilingCategory.Charge || d.TextExtractionMethod == TextExtractionMethod.Xfa));

    /// <summary>The charge forms of the request's authoritative filing batch, linked to the given charges. Form fields are
    /// read from each document's saved XFA fields (written at extraction, beside its text) — never by opening PDFs on a page
    /// load. Documents extracted before that existed have none yet: they are handed to <paramref name="backfill"/>, which
    /// saves them in the background, and the forms appear on a later load. A complete batch is cached; the link to charges is
    /// recomputed each time (cheap). The cache is keyed on the batch's evidence — how many documents are eligible, the newest
    /// and the latest update — so a document extracted after a load (which updates its row) is read on the very next one;
    /// a batch still being processed, or one with documents awaiting backfill, is never cached.</summary>
    public static async Task<IReadOnlyDictionary<long, IReadOnlyList<LinkedChargeForm>>> LoadAsync(
        AppDbContext db, long requestId, IReadOnlyCollection<RocCharge> charges, CancellationToken ct,
        IMemoryCache? cache = null, ChargeFormBackfill? backfill = null)
    {
        if (charges.Count == 0) return new Dictionary<long, IReadOnlyList<LinkedChargeForm>>();
        var batch = await McaFilingBatchResolver.GetAuthoritativeBatchAsync(db, requestId, ct);
        if (batch is null) return new Dictionary<long, IReadOnlyList<LinkedChargeForm>>();

        var documents = await Candidates(db, batch.BatchId).Select(d => new { d.FilingDocumentId, d.ExtractedTextPath, d.UpdatedAt }).ToListAsync(ct);
        var key = documents.Count == 0 ? null
            : $"ChargeForms:{batch.BatchId}:{documents.Count}:{documents.Max(d => d.FilingDocumentId)}:{documents.Max(d => d.UpdatedAt).Ticks}";
        if (key is not null && cache is not null && cache.TryGetValue(key, out List<ChargeFormRecord>? cached) && cached is not null)
            return Link(cached, charges);

        var forms = new List<ChargeFormRecord>();
        var missing = false;
        foreach (var d in documents)
        {
            var fields = await XfaFormReader.ReadSidecarAsync(d.ExtractedTextPath!, ct);
            if (fields is null) { missing = true; continue; }
            if (FromFields(d.FilingDocumentId, fields) is { } form) forms.Add(form);
        }
        var batchSettled = batch.Status is FilingBatchStatus.Completed or FilingBatchStatus.CompletedWithErrors;
        if (missing) backfill?.Request(batch.BatchId);
        else if (batchSettled && key is not null)
            cache?.Set(key, forms, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = CacheFor });
        return Link(forms, charges);
    }
}

/// <summary>#364: saves the XFA fields of a batch's charge documents that were extracted before fields were saved at
/// extraction — off the request path, a few PDFs at a time — so the next load shows them (a load with documents still
/// awaiting this is never cached). One run per batch at a time; writing a document's fields is idempotent, so an
/// overlapping run is harmless.</summary>
public class ChargeFormBackfill(IServiceScopeFactory scopes, ILogger<ChargeFormBackfill> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task> _running = new();

    public virtual void Request(long batchId) =>
        _running.GetOrAdd(batchId, id => Task.Run(async () =>
        {
            try { await RunAsync(id, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Saving charge-form fields failed for batch {BatchId}", id); }
            finally { _running.TryRemove(id, out _); }
        }));

    internal async Task<int> RunAsync(long batchId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var documents = await ChargeForms.Candidates(db, batchId).Select(d => new { d.ExtractedTextPath, d.StoragePath }).ToListAsync(ct);
        var todo = documents.Where(d => !File.Exists(XfaFormReader.SidecarPath(d.ExtractedTextPath!))).ToList();
        var saved = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (d, token) =>
        {
            IReadOnlyList<XfaField> fields = [];
            if (!string.IsNullOrEmpty(d.StoragePath) && File.Exists(d.StoragePath))
            {
                try
                {
                    using var pdf = PdfDocument.Open(d.StoragePath);
                    fields = XfaFormReader.ReadFields(pdf) ?? [];
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // An unreadable PDF has no form data to offer; record it as checked.
                }
            }
            await XfaFormReader.WriteSidecarAsync(d.ExtractedTextPath!, fields, token);
            Interlocked.Increment(ref saved);
        });
        if (saved > 0) logger.LogInformation("Saved charge-form fields for {Count} document(s) of batch {BatchId}", saved, batchId);
        return saved;
    }
}
