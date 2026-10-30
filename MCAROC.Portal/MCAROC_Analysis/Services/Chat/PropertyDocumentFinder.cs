using System.Globalization;
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

/// <summary>What the search found, and what it could not do. <paramref name="Unsearched"/> lists identifiers in the question that were too short to search on.</summary>
public sealed record PropertyDocumentSearch(IReadOnlyList<PropertyDocument> Documents, bool Truncated, string? OnlyIdentifier, IReadOnlyList<string> Unsearched, bool AreaOnlyInOtherUnitsNotSearched = false)
{
    public static readonly PropertyDocumentSearch None = new([], false, null, []);
}

/// <summary>
/// "Which documents are about this land?" (#564). Retrieval answers it from the top few chunks, so several near-identical deeds crowd out the one document that
/// releases the land, and the answer can say no charge was satisfied while a no-claim letter describing the very same land sits on file. This looks at whole
/// documents instead: every document of the authoritative filing batch with a passage that names the property, by identifiers read in context (survey, CTS,
/// plot numbers), by an area figure given with its unit, or by its quoted name. No AI call.
/// <para>A document is <b>confirmed</b> on two or more of the question's identifiers, on an area figure with its unit together with the quoted name or one identifier,
/// or on the quoted name together with one identifier. It is only a <b>candidate</b> on one identifier alone, on an area figure with its unit alone, or on a number
/// shared across kinds of identifier ("survey 42" and "plot 42"). A name alone is nothing (a compound is full of addresses), nor is a bare number (a folio, a
/// certificate number, a financial amount). An area matches only in the same unit, or in another unit of area converted (2,152.25 sq yards is 1,800 sq metres), never
/// by its digits alone.</para>
/// <para>The search has limits (chunks read per term, search terms, documents returned). When any is reached the result says it was cut short and is never described as
/// complete. Identifiers too short to search on (a one- or two-digit number recurs on almost every page) are reported as not searched.</para>
/// </summary>
public sealed class PropertyDocumentFinder(AppDbContext db, int maxChunksPerTerm = PropertyDocumentFinder.DefaultMaxChunksPerTerm, int maxDocuments = PropertyDocumentFinder.DefaultMaxDocuments,
    int maxTerms = PropertyDocumentFinder.DefaultMaxTerms)
{
    public const int DefaultMaxDocuments = 25;
    public const int DefaultMaxChunksPerTerm = 600;
    public const int DefaultMaxTerms = 40;
    private const int MinSearchableDigits = 3;
    private const int MaxDocumentsRead = 100;       // documents whose other chunks are read to find their area
    private const int MaxChunksReadForArea = 6000;

    private static readonly Regex Quoted = new("[\"“‘']([A-Za-z][^\"”’']{5,80})[\"”’']", RegexOptions.Compiled);

    // A figure with a unit of land measure ("2152.25 sq. yards", "1800 sq mtrs", "0.5 acre"). A rupee amount or a ratio shares the digits, not the meaning.
    private static readonly Regex AreaWithUnit = new(
        @"(?<![\d.,])(?<v>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*(?<u>sq\.?\s*(?:ft|feet|foot|yards?|yds?|mtrs?|metres?|meters?|m)\b|square\s+(?:feet|foot|yards?|metres?|meters?)\b|yards?\b|yds?\b|mtrs?\b|metres?\b|meters?\b|acres?\b|hectares?\b|guntas?\b|cents?\b|ft\b|feet\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A figure and the kind of unit it was given in. Equal digits in different units are different areas.</summary>
    internal readonly record struct AreaFigure(decimal Value, string Family, string Text)
    {
        // square metres per unit, where the unit has a fixed size
        private static readonly Dictionary<string, decimal> SqMetres = new()
        {
            ["yd2"] = 0.83612736m, ["m2"] = 1m, ["ft2"] = 0.09290304m, ["acre"] = 4046.8564224m, ["ha"] = 10000m, ["gunta"] = 101.17141056m, ["cent"] = 40.468564224m
        };

        public string Label => $"{Text} {Family}";

        /// <summary>True when the unit has a fixed size, so the same area can be recognised in another unit.</summary>
        public bool IsConvertible => SqMetres.ContainsKey(Family);

        public bool SameAs(AreaFigure other)
        {
            if (Family == other.Family) return Math.Abs(Value - other.Value) < 0.005m;
            if (!SqMetres.TryGetValue(Family, out var a) || !SqMetres.TryGetValue(other.Family, out var b)) return false;
            var x = Value * a; var y = other.Value * b;
            return y != 0 && Math.Abs(x - y) / y < 0.005m;   // converted: 2,152.25 sq yards is 1,799.6 sq m, which a document may round to 1,800
        }

        public static string FamilyOf(string unit)
        {
            var u = Regex.Replace(unit.ToLowerInvariant(), @"[^a-z]", "");
            if (u.Contains("yard") || u.Contains("yd")) return "yd2";
            if (u.Contains("acre")) return "acre";
            if (u.Contains("hectare")) return "ha";
            if (u.Contains("gunta")) return "gunta";
            if (u.Contains("cent")) return "cent";
            if (u.Contains("ft") || u.Contains("feet") || u.Contains("foot")) return "ft2";
            return "m2";   // mtr, metre, meter, sq m
        }
    }

    internal static IEnumerable<AreaFigure> AreasIn(string text) =>
        AreaWithUnit.Matches(text).Select(m =>
            decimal.TryParse(m.Groups["v"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
                ? new AreaFigure(v, AreaFigure.FamilyOf(m.Groups["u"].Value), m.Groups["v"].Value.Replace(",", "")) : default)
            .Where(a => a.Family is not null);

    /// <summary>What the question says about the property that a document could repeat: identifiers, areas (figure and unit) and a quoted name.</summary>
    internal sealed record Subject(IReadOnlyList<StructuredFactsProvider.PropertyId> Ids, IReadOnlyList<AreaFigure> Areas, Regex? Name, string? NameHead)
    {
        public bool IsEmpty => Ids.Count == 0 && Areas.Count == 0 && Name is null;
    }

    internal static Subject SubjectOf(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return new([], [], null, null);
        var ids = StructuredFactsProvider.PropertyIds(question);
        // a distinctive figure: with a fractional part and at least three digits before it (a bare "1800" would match half the land filings)
        var areas = AreasIn(question).Where(a => a.Text.Contains('.') && a.Text.Split('.')[0].Length >= 3).DistinctBy(a => (a.Value, a.Family)).ToList();
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
        var unsearched = subject.Ids.Where(i => i.Parts.Last().Length < MinSearchableDigits).Select(i => i.Label).Distinct().ToList();

        // Chunks that could repeat the property: one cheap pass per distinctive term, limited to the request's current batch. Every term is searched; if there are more
        // than the limit, the result says so.
        var terms = subject.Ids.Select(i => i.Parts.Last()).Where(p => p.Length >= MinSearchableDigits).Distinct().Select(p => "%" + p + "%").ToList();
        foreach (var a in subject.Areas)
        {
            terms.Add("%" + a.Text + "%");
            var parts = a.Text.Split('.');
            terms.Add("%" + Regex.Replace(parts[0], @"(\d)(?=(\d{3})+$)", "$1,") + "." + parts[1] + "%");
        }
        if (subject.NameHead is { Length: >= 4 }) terms.Add("%" + subject.NameHead + "%");
        terms = terms.Distinct().ToList();
        var truncated = terms.Count > maxTerms;
        terms = terms.Take(maxTerms).ToList();

        var chunks = new Dictionary<long, (long DocId, string Name, string? FormType, int Page, string Text)>();
        foreach (var term in terms)
        {
            var found = await db.DocumentChunks.AsNoTracking()
                .Where(c => c.RequestId == requestId && c.BatchId == batchId && EF.Functions.Like(c.ChunkText, term))
                .OrderBy(c => c.FilingDocumentId).ThenBy(c => c.PageNumber).Take(maxChunksPerTerm)
                .Select(c => new { c.ChunkId, c.FilingDocumentId, c.DocumentName, c.FormType, c.PageNumber, c.ChunkText }).ToListAsync(ct);
            if (found.Count >= maxChunksPerTerm) truncated = true;
            foreach (var c in found) chunks[c.ChunkId] = (c.FilingDocumentId, c.DocumentName, c.FormType, c.PageNumber, c.ChunkText);
        }

        var perDocument = new Dictionary<long, Evidence>();
        void Absorb(long docId, string name, string? formType, int page, string text)
        {
            var ids = StructuredFactsProvider.PropertyIds(text);
            var confirmedIds = ids.Where(t => subject.Ids.Any(q => StructuredFactsProvider.Compare(q, t) == StructuredFactsProvider.PropertyMatch.Match)).Select(t => t.Label).ToList();
            var otherKindIds = ids.Where(t => subject.Ids.Any(q => StructuredFactsProvider.Compare(q, t) == StructuredFactsProvider.PropertyMatch.Candidate)).Select(t => t.Label).ToList();
            var docAreas = subject.Areas.Count == 0 ? [] : AreasIn(text).ToList();
            var matchedAreas = subject.Areas.Where(q => docAreas.Any(d => q.SameAs(d))).Select(q => q.Label).ToList();
            var nameHit = subject.Name is not null && subject.Name.IsMatch(Regex.Replace(text, @"\s+", " "));
            if (confirmedIds.Count == 0 && otherKindIds.Count == 0 && matchedAreas.Count == 0 && !nameHit) return;
            if (!perDocument.TryGetValue(docId, out var e)) perDocument[docId] = e = new Evidence(name, formType);
            e.Pages.Add(page);
            foreach (var i in confirmedIds) e.Ids.Add(i);
            foreach (var i in otherKindIds) e.OtherKindIds.Add(i);
            foreach (var a in matchedAreas) e.Areas.Add(a);
            e.NameHit |= nameHit;
        }
        foreach (var (docId, name, formType, page, text) in chunks.Values) Absorb(docId, name, formType, page, text);

        // Second pass: a document found through an identifier or the name may give the area in another chunk (and another unit, which no search term can find). Read the
        // rest of every such document that is not yet confirmed and look for the area there. A document that gives the area only in another unit and names neither the
        // property nor an identifier cannot be found this way; that is disclosed, not hidden.
        if (subject.Areas.Count > 0)
        {
            var more = perDocument.Where(kv => kv.Value.Decide() != Verdict.Confirmed).Select(kv => kv.Key).ToList();
            if (more.Count > MaxDocumentsRead) { truncated = true; more = more.Take(MaxDocumentsRead).ToList(); }
            if (more.Count > 0)
            {
                var seen = chunks.Keys.ToHashSet();
                var rest = await db.DocumentChunks.AsNoTracking().Where(c => c.RequestId == requestId && c.BatchId == batchId && more.Contains(c.FilingDocumentId))
                    .OrderBy(c => c.FilingDocumentId).ThenBy(c => c.PageNumber).Take(MaxChunksReadForArea + 1)
                    .Select(c => new { c.ChunkId, c.FilingDocumentId, c.DocumentName, c.FormType, c.PageNumber, c.ChunkText }).ToListAsync(ct);
                if (rest.Count > MaxChunksReadForArea) { truncated = true; rest = rest.Take(MaxChunksReadForArea).ToList(); }
                foreach (var c in rest.Where(c => !seen.Contains(c.ChunkId))) Absorb(c.FilingDocumentId, c.DocumentName, c.FormType, c.PageNumber, c.ChunkText);
            }
        }
        var areaNote = subject.Areas.Any(a => a.IsConvertible);

        var qualifying = perDocument.Where(kv => kv.Value.Decide() != Verdict.No).ToList();
        if (qualifying.Count == 0) return new([], truncated, onlyOne, unsearched, areaNote);

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
            matched.AddRange(e.Areas.OrderBy(x => x, StringComparer.Ordinal).Select(x => "the area " + x));
            if (e.NameHit) matched.Add("the property's name");
            matched.AddRange(e.OtherKindIds.OrderBy(x => x, StringComparer.Ordinal).Select(x => x + " (a different kind of identifier from the question's)"));
            return new PropertyDocument(q.Key, e.Name, e.FormType, [.. e.Pages.Take(4)], matched,
                links.Where(l => l.FilingDocumentId == q.Key).Select(l => charges.GetValueOrDefault(l.RocChargeNumber)).Where(c => c is not null).Cast<LinkedCharge>().DistinctBy(c => c.Number).ToList(),
                e.Decide() == Verdict.Confirmed);
        }).OrderByDescending(d => d.Confirmed).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (all.Count > maxDocuments) truncated = true;
        return new([.. all.Take(maxDocuments)], truncated, onlyOne, unsearched, areaNote);
    }

    private enum Verdict { No, Candidate, Confirmed }

    private sealed class Evidence(string name, string? formType)
    {
        public string Name { get; } = name;
        public string? FormType { get; } = formType;
        public SortedSet<int> Pages { get; } = [];
        public HashSet<string> Ids { get; } = [];
        public HashSet<string> OtherKindIds { get; } = [];
        public HashSet<string> Areas { get; } = [];
        public bool NameHit { get; set; }

        public Verdict Decide()
        {
            var areaHit = Areas.Count > 0;
            if (Ids.Count >= 2) return Verdict.Confirmed;
            if (areaHit && (NameHit || Ids.Count >= 1)) return Verdict.Confirmed;
            if (NameHit && Ids.Count >= 1) return Verdict.Confirmed;
            // one identifier alone, an area with its unit alone, or a number shared across kinds of identifier: it may be the property, it is not established
            if (Ids.Count == 1 || areaHit || OtherKindIds.Count > 0) return Verdict.Candidate;
            return Verdict.No;   // the name alone, or nothing
        }
    }

    /// <summary>The facts the model reads: a header that says whether the list is complete, then one fact per document, candidates marked as such.</summary>
    public async Task<List<StructuredFact>> FactsAsync(long requestId, long? batchId, string? question, CancellationToken ct)
    {
        var search = await FindAsync(requestId, batchId, question, ct);
        var confirmed = search.Documents.Where(d => d.Confirmed).ToList();
        var candidates = search.Documents.Where(d => !d.Confirmed).ToList();
        if (confirmed.Count == 0 && candidates.Count == 0 && search.Unsearched.Count == 0 && !search.AreaOnlyInOtherUnitsNotSearched) return [];
        const string AreaGap = "One gap: a document that states the area only in another unit (for example square metres where the question gave square yards) and names neither the property nor one of its identifiers is not searched for.";

        var facts = new List<StructuredFact>();
        if (search.Unsearched.Count > 0)
            facts.Add(new("Documents", $"These identifiers in the question were NOT searched: {string.Join(", ", search.Unsearched)}. A one- or two-digit number recurs on almost every page, so it cannot find " +
                "documents on its own; the documents listed below (if any) were found without it. Ask for the longer identifiers, the area or the property's name.", null, null));
        if (search.OnlyIdentifier is { } only && candidates.Count > 0)
            facts.Add(new("Documents", $"The question names only one identifier ({only}). One identifier does not establish a property (a number can recur as a folio, a certificate number or another parcel), so " +
                "the documents below are only candidates that contain it; the finder cannot establish which documents are about the property from that alone. Ask for another identifier, the area or the property's name to narrow it.", null, null));
        if (confirmed.Count > 0)
            facts.Add(new("Documents", search.Truncated
                ? $"At least {confirmed.Count} document(s) on file whose own text describes the property the question names. The search reached its limit, so there may be more such documents than are listed: do not describe this list as complete."
                : search.Unsearched.Count > 0
                    ? $"At least {confirmed.Count} document(s) on file whose own text describes the property the question names. The search could not use some identifiers ({string.Join(", ", search.Unsearched)}), so there may be more such documents than are listed: do not describe this list as complete."
                    : $"{confirmed.Count} document(s) on file whose own text describes the property the question names. " +
                      (search.AreaOnlyInOtherUnitsNotSearched
                          ? "This is every such document that names the property or one of its identifiers (the area is checked, in any unit, in those documents), not a sample; "
                          : "This is every such document in the current filing batch, not a sample; ") +
                      "a charge shown as Satisfied here is a charge on this property that has been released." +
                      (search.AreaOnlyInOtherUnitsNotSearched ? " " + AreaGap : ""), null, null));
        // No confirmed document: the gap still matters. Nothing found, or only candidates, does not rule out a document that gives the area in another unit.
        if (confirmed.Count == 0 && search.AreaOnlyInOtherUnitsNotSearched)
            facts.Add(new("Documents", (candidates.Count == 0 ? "No document on file was found that describes the property the question names. " : "No document on file was confirmed as describing the property the question names. ") + AreaGap, null, null));
        if (confirmed.Count > 0) { }
        else if ((search.Truncated || search.Unsearched.Count > 0) && candidates.Count > 0)
            facts.Add(new("Documents", "The candidate documents below may not be all of them: the search reached its limit or could not use some identifiers.", null, null));

        foreach (var d in confirmed) facts.Add(Fact("Document", d));
        foreach (var d in candidates) facts.Add(Fact("Candidate document (possible match only, identity not established)", d));
        return facts;

        static StructuredFact Fact(string label, PropertyDocument d) => new("Documents",
            $"{label}: {d.Name}" + (d.FormType is { Length: > 0 } ? $" ({d.FormType})" : "") + $", page(s) {string.Join(", ", d.Pages)}, names the property by {string.Join("; ", d.Matched)}"
            + (d.Charges.Count == 0 ? "; not linked to a charge by the system"
                : "; linked to charge " + string.Join(", ", d.Charges.Select(c => $"{c.Number} ({c.Holder}, {c.Status})"))), null, null);
    }
}
