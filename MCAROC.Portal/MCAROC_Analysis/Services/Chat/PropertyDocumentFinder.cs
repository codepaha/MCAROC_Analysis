using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>The charge a document is linked to, with whether it is still open.</summary>
public sealed record LinkedCharge(string Number, string Holder, string Status);

/// <summary>A filing document whose own text describes the property a question names, with the pages that do and the charge it belongs to.</summary>
public sealed record PropertyDocument(long FilingDocumentId, string Name, string? FormType, IReadOnlyList<int> Pages, IReadOnlyList<string> Matched, IReadOnlyList<LinkedCharge> Charges);

/// <summary>
/// "Which documents are about this land?" (#564). Retrieval answers it from the top few chunks, so several near-identical deeds crowd out the one document that
/// releases the land, and the answer can say no charge was satisfied while a no-claim letter describing the very same land sits on file. This looks at whole
/// documents instead: every document of the authoritative filing batch with a passage that names the property, by identifiers read in context (survey, CTS,
/// plot numbers), by its area figure, or by its quoted name. No AI call.
/// <para>A document qualifies on two or more of the question's identifiers, on the area figure, or on the quoted name together with one identifier. The name alone
/// does not: a compound is full of addresses (a shareholder at "2nd Floor, Mathuradas Mill Compound" is not the land). One identifier alone does not either: a
/// four-digit number is too easily a folio or a certificate number.</para>
/// </summary>
public sealed class PropertyDocumentFinder(AppDbContext db)
{
    public const int MaxDocuments = 25;
    private const int MaxTerms = 16;
    private const int MaxChunksPerTerm = 600;

    private static readonly Regex Quoted = new("[\"“‘']([A-Za-z][^\"”’']{5,80})[\"”’']", RegexOptions.Compiled);
    private static readonly Regex Area = new(@"(?<![\d.,])\d{1,3}(?:,\d{3})*\.\d+|(?<![\d.,])\d{3,}\.\d+", RegexOptions.Compiled);

    /// <summary>What the question says about the property that a document could repeat: identifiers, area figures (kept as written without thousands commas) and a quoted name.</summary>
    internal sealed record Subject(IReadOnlyList<StructuredFactsProvider.PropertyId> Ids, IReadOnlyList<string> Areas, Regex? Name, string? NameHead)
    {
        public bool IsEmpty => Ids.Count == 0 && Areas.Count == 0 && Name is null;
    }

    internal static Subject SubjectOf(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return new([], [], null, null);
        var ids = StructuredFactsProvider.PropertyIds(question);
        var areas = Area.Matches(question).Select(m => m.Value.Replace(",", "")).Where(a => a.Split('.')[0].Length >= 3).Distinct().ToList();
        Regex? name = null; string? head = null;
        foreach (Match m in Quoted.Matches(question))
        {
            var words = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) continue;
            name = new Regex(string.Join(@"\s+", words.Select(w => Regex.Escape(w.TrimEnd('s', 'S')) + "s?")), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            head = words[0].TrimEnd('s', 'S');
            break;
        }
        return new(ids, areas, name, head);
    }

    public async Task<List<PropertyDocument>> FindAsync(long requestId, long? batchId, string? question, CancellationToken ct)
    {
        var subject = SubjectOf(question);
        if (subject.IsEmpty || batchId is null) return [];

        // Chunks that could repeat the property: one cheap pass per distinctive term, limited to the request's current batch.
        var terms = subject.Ids.Select(i => i.Parts.Last()).Where(p => p.Length >= 3).Distinct().Take(MaxTerms - 4).Select(p => "%" + p + "%").ToList();
        foreach (var area in subject.Areas.Take(2)) { terms.Add("%" + area + "%"); terms.Add("%" + System.Text.RegularExpressions.Regex.Replace(area.Split('.')[0], @"(\d)(?=(\d{3})+$)", "$1,") + "." + area.Split('.')[1] + "%"); }
        if (subject.NameHead is { Length: >= 4 }) terms.Add("%" + subject.NameHead + "%");

        var chunks = new Dictionary<long, (long DocId, string Name, string? FormType, int Page, string Text)>();
        foreach (var term in terms.Distinct())
        {
            var found = await db.DocumentChunks.AsNoTracking()
                .Where(c => c.RequestId == requestId && c.BatchId == batchId && EF.Functions.Like(c.ChunkText, term))
                .OrderBy(c => c.FilingDocumentId).ThenBy(c => c.PageNumber).Take(MaxChunksPerTerm)
                .Select(c => new { c.ChunkId, c.FilingDocumentId, c.DocumentName, c.FormType, c.PageNumber, c.ChunkText }).ToListAsync(ct);
            foreach (var c in found) chunks[c.ChunkId] = (c.FilingDocumentId, c.DocumentName, c.FormType, c.PageNumber, c.ChunkText);
        }

        var perDocument = new Dictionary<long, (string Name, string? FormType, SortedSet<int> Pages, HashSet<string> Ids, bool AreaHit, bool NameHit)>();
        foreach (var (docId, name, formType, page, text) in chunks.Values)
        {
            var plain = Regex.Replace(text, @"(?<=\d),(?=\d{3})", "");
            var ids = StructuredFactsProvider.PropertyIds(text).Where(t => StructuredFactsProvider.SharesProperty(subject.Ids, [t])).Select(t => t.Text).ToList();
            var areaHit = subject.Areas.Any(a => plain.Contains(a, StringComparison.Ordinal));
            var nameHit = subject.Name is not null && subject.Name.IsMatch(Regex.Replace(text, @"\s+", " "));
            if (ids.Count == 0 && !areaHit && !nameHit) continue;
            if (!perDocument.TryGetValue(docId, out var d)) perDocument[docId] = d = (name, formType, [], [], false, false);
            d.Pages.Add(page); foreach (var i in ids) d.Ids.Add(i);
            perDocument[docId] = (d.Name, d.FormType, d.Pages, d.Ids, d.AreaHit || areaHit, d.NameHit || nameHit);
        }

        var qualifying = perDocument.Where(kv => kv.Value.Ids.Count >= 2 || kv.Value.AreaHit || (kv.Value.NameHit && kv.Value.Ids.Count >= 1)).Select(kv => kv.Key).ToList();
        if (qualifying.Count == 0) return [];

        // The charge each document belongs to, from the links the system has built, with that charge's current status.
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);
        var links = await db.ChargeDocumentLinks.AsNoTracking().Where(l => l.RequestId == requestId && qualifying.Contains(l.FilingDocumentId))
            .Select(l => new { l.FilingDocumentId, l.RocChargeNumber }).ToListAsync(ct);
        var numbers = links.Select(l => l.RocChargeNumber).Distinct().ToList();
        var charges = numbers.Count == 0 || runId is null ? new Dictionary<string, LinkedCharge>()
            : (await db.RocCharges.AsNoTracking().Where(c => c.IngestionRunId == runId && numbers.Contains(c.RocChargeNumber))
                .Select(c => new { c.RocChargeNumber, c.LatestChargeHolderRaw, c.ChargeStatus }).ToListAsync(ct))
                .ToDictionary(c => c.RocChargeNumber, c => new LinkedCharge(c.RocChargeNumber, c.LatestChargeHolderRaw, c.ChargeStatus));

        return qualifying.Select(id =>
            {
                var d = perDocument[id];
                var matched = d.Ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (d.AreaHit) matched.Add("the area " + string.Join(", ", subject.Areas));
                if (d.NameHit) matched.Add("the property's name");
                return new PropertyDocument(id, d.Name, d.FormType, [.. d.Pages.Take(4)], matched,
                    links.Where(l => l.FilingDocumentId == id).Select(l => charges.GetValueOrDefault(l.RocChargeNumber)).Where(c => c is not null).Cast<LinkedCharge>().DistinctBy(c => c.Number).ToList());
            })
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Take(MaxDocuments).ToList();
    }

    /// <summary>The facts the model reads: a header that says the list is every document naming the property, then one fact per document.</summary>
    public async Task<List<StructuredFact>> FactsAsync(long requestId, long? batchId, string? question, CancellationToken ct)
    {
        var docs = await FindAsync(requestId, batchId, question, ct);
        if (docs.Count == 0) return [];
        var facts = new List<StructuredFact>
        {
            new("Documents", $"{docs.Count} document(s) on file whose own text describes the property the question names. This is every such document in the current filing batch, " +
                "not a sample; a charge shown as Satisfied here is a charge on this property that has been released.", null, null)
        };
        foreach (var d in docs)
            facts.Add(new StructuredFact("Documents",
                $"Document: {d.Name}" + (d.FormType is { Length: > 0 } ? $" ({d.FormType})" : "") + $", page(s) {string.Join(", ", d.Pages)}, names the property by {string.Join("; ", d.Matched)}"
                + (d.Charges.Count == 0 ? "; not linked to a charge by the system"
                    : "; linked to charge " + string.Join(", ", d.Charges.Select(c => $"{c.Number} ({c.Holder}, {c.Status})"))),
                null, null));
        return facts;
    }
}
