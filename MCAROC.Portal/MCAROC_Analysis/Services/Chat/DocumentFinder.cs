using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>What a question asks for, when it asks for documents: the document types it names and how it named them.</summary>
public sealed record DocumentRequest(IReadOnlyList<string> FormTypes, string Asked, int? Year);

/// <summary>
/// Answers a request for a document ("show me the charge registration certificates", "give the name change certificate") from the document index, not
/// from text similarity (#499): every filing document of the request's authoritative batch whose classified type matches, newest first, each a link
/// that opens the PDF. When the filings hold none, it says so instead of answering from memory. No AI call — the answer is the index.
/// <para>Only a question that asks for the document itself (show, give, send, fetch, download, open, list, where is, copy of…) and names a known
/// type is answered here; a question about what a document says goes to the usual retrieval.</para>
/// </summary>
public sealed partial class DocumentFinder(AppDbContext db)
{
    public const int MaxListed = 15;

    /// <summary>Ways people ask for each document type, as regexes over the lower-cased question, and the classifier types they mean.</summary>
    private static readonly (Regex Phrase, string[] FormTypes, string Label)[] Synonyms =
    [
        (P(@"(charge )?satisfaction certificates?|certificates? of satisfaction( of charge)?"), ["Certificate of Satisfaction of Charge"], "certificate of satisfaction of charge"),
        (P(@"charge (registration )?certificates?|certificates? of registration of (the )?charges?|registration certificates?"), ["Certificate of Registration of Charge"], "certificate of registration of charge"),
        (P(@"name ?change certificates?|fresh certificates? of incorporation|certificates? of incorporation (on|pursuant to|after) (the )?change of name"), ["Certificate of Incorporation on Change of Name"], "certificate of incorporation on change of name"),
        (P(@"certificates? of incorporation|incorporation certificates?"), ["Certificate of Incorporation", "Certificate of Incorporation on Change of Name"], "certificate of incorporation"),
        (P(@"\bmoa\b|memorandum( of association)?"), ["MoA"], "memorandum of association"),
        (P(@"\baoa\b|articles( of association)?"), ["AoA"], "articles of association"),
        (P(@"hypothecation deeds?|deeds? of hypothecation|charge instruments?|instruments? of charge|loan agreements?|mortgage deeds?"), ["Instrument of Charge", "Charge Instrument"], "instrument of charge"),
        (P(@"\bchg ?-?1\b|charge forms?|form chg ?-?1"), ["Form CHG-1", "Form 8"], "charge form (CHG-1 / Form 8)"),
        (P(@"\bchg ?-?4\b"), ["Form CHG-4", "Form 17"], "charge satisfaction form (CHG-4 / Form 17)"),
        (P(@"allot+ees? lists?|lists? of allot+ees|allotment lists?|\bpas ?-?5\b"), ["List of Allottees (PAS-5)"], "list of allottees"),
        (P(@"\bpas ?-?3\b|return of allotment"), ["Form PAS-3", "Form 2 (Return of Allotment)"], "return of allotment (PAS-3)"),
        (P(@"shareholders? lists?|lists? of (share ?holders|members|directors)|members? lists?|directors? lists?"), ["List of Shareholders / Directors"], "list of shareholders / directors"),
        (P(@"annual returns?|\bmgt ?-?7\b"), ["Form MGT-7", "Form 20B (Annual Return)"], "annual return (MGT-7)"),
        (P(@"\bmgt ?-?14\b"), ["Form MGT-14"], "MGT-14"),
        (P(@"board resolutions?"), ["Board Resolution"], "board resolution"),
        (P(@"special resolutions?|shareholders?'? resolutions?|egm resolutions?|agm resolutions?"), ["Shareholder Resolution"], "shareholder resolution"),
        (P(@"financial statements?|annual accounts|balance sheets?|audited accounts|\baoc ?-?4\b"), ["Financial Statements", "Form AOC-4", "XBRL Financial Statement", "Form 23AC/23ACA (XBRL)", "Form Sch-V"], "financial statements"),
        (P(@"directors'? reports?|board'?s reports?"), ["Directors' Report"], "directors' report"),
        (P(@"\baoc ?-?2\b|related party contracts?"), ["Form AOC-2"], "AOC-2"),
        (P(@"\bdpt ?-?3\b|deposit returns?"), ["Form DPT-3"], "DPT-3"),
        (P(@"\bsh ?-?7\b"), ["Form SH-7"], "SH-7"),
        (P(@"\bmsme\b"), ["Form MSME-1"], "MSME return"),
        (P(@"auditor'?s? (appointment|resignation)|\badt ?-?[13]\b"), ["Form ADT-1", "Form ADT-3", "Auditor Appointment / Resignation (ADT attachment)"], "auditor appointment / resignation"),
        (P(@"valuation reports?"), ["Valuation Report"], "valuation report"),
        (P(@"\bcsr policy"), ["CSR Policy"], "CSR policy"),
        (P(@"condonation"), ["Condonation of Delay Order"], "condonation of delay order"),
        (P(@"\binc ?-?22\b|registered office (change|shift)"), ["Form INC-22", "Form 18 (Registered Office)"], "registered office notice (INC-22)"),
    ];

    private static Regex P(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The documents a question asks for, or null when it does not ask for a document itself.</summary>
    public static DocumentRequest? Parse(string question)
    {
        var q = question.ToLowerInvariant();
        if (!AsksForDocument().IsMatch(q)) return null;
        // "Show me what the charge certificate says about the interest rate" asks about its content: the usual retrieval answers that (review of #506).
        if (AsksAboutContent().IsMatch(q)) return null;
        foreach (var (phrase, formTypes, label) in Synonyms)
            if (phrase.IsMatch(q))
            {
                var year = Year().Match(q) is { Success: true } y ? int.Parse(y.Value, CultureInfo.InvariantCulture) : (int?)null;
                return new DocumentRequest(formTypes, label, year);
            }
        return null;
    }

    /// <summary>The answer to a document request, or null when the question does not ask for a document (it then goes to the usual retrieval).</summary>
    public async Task<ChatCompletionResult?> TryAnswerAsync(long requestId, string question, CancellationToken ct)
    {
        var asked = Parse(question);
        if (asked is null || asked.FormTypes.Count == 0) return null;

        var batch = await McaFilingBatchResolver.GetAuthoritativeBatchAsync(db, requestId, ct);
        if (batch is null)
            return new ChatCompletionResult($"This request has no MCA filings yet, so there is no {asked.Asked} to show.", true, []);

        var rows = await db.McaFilingDocuments.AsNoTracking()
            .Where(d => d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null && d.RetiredUtc == null && d.FormType != null && asked.FormTypes.Contains(d.FormType))
            .Select(d => new { d.FilingDocumentId, d.OriginalFileName, d.FormType, d.Filing!.Srn, d.PageCount })
            .ToListAsync(ct);
        var found = rows.Select(r => (Row: r, Date: DateInName(r.OriginalFileName)))
            .Where(x => asked.Year is null || x.Date?.Year == asked.Year)
            .OrderByDescending(x => x.Date ?? DateOnly.MinValue).ThenByDescending(x => x.Row.FilingDocumentId)
            .ToList();

        if (found.Count == 0)
        {
            // Every document without a specific type, folder-only ones included (they have a category but no type): any of them may be it (review of #506).
            var unclassified = await db.McaFilingDocuments.AsNoTracking()
                .CountAsync(d => d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null && d.RetiredUtc == null && d.FormType == null, ct);
            var text = $"The company's MCA filings contain no {asked.Asked}{(asked.Year is { } y ? $" from {y}" : "")}."
                + (unclassified > 0 ? $" ({unclassified} document(s) in the filings could not be identified by type; one of them may be it.)" : "");
            return new ChatCompletionResult(text, false, []);
        }

        var sb = new StringBuilder();
        sb.Append($"The company's MCA filings hold {found.Count} {asked.Asked}{(found.Count == 1 ? "" : " document(s)")}{(asked.Year is { } yr ? $" from {yr}" : "")}");
        sb.AppendLine(found.Count > MaxListed ? $"; the {MaxListed} newest are listed:" : ":");
        var citations = new List<ResolvedCitation>();
        foreach (var (x, i) in found.Take(MaxListed).Select((x, i) => (x, i)))
        {
            var label = $"{x.Row.FormType}: {x.Row.OriginalFileName}";
            sb.AppendLine($"{i + 1}. {x.Row.OriginalFileName}{(x.Date is { } d ? $", dated {d:dd MMM yyyy}" : "")} (SRN {x.Row.Srn})");
            citations.Add(new ResolvedCitation("DocumentChunk", null, x.Row.OriginalFileName, 1, null, null, label, DocumentId: x.Row.FilingDocumentId));
        }
        return new ChatCompletionResult(sb.ToString().TrimEnd(), false, citations);
    }

    /// <summary>The date MCA file names end with ("…-12032019.pdf", "…-120319.pdf"), if any.</summary>
    internal static DateOnly? DateInName(string fileName)
    {
        var m = NameDate().Match(fileName);
        if (!m.Success) return null;
        var digits = m.Groups["d"].Value;
        var format = digits.Length == 8 ? "ddMMyyyy" : "ddMMyy";
        return DateOnly.TryParseExact(digits, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 1956 && date <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))
            ? date : null;
    }

    [GeneratedRegex(@"\b(show|give|send|fetch|get|download|open|share|provide|attach|display|pull up|find|list|where (is|are|can i find)|copy of|copies of|i need|i want|need the|need a)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AsksForDocument();

    /// <summary>Words that make a question about what a document says rather than a request for the document itself.</summary>
    [GeneratedRegex(@"\b(what|which|how|why|when|who|whom|whose|says?|said|saying|mentions?|mentioned|about|states?|stated|stating|explain|summar\w*|terms?|amounts?|rates?|clauses?|details?|contents?|according)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AsksAboutContent();

    [GeneratedRegex(@"\b(19[5-9]\d|20\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Year();

    [GeneratedRegex(@"[-_ ](?<d>\d{8}|\d{6})(?:[-_ ]\d+)?(?:_signed)?\.[a-z]{3,4}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NameDate();
}
