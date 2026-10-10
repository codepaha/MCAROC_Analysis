using System.Globalization;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Picks out the judgments among a company's orders, for a company with so many cases that classifying every order would cost hours and
/// mostly label adjournments. Judgments (final orders, decrees, awards, and the order that disposes of a case) are where the outcome is, so those are
/// the orders worth a classification. It is free and deterministic and reads the order text, because the source's own order type is not enough:
/// most orders arrive typed as the generic "order/judgement", whether they dispose of the case or only adjourn it.
/// <para>An order is a judgment when its opening names it as one (judgment, final order, decree, award), or it ends with an operative disposal
/// (dismissed, allowed, disposed of, decreed, set aside, convicted, costs payable), or it is the newest order of a case that has been disposed.
/// An order typed interim, daily or adjournment is left out unless its text disposes of something. A false positive costs one cheap classification;
/// a missed judgment costs an outcome, so the test leans towards including.</para></summary>
public static partial class LitigationJudgmentFilter
{
    private const int HeadChars = 1200, TailChars = 3000;

    /// <summary>The ids of the order documents worth classifying: every judgment, as defined above. <paramref name="caseStatus"/> says which cases are disposed.</summary>
    public static HashSet<long> JudgmentDocumentIds(IEnumerable<LitigationOrderChunk> chunks, IReadOnlyDictionary<long, LitigationCaseStatusBucket> caseStatus)
    {
        var documents = chunks.GroupBy(c => c.LitigationOrderDocumentId).Select(g =>
        {
            var ordered = g.OrderBy(c => c.PageNumber).ThenBy(c => c.ChunkIndex).ToList();
            var first = ordered[0];
            return (Id: g.Key, first.LitigationCaseId, first.LitigationCaseOrderId, first.OrderType, first.OrderDate,
                Head: HeadWindow(ordered), Tail: TailWindow(ordered));
        }).ToList();

        // The newest order with text of each disposed case is the one that disposed it.
        var newestOfDisposed = documents
            .Where(d => caseStatus.GetValueOrDefault(d.LitigationCaseId) == LitigationCaseStatusBucket.Disposed)
            .GroupBy(d => d.LitigationCaseId)
            .Select(g => g.OrderByDescending(d => ParseDate(d.OrderDate) ?? DateTime.MinValue).ThenByDescending(d => d.LitigationCaseOrderId).First().Id)
            .ToHashSet();

        return documents.Where(d => IsJudgment(d.OrderType, d.Head, d.Tail, newestOfDisposed.Contains(d.Id))).Select(d => d.Id).ToHashSet();
    }

    internal static bool IsJudgment(string? orderType, string head, string tail, bool newestOrderOfDisposedCase)
    {
        if (newestOrderOfDisposedCase) return true;
        if (Disposal().IsMatch(tail)) return true;               // ends with an operative disposal, whatever it is called
        if (InterimType().IsMatch(orderType ?? "")) return false; // an interim or daily order that disposes of nothing
        return Heading().IsMatch(head);                          // opens as a judgment / final order / decree / award
    }

    /// <summary>The order's own words: each stored chunk opens with a bracketed label (court, case, CNR, "Order: order/judgement 12-11-2018", page) that
    /// is the source's metadata, not the order, and would make every order look like a judgment.</summary>
    internal static string Body(string chunkText) => ChunkLabel().Replace(chunkText, "", 1);

    /// <summary>The first <see cref="HeadChars"/> of the order's own words, across as many consecutive chunks as it takes.</summary>
    internal static string HeadWindow(IReadOnlyList<LitigationOrderChunk> ordered)
    {
        var text = new System.Text.StringBuilder();
        foreach (var chunk in ordered)
        {
            text.Append(Body(chunk.ChunkText)).Append('\n');
            if (text.Length >= HeadChars) break;
        }
        return text.Length <= HeadChars ? text.ToString() : text.ToString(0, HeadChars);
    }

    /// <summary>The last <see cref="TailChars"/> of the order's own words, across consecutive chunks: the operative part is often a page or more
    /// before the signature page, so the last chunk alone can hold nothing but the signatures.</summary>
    internal static string TailWindow(IReadOnlyList<LitigationOrderChunk> ordered)
    {
        var parts = new List<string>();
        var length = 0;
        for (var i = ordered.Count - 1; i >= 0 && length < TailChars; i--)
        {
            var body = Body(ordered[i].ChunkText);
            parts.Add(body);
            length += body.Length + 1;
        }
        parts.Reverse();
        var text = string.Join('\n', parts);
        return text.Length <= TailChars ? text : text[^TailChars..];
    }

    [GeneratedRegex(@"^\s*\[[^\]]*\]")]
    private static partial Regex ChunkLabel();

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] formats = ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy", "yyyy/MM/dd", "dd MMM yyyy", "d MMMM yyyy"];
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact) ? exact
            : DateTime.TryParse(value, CultureInfo.GetCultureInfo("en-IN"), DateTimeStyles.None, out var loose) ? loose : null;
    }

    [GeneratedRegex(@"\b(final\s+(order|judg(e)?ment)|judg(e)?ment|decree|arbitral\s+award|award|order\s+and\s+judg(e)?ment|common\s+order|conviction|sentence)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\b(is|are|stands?|shall\s+stand|be|hereby|accordingly|therefore|result)\b[^.\n]{0,30}\b(dismissed|allowed|disposed\s+of|decreed|quashed|set\s+aside|rejected|convicted|acquitted|partly\s+allowed|partly\s+decreed)\b|\bdisposed\s+of\b|\bin\s+the\s+result\b|\bcosts?\s+of\s+rs\b|\bpetition\s+(is|stands)\s+dismissed\b|\bpay\s+(a\s+)?(sum|costs|penalty|fine|amount)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Disposal();

    [GeneratedRegex(@"interm|daily|adjourn|mention|notice|cause\s*list", RegexOptions.IgnoreCase)]
    private static partial Regex InterimType();
}
