using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>The charge a document is linked to, with whether it is still open.</summary>
public sealed record LinkedCharge(string Number, string Holder, string Status);

/// <summary>A filing document whose own text mentions the property a question names. <see cref="Confirmed"/> when the document names it firmly enough to say it is about
/// that property; otherwise a candidate: it shares one identifier, or an area figure, with the question, which does not establish the same property.</summary>
public sealed record PropertyDocument(long FilingDocumentId, string Name, string? FormType, IReadOnlyList<int> Pages, IReadOnlyList<string> Matched, IReadOnlyList<LinkedCharge> Charges, bool Confirmed);

/// <summary>What the search found, and what it could not establish.</summary>
public sealed record PropertyDocumentSearch(IReadOnlyList<PropertyDocument> Documents, bool Truncated, string? OnlyIdentifier)
{
    public static readonly PropertyDocumentSearch None = new([], false, null);
}

/// <summary>
/// "Which documents are about this land?" (#564). Retrieval answers it from the top few chunks, so several near-identical deeds crowd out the one document that
/// releases the land, and the answer can say no charge was satisfied while a no-claim letter describing the very same land sits on file. This looks at whole
/// documents instead: every document of the authoritative filing batch with a passage that names the property, by identifiers read in context (survey, CTS,
/// plot numbers), by an area figure given with its unit, or by its quoted name. No AI call.
/// <para>A document is <b>confirmed</b> on two or more of the question's identifiers, on an area figure with its unit together with the quoted name or one identifier,
/// or on the quoted name together with one identifier. It is only a <b>candidate</b> on one identifier alone, on an area figure with its unit alone, or on a number
/// shared across kinds of identifier ("survey 42" and "plot 42"). A name alone is nothing (a compound is full of addresses), nor is a bare number (a folio, a
/// certificate number, a financial amount).</para>
/// <para>The search has limits (chunks read per term, documents returned). When either is reached the result says it was cut short and is never described as complete.</para>
/// </summary>
public sealed class PropertyDocumentFinder(AppDbContext db, int maxChunksPerTerm = PropertyDocumentFinder.DefaultMaxChunksPerTerm, int maxDocuments = PropertyDocumentFinder.DefaultMaxDocuments)
{
    public const int DefaultMaxDocuments = 25;
    public const int DefaultMaxChunksPerTerm = 600;
    private const int MaxTerms = 16;

    private static readonly Regex Quoted = new("[\"“‘']([A-Za-z][^\"”’']{5,80})[\"”’']", RegexOptions.Compiled);
    private static readonly Regex Area = new(@"(?<![\d.,])\d{1,3}(?:,\d{3})*\.\d+|(?<![\d.,])\d{3,}\.\d+", RegexOptions.Compiled);

    // An area figure is an area only when a unit of land measure follows it ("2152.25 sq. yards", "1800 sq mtrs", "0.5 acre"): a rupee amount or a ratio shares the digits, not the meaning.
    private const string AreaUnit = @"\s*(?:sq\b|square|yards?\b|yds?\b|mtrs?\b|metres?\b|meters?\b|acres?\b|hectares?\b|\bha\b|guntas?\b|cents?\b|bigha|ft\b|feet\b)";

    /// <summary>What the question says about the property that a document could repeat: identifiers, area figures (kept as written without thousands commas) and a quoted name.</summary>
    internal sealed record Subject(IReadOnlyList<StructuredFactsProvider.PropertyId> Ids, IReadOnlyList<string> Areas, Regex? Name, string? NameHead)
    {
        public bool IsEmpty => Ids.Count == 0 && Areas.Count == 0 && Name is null;
    }

    internal static Subject SubjectOf(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return new([], [], null, null);
        var ids = StructuredFactsProvider.PropertyIds(question);
        // only a figure the question itself gives with a unit of area counts as an area
        var areas = Area.Matches(question).Where(m => Regex.IsMatch(question[(m.Index + m.Length)..], "^" + AreaUnit, RegexOptions.IgnoreCase))
            .Select(m => m.Value.Replace(",", "")).Where(a => a.Split('.')[0].Length >= 3).Distinct().ToList();
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

    public async Task<PropertyDocumentSearch> FindAsync(long requestId, long? batchId, string? question, CancellationToken ct)
    {
        var subject = SubjectOf(question);
        if (subject.IsEmpty || batchId is null) return PropertyDocumentSearch.None;
        var onlyOne = subject.Ids.Count == 1 && subject.Areas.Count == 0 && subject.Name is null ? subject.Ids[0].Label : null;

        // Chunks that could repeat the property: one cheap pass per distinctive term, limited to the request's current batch.
        var terms = subject.Ids.Select(i => i.Parts.Last()).Where(p => p.Length >= 3).Distinct().Take(MaxTerms - 4).Select(p => "%" + p + "%").ToList();
        foreach (var area in subject.Areas.Take(2))
        {
            terms.Add("%" + area + "%");
            terms.Add("%" + Regex.Replace(area.Split('.')[0], @"(\d)(?=(\d{3})+$)", "$1,") + "." + area.Split('.')[1] + "%");
        }
        if (subject.NameHead is { Length: >= 4 }) terms.Add("%" + subject.NameHead + "%");

        var truncated = false;
        var chunks = new Dictionary<long, (long DocId, string Name, string? FormType, int Page, string Text)>();
        foreach (var term in terms.Distinct())
        {
            var found = await db.DocumentChunks.AsNoTracking()
                .Where(c => c.RequestId == requestId && c.BatchId == batchId && EF.Functions.Like(c.ChunkText, term))
                .OrderBy(c => c.FilingDocumentId).ThenBy(c => c.PageNumber).Take(maxChunksPerTerm)
                .Select(c => new { c.ChunkId, c.FilingDocumentId, c.DocumentName, c.FormType, c.PageNumber, c.ChunkText }).ToListAsync(ct);
            if (found.Count >= maxChunksPerTerm) truncated = true;
            foreach (var c in found) chunks[c.ChunkId] = (c.FilingDocumentId, c.DocumentName, c.FormType, c.PageNumber, c.ChunkText);
        }

        var perDocument = new Dictionary<long, Evidence>();
        foreach (var (docId, name, formType, page, text) in chunks.Values)
        {
            var plain = Regex.Replace(text, @"(?<=\d),(?=\d{3})", "");
            var ids = StructuredFactsProvider.PropertyIds(text);
            var confirmedIds = ids.Where(t => subject.Ids.Any(q => StructuredFactsProvider.Compare(q, t) == StructuredFactsProvider.PropertyMatch.Match)).Select(t => t.Label).ToList();
            var otherKindIds = ids.Where(t => subject.Ids.Any(q => StructuredFactsProvider.Compare(q, t) == StructuredFactsProvider.PropertyMatch.Candidate)).Select(t => t.Label).ToList();
            var areaHit = subject.Areas.Any(a => Regex.IsMatch(plain, @"(?<![\d.])" + Regex.Escape(a) + AreaUnit, RegexOptions.IgnoreCase));
            var nameHit = subject.Name is not null && subject.Name.IsMatch(Regex.Replace(text, @"\s+", " "));
            if (confirmedIds.Count == 0 && otherKindIds.Count == 0 && !areaHit && !nameHit) continue;
            if (!perDocument.TryGetValue(docId, out var e)) perDocument[docId] = e = new Evidence(name, formType);
            e.Pages.Add(page);
            foreach (var i in confirmedIds) e.Ids.Add(i);
            foreach (var i in otherKindIds) e.OtherKindIds.Add(i);
            e.AreaHit |= areaHit; e.NameHit |= nameHit;
        }

        var qualifying = perDocument.Where(kv => kv.Value.Decide() != Verdict.No).ToList();
        if (qualifying.Count == 0) return new([], truncated, onlyOne);

        // The charge each document belongs to, from the links the system has built, with that charge's current status.
        var docIds = qualifying.Select(q => q.Key).ToList();
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);
        var links = await db.ChargeDocumentLinks.AsNoTracking().Where(l => l.RequestId == requestId && docIds.Contains(l.FilingDocumentId))
            .Select(l => new { l.FilingDocumentId, l.RocChargeNumber }).ToListAsync(ct);
        var numbers = links.Select(l => l.RocChargeNumber).Distinct().ToList();
        var charges = numbers.Count == 0 || runId is null ? new Dictionary<string, LinkedCharge>()
            : (await db.RocCharges.AsNoTracking().Where(c => c.IngestionRunId == runId && numbers.Contains(c.RocChargeNumber))
                .Select(c => new { c.RocChargeNumber, c.LatestChargeHolderRaw, c.ChargeStatus }).ToListAsync(ct))
                .ToDictionary(c => c.RocChargeNumber, c => new LinkedCharge(c.RocChargeNumber, c.LatestChargeHolderRaw, c.ChargeStatus));

        var all = qualifying.Select(q =>
        {
            var e = q.Value;
            var matched = e.Ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (e.AreaHit) matched.Add("the area " + string.Join(", ", subject.Areas));
            if (e.NameHit) matched.Add("the property's name");
            matched.AddRange(e.OtherKindIds.OrderBy(x => x, StringComparer.Ordinal).Select(x => x + " (a different kind of identifier from the question's)"));
            return new PropertyDocument(q.Key, e.Name, e.FormType, [.. e.Pages.Take(4)], matched,
                links.Where(l => l.FilingDocumentId == q.Key).Select(l => charges.GetValueOrDefault(l.RocChargeNumber)).Where(c => c is not null).Cast<LinkedCharge>().DistinctBy(c => c.Number).ToList(),
                e.Decide() == Verdict.Confirmed);
        }).OrderByDescending(d => d.Confirmed).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (all.Count > maxDocuments) truncated = true;
        return new([.. all.Take(maxDocuments)], truncated, onlyOne);
    }

    private enum Verdict { No, Candidate, Confirmed }

    private sealed class Evidence(string name, string? formType)
    {
        public string Name { get; } = name;
        public string? FormType { get; } = formType;
        public SortedSet<int> Pages { get; } = [];
        public HashSet<string> Ids { get; } = [];
        public HashSet<string> OtherKindIds { get; } = [];
        public bool AreaHit { get; set; }
        public bool NameHit { get; set; }

        public Verdict Decide()
        {
            if (Ids.Count >= 2) return Verdict.Confirmed;
            if (AreaHit && (NameHit || Ids.Count >= 1)) return Verdict.Confirmed;
            if (NameHit && Ids.Count >= 1) return Verdict.Confirmed;
            // one identifier alone, an area with its unit alone, or a number shared across kinds of identifier: it may be the property, it is not established
            if (Ids.Count == 1 || AreaHit || OtherKindIds.Count > 0) return Verdict.Candidate;
            return Verdict.No;   // the name alone, or nothing
        }
    }

    /// <summary>The facts the model reads: a header that says whether the list is complete, then one fact per document, candidates marked as such.</summary>
    public async Task<List<StructuredFact>> FactsAsync(long requestId, long? batchId, string? question, CancellationToken ct)
    {
        var search = await FindAsync(requestId, batchId, question, ct);
        var confirmed = search.Documents.Where(d => d.Confirmed).ToList();
        var candidates = search.Documents.Where(d => !d.Confirmed).ToList();
        if (confirmed.Count == 0 && candidates.Count == 0) return [];

        var facts = new List<StructuredFact>();
        if (search.OnlyIdentifier is { } only)
            facts.Add(new("Documents", $"The question names only one identifier ({only}). One identifier does not establish a property (a number can recur as a folio, a certificate number or another parcel), so " +
                "the documents below are only candidates that contain it; the finder cannot establish which documents are about the property from that alone. Ask for another identifier, the area or the property's name to narrow it.", null, null));
        if (confirmed.Count > 0)
            facts.Add(new("Documents", search.Truncated
                ? $"At least {confirmed.Count} document(s) on file whose own text describes the property the question names. The search reached its limit, so there may be more such documents than are listed: do not describe this list as complete."
                : $"{confirmed.Count} document(s) on file whose own text describes the property the question names. This is every such document in the current filing batch, not a sample; " +
                  "a charge shown as Satisfied here is a charge on this property that has been released.", null, null));
        else if (search.Truncated)
            facts.Add(new("Documents", "The search reached its limit, so the candidate documents below may not be all of them.", null, null));

        foreach (var d in confirmed) facts.Add(Fact("Document", d));
        foreach (var d in candidates) facts.Add(Fact("Candidate document (possible match only, identity not established)", d));
        return facts;

        static StructuredFact Fact(string label, PropertyDocument d) => new("Documents",
            $"{label}: {d.Name}" + (d.FormType is { Length: > 0 } ? $" ({d.FormType})" : "") + $", page(s) {string.Join(", ", d.Pages)}, names the property by {string.Join("; ", d.Matched)}"
            + (d.Charges.Count == 0 ? "; not linked to a charge by the system"
                : "; linked to charge " + string.Join(", ", d.Charges.Select(c => $"{c.Number} ({c.Holder}, {c.Status})"))), null, null);
    }
}
