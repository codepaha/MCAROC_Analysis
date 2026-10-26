using System.Globalization;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>One change of name as a certificate of incorporation on change of name states it: the name before, the name after, the date
/// the certificate gives (the new name takes effect that day), and the page it is on. Names are as the text reads, which can run words
/// together ("COASTALPROJECTS LIMITED"); <see cref="NameChanges.Merge"/> shows the register's spelling where the two agree.</summary>
public sealed record CertificateNameChange(string FromName, string ToName, DateOnly? EffectiveDate, long FilingDocumentId, int Page);

/// <summary>#497 PR B: reads the certificate the Registrar issues when a company changes its name. Two wordings are in use: the Companies
/// Act 1956 one ("… the name of the said company is this day changed to Y and this Certificate is issued …", the old name in "In the matter
/// of M/s X") and the 2014 rules one ("… the name of the company has been changed from X to Y with effect from the date of this
/// certificate"). Both end "Given under my hand at &lt;place&gt; this &lt;date&gt;", the date in words or figures. Pure: a text in, the
/// changes it states out; a text that only talks about a change of name (a notice, a resolution) states none.</summary>
public static partial class NameChangeCertificateReader
{
    /// <summary>Bumped when the reading changes, so cached readings are read again.</summary>
    public const int Version = 1;

    [GeneratedRegex(@"has\s*been\s*changed\s*from\s*[""“]?(?<from>.{3,160}?)[""”]?\s+to\s+[""“]?(?<to>.{3,160}?)[""”]?\s*(?:with\s*effect\s*from|and\s*th(?:is|e)\s*(?:certificate|company))", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ChangedFromTo();

    [GeneratedRegex(@"is\s*this\s*day\s*changed\s*to\s*[""“]?(?<to>.{3,160}?)[""”]?\s*and\s*this\s*certificate", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ChangedThisDay();

    [GeneratedRegex(@"In\s*the\s*matter\s*of\s*(?:M/?s\.?\s*)?(?<from>.{3,160}?)\s*I\s*hereby\s*certify", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InTheMatterOf();

    [GeneratedRegex(@"\(No\.\s*1\s*of\s*1956\)\s*as\s*(?:M/?s\.?\s*)?(?<from>.{3,160}?)\s*having\s*duly\s*passed", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IncorporatedAs();

    [GeneratedRegex(@"Given\s*under\s*my\s*hand\s*at\s*.{1,80}?\s*this\s*(?<date>.{4,90})", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GivenUnderMyHand();

    [GeneratedRegex(@"--- Page (\d+) \((?:native|OCR)\) ---", RegexOptions.IgnoreCase)]
    private static partial Regex PageMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"I\s*hereby\s*certify", RegexOptions.IgnoreCase)]
    private static partial Regex HerebyCertify();

    // Where the next certificate begins: its heading, its certification, or its own change of name.
    [GeneratedRegex(@"I\s*hereby\s*certify|In\s*the\s*matter\s*of|Certificate\s*of\s*Incorporation|GOVERNMENT\s*OF\s*INDIA|has\s*been\s*changed\s*from|is\s*this\s*day\s*changed", RegexOptions.IgnoreCase)]
    private static partial Regex NextCertificate();

    public static IReadOnlyList<CertificateNameChange> Read(string? text, long filingDocumentId)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<CertificateNameChange>();
        foreach (var (page, raw) in Pages(text))
        {
            var flat = Whitespace().Replace(raw, " ");
            // Only the Registrar's certificate certifies; a notice or resolution about the change does not.
            if (!HerebyCertify().IsMatch(flat)) continue;
            foreach (Match m in ChangedFromTo().Matches(flat))
                Add(m.Groups["from"].Value, m.Groups["to"].Value, flat, m.Index + m.Length, page);
            foreach (Match m in ChangedThisDay().Matches(flat))
            {
                // The old name is the one the certificate is "in the matter of", else the one the company was incorporated as.
                var before = flat[..m.Index];
                var from = InTheMatterOf().Matches(before).LastOrDefault() ?? IncorporatedAs().Matches(before).LastOrDefault();
                if (from is not null) Add(from.Groups["from"].Value, m.Groups["to"].Value, flat, m.Index + m.Length, page);
            }
        }
        return found;

        void Add(string from, string to, string flat, int after, int page)
        {
            from = Clean(from);
            to = Clean(to);
            if (from.Length < 3 || to.Length < 3 || NameChanges.Key(from) == NameChanges.Key(to)) return;
            // The certificate's own closing line, never one past the start of the next certificate on the page: an unreadable
            // closing leaves the date unknown rather than borrowing the next certificate's.
            var next = NextCertificate().Match(flat, after);
            var end = Math.Min(next.Success ? next.Index : flat.Length, after + 1500);
            var given = GivenUnderMyHand().Match(flat[after..end]);
            var date = given.Success ? ParseDate(given.Groups["date"].Value) : null;
            if (found.Any(f => NameChanges.Key(f.FromName) == NameChanges.Key(from) && NameChanges.Key(f.ToName) == NameChanges.Key(to) && f.EffectiveDate == date)) return;
            found.Add(new CertificateNameChange(from, to, date, filingDocumentId, page));
        }
    }

    private static string Clean(string name)
    {
        name = Whitespace().Replace(name, " ").Trim().Trim('"', '“', '”', '\'', ',', '.', ' ');
        if (name.StartsWith("M/s", StringComparison.OrdinalIgnoreCase)) name = name[3..].TrimStart('.', ' ');
        return name;
    }

    private static IEnumerable<(int Page, string Text)> Pages(string text)
    {
        var markers = PageMarker().Matches(text);
        if (markers.Count == 0) { yield return (1, text); yield break; }
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            yield return (int.Parse(markers[i].Groups[1].Value, CultureInfo.InvariantCulture), text[start..end]);
        }
    }

    // ── Dates: "Twenty Fifth day of November Two Thousand Ten", "ELEVENTH day of APRIL TWO THOUSAND TWENTY FIVE",
    //    "11th day of April 2025", "11/04/2025" ──

    private static readonly string[] Months =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    [GeneratedRegex(@"^(?<day>.{1,40}?)\s*day\s*of\s*(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\s*,?\s*(?<year>.{1,60})", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DayOfMonthYear();

    [GeneratedRegex(@"^(?<d>\d{1,2})\s*[/.-]\s*(?<m>\d{1,2})\s*[/.-]\s*(?<y>\d{4})")]
    private static partial Regex NumericDate();

    // Number words read from the front of the text, longest first, so a word the text ran on into the next ("FIVEBrijesh") still reads.
    [GeneratedRegex(@"\G[\s,-]*(?<w>\d+(?:st|nd|rd|th)?|seventeenth|thirteenth|fourteenth|eighteenth|nineteenth|fifteenth|sixteenth|twentieth|thirtieth|eleventh|seventeen|thirteen|fourteen|eighteen|nineteen|thousand|twelfth|fifteen|sixteen|hundred|seventh|seventy|second|eleven|twelve|twenty|thirty|eighty|ninety|eighth|fourth|forty|fifty|sixty|first|third|fifth|sixth|ninth|tenth|three|seven|eight|four|five|nine|one|two|six|ten|and)", RegexOptions.IgnoreCase)]
    private static partial Regex NumberWord();

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        for (var m = NumberWord().Match(text); m.Success; m = m.NextMatch()) words.Add(m.Groups["w"].Value);
        return words;
    }

    private static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60,
        ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9,
        ["tenth"] = 10, ["eleventh"] = 11, ["twelfth"] = 12, ["thirteenth"] = 13, ["fourteenth"] = 14, ["fifteenth"] = 15, ["sixteenth"] = 16,
        ["seventeenth"] = 17, ["eighteenth"] = 18, ["nineteenth"] = 19, ["twentieth"] = 20, ["thirtieth"] = 30,
    };

    internal static DateOnly? ParseDate(string text)
    {
        text = text.Trim();
        var numeric = NumericDate().Match(text);
        if (numeric.Success)
            return Date(int.Parse(numeric.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(numeric.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(numeric.Groups["d"].Value, CultureInfo.InvariantCulture));

        var m = DayOfMonthYear().Match(text);
        if (!m.Success) return null;
        var month = Array.IndexOf(Months, m.Groups["month"].Value.ToLowerInvariant()) + 1;
        return Day(m.Groups["day"].Value) is { } day && Year(m.Groups["year"].Value) is { } year ? Date(year, month, day) : null;
    }

    // "Twenty Fifth", "ELEVENTH", "twenty-first", "11th", "1st".
    private static int? Day(string text)
    {
        var tokens = Words(text);
        if (tokens.Count == 0) return null;
        if (int.TryParse(tokens[0].TrimEnd('s', 't', 'n', 'd', 'r', 'h', 'S', 'T', 'N', 'D', 'R', 'H'), NumberStyles.None, CultureInfo.InvariantCulture, out var digits)) return digits;
        var day = 0;
        foreach (var t in tokens)
        {
            if (Ordinals.TryGetValue(t, out var o)) return day + o;
            if (Units.TryGetValue(t, out var u) && u % 10 == 0 && u >= 20) day += u;
            else return null;
        }
        return null;
    }

    // "Two Thousand Ten", "TWO THOUSAND TWENTY FIVE", "Nineteen Hundred Ninety Five", "Two Thousand and Nine", "2025"; stops at the first
    // word that is not part of the number.
    private static int? Year(string text)
    {
        var tokens = Words(text);
        if (tokens.Count == 0) return null;
        if (tokens[0].Length == 4 && int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var digits)) return digits;
        int total = 0, current = 0, read = 0;
        foreach (var t in tokens)
        {
            if (Units.TryGetValue(t, out var u)) current += u;
            else if (t.Equals("hundred", StringComparison.OrdinalIgnoreCase)) current *= 100;
            else if (t.Equals("thousand", StringComparison.OrdinalIgnoreCase)) { total += current * 1000; current = 0; }
            else if (t.Equals("and", StringComparison.OrdinalIgnoreCase) && read > 0) continue;
            else break;
            read++;
        }
        var year = total + current;
        return year is >= 1850 and <= 2100 ? year : null;
    }

    private static DateOnly? Date(int year, int month, int day) =>
        year is >= 1850 and <= 2100 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
}

/// <summary>How a change of name shown on the request page is backed.</summary>
public enum NameChangeCheck
{
    /// <summary>The name history and a certificate state it, and both dates are read and agree (a day apart at most: "in use until" and "changed on").</summary>
    Confirmed,
    /// <summary>The name history and a certificate state it, with different dates; the certificate's is the official one.</summary>
    DatesDiffer,
    /// <summary>A certificate states the change but its date could not be read; the date shown is the name history's, not confirmed.</summary>
    CertificateUndated,
    /// <summary>The name history states the change without a date; the date shown is the certificate's.</summary>
    HistoryUndated,
    /// <summary>A certificate states it; the name history does not.</summary>
    CertificateOnly,
    /// <summary>The name history states it; no certificate for it was read in the filings.</summary>
    HistoryOnly,
}

/// <summary>One change of name, from the name history, a certificate, or both. <see cref="Date"/> is the certificate's when there is
/// one, else the history's "in use until".</summary>
public sealed record NameChange(
    string FromName, string ToName, DateOnly? Date, NameChangeCheck Check,
    CompanyNameHistory? HistoryRow, DateOnly? HistoryDate,
    CertificateNameChange? Certificate);

public static class NameChanges
{
    /// <summary>Spacing and punctuation apart, the same name: certificate text often runs words together.</summary>
    public static string Key(string? name) => CompanyNameNormalizer.Normalize(name).NameNormalized.Replace(" ", "", StringComparison.Ordinal);

    /// <summary>The name history (oldest first; each name in use until its <see cref="CompanyNameHistory.TillDate"/>, then the next one or
    /// the current name) checked against the certificates read. A certificate is matched to the history change with the same former name
    /// (the nearest by date when the name was used twice); the history's own "renamed to" is only its row order, so the certificate's new
    /// name is kept when the two differ. With no certificates, the history as it is.</summary>
    public static IReadOnlyList<NameChange> Merge(IReadOnlyList<CompanyNameHistory> history, string currentName, IReadOnlyList<CertificateNameChange> certificates)
    {
        var historyChanges = history.Select((row, i) => (Row: row, To: i + 1 < history.Count ? history[i + 1].PreviousName : currentName)).ToList();
        var used = new HashSet<int>();
        var changes = new List<NameChange>();

        foreach (var cert in certificates)
        {
            var match = historyChanges.Select((h, i) => (h, i))
                .Where(x => !used.Contains(x.i) && Key(x.h.Row.PreviousName) == Key(cert.FromName))
                .OrderBy(x => cert.EffectiveDate is { } c && x.h.Row.TillDate is { } t ? Math.Abs(c.DayNumber - t.DayNumber) : int.MaxValue)
                .Select(x => ((CompanyNameHistory Row, string To, int Index)?)(x.h.Row, x.h.To, x.i))
                .FirstOrDefault();

            var to = Spelled(cert.ToName, currentName, history.Select(h => h.PreviousName));
            if (match is { } h)
            {
                used.Add(h.Index);
                // A date is confirmed only by two readable dates; a missing one is said, never taken as agreement.
                var check = (cert.EffectiveDate, h.Row.TillDate) switch
                {
                    (null, _) => NameChangeCheck.CertificateUndated,
                    (_, null) => NameChangeCheck.HistoryUndated,
                    ({ } c, { } t) when Math.Abs(c.DayNumber - t.DayNumber) <= 1 => NameChangeCheck.Confirmed,
                    _ => NameChangeCheck.DatesDiffer,
                };
                changes.Add(new NameChange(h.Row.PreviousName, Key(h.To) == Key(cert.ToName) ? h.To : to, cert.EffectiveDate ?? h.Row.TillDate,
                    check, h.Row, h.Row.TillDate, cert));
            }
            else
            {
                changes.Add(new NameChange(Spelled(cert.FromName, currentName, history.Select(x => x.PreviousName)), to, cert.EffectiveDate,
                    NameChangeCheck.CertificateOnly, null, null, cert));
            }
        }

        for (var i = 0; i < historyChanges.Count; i++)
            if (!used.Contains(i))
                changes.Add(new NameChange(historyChanges[i].Row.PreviousName, historyChanges[i].To, historyChanges[i].Row.TillDate,
                    NameChangeCheck.HistoryOnly, historyChanges[i].Row, historyChanges[i].Row.TillDate, null));

        // The dated changes by date; an undated one stays after the history row before it.
        DateOnly SortDate(NameChange c) => c.Date ?? (c.HistoryRow is not { } row ? DateOnly.MaxValue
            : history.Where(h => h.DisplayOrder < row.DisplayOrder && h.TillDate is not null).Select(h => h.TillDate!.Value).DefaultIfEmpty(DateOnly.MinValue).Max());
        return changes
            .OrderBy(SortDate)
            .ThenBy(c => c.HistoryRow?.DisplayOrder ?? int.MaxValue)
            .ToList();
    }

    // The register's spelling of a name the certificate text ran together, when it is the current name or one in the history.
    private static string Spelled(string certificateName, string currentName, IEnumerable<string> known) =>
        known.Prepend(currentName).FirstOrDefault(n => Key(n) == Key(certificateName)) ?? certificateName;
}

/// <summary>Loads the certificates of change of name in the request's filings: the constitutional documents of its authoritative batch
/// (a certificate is also found inside an altered memorandum), read from their extracted text and kept in memory per batch until a
/// document changes. The same change in several documents is one, from the certificate itself where there is one.</summary>
public static class NameChangeCertificateLoader
{
    public const string CertificateFormType = "Certificate of Incorporation on Change of Name";

    public static async Task<IReadOnlyList<CertificateNameChange>> LoadAsync(AppDbContext db, long requestId, CancellationToken ct, IMemoryCache? cache = null)
    {
        var batch = await McaFilingBatchResolver.GetAuthoritativeBatchAsync(db, requestId, ct);
        if (batch is null) return [];

        var docs = await db.McaFilingDocuments.AsNoTracking()
            .Where(d => d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null && d.ExtractedTextPath != null
                        && (d.Category == FilingCategory.Constitutional || d.FormType == CertificateFormType))
            .Select(d => new { d.FilingDocumentId, d.FormType, d.ExtractedTextPath, d.UpdatedAt })
            .OrderBy(d => d.FilingDocumentId)
            .ToListAsync(ct);
        if (docs.Count == 0) return [];

        var key = $"name-change-certificates:{NameChangeCertificateReader.Version}:{batch.BatchId}:{docs.Count}:{docs[^1].FilingDocumentId}:{docs.Max(d => d.UpdatedAt).Ticks}";
        if (cache?.TryGetValue(key, out IReadOnlyList<CertificateNameChange>? cached) == true && cached is not null) return cached;

        var readings = new List<CertificateNameChange>();
        foreach (var d in docs.OrderByDescending(d => d.FormType == CertificateFormType).ThenBy(d => d.FilingDocumentId))
        {
            string text;
            try
            {
                var fullTextPath = MCAROC_Analysis.Services.Storage.StoragePathHelper.ResolveFullPath(d.ExtractedTextPath);
                if (!File.Exists(fullTextPath)) continue;
                text = await File.ReadAllTextAsync(fullTextPath, ct);
            }
            catch (IOException) { continue; }
            foreach (var change in NameChangeCertificateReader.Read(text, d.FilingDocumentId))
            {
                // The same change already read (a copy with no readable date counts as the same); a dated copy replaces an undated one.
                var same = readings.FindIndex(r => NameChanges.Key(r.FromName) == NameChanges.Key(change.FromName)
                                                   && NameChanges.Key(r.ToName) == NameChanges.Key(change.ToName)
                                                   && (r.EffectiveDate == change.EffectiveDate || r.EffectiveDate is null || change.EffectiveDate is null));
                if (same < 0) readings.Add(change);
                else if (readings[same].EffectiveDate is null && change.EffectiveDate is not null) readings[same] = change;
            }
        }

        IReadOnlyList<CertificateNameChange> result = readings;
        cache?.Set(key, result, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) });
        return result;
    }
}
