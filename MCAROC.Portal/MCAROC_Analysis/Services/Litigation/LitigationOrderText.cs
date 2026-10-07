using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>What a non-PDF page the source serves for an order turns out to be.</summary>
public enum LitigationOrderPageKind
{
    /// <summary>The order's own text.</summary>
    OrderText,
    /// <summary>Only the source's notice that the order is not there ("Orders is not uploaded…", "File does not exist.").</summary>
    NotPublished,
    /// <summary>Not an order: an error body (JSON, the storage service's error document, a short "Expired" / "Access
    /// Denied" page) or content that mentions the PDF header — never stored as an order; retried.</summary>
    Rejected
}

/// <summary>Some forums' orders (consumer forums, Delhi High Court, DRT, ITAT and others) come from the litigation
/// service as a short HTML page stored under a ".pdf" name — the order's own text ("Counsel for D1 to D4 reported No
/// rejoinder…"), not an error. Others are a placeholder saying the court has not uploaded the order. This reads such
/// a page as plain text, so the order is kept as text (never as HTML, which the portal would otherwise serve from its
/// own origin), and tells a real order apart from a placeholder or a storage error.</summary>
public static partial class LitigationOrderText
{
    /// <summary>The shortest order text accepted. Every real order page measured (Coastal, 116 pages) is at least 53
    /// characters; the error pages seen ("Expired", "Not Found") are far shorter.</summary>
    private const int MinOrderTextLength = 40;

    /// <summary>What a non-PDF page from the source is. An error returned with HTTP 200 must never be stored as an
    /// order, so a page counts as order text only when nothing marks it as an error.</summary>
    public static (LitigationOrderPageKind Kind, string Text) Classify(byte[] bytes)
    {
        var raw = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
        var first = raw.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').FirstOrDefault();
        if (first is '{' or '[' || IsStorageError(bytes) || raw.Contains("%PDF-", StringComparison.Ordinal))
            return (LitigationOrderPageKind.Rejected, "");
        var text = FromHtml(bytes);
        if (text.Length > 0 && IsPlaceholder(text))
            return (LitigationOrderPageKind.NotPublished, text);
        if (text.Length < MinOrderTextLength || (text.Length < 200 && ErrorWords().IsMatch(text)))
            return (LitigationOrderPageKind.Rejected, text);
        return (LitigationOrderPageKind.OrderText, text);
    }

    /// <summary>The page's readable text: scripts and styles dropped, block ends kept as line breaks, entities
    /// decoded, spaces collapsed.</summary>
    public static string FromHtml(byte[] bytes)
    {
        var html = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes).TrimStart('\uFEFF');
        html = ScriptOrStyle().Replace(html, " ");
        html = BlockEnd().Replace(html, "\n");
        var text = WebUtility.HtmlDecode(Tag().Replace(html, " ")).Replace('\u00A0', ' ').Replace("\uFEFF", "");
        var lines = text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>The storage service's own error document (an expired or forbidden link), not an order.</summary>
    public static bool IsStorageError(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<Error>", StringComparison.Ordinal);
    }

    /// <summary>The page carries no order: empty, or only the source's notice that the order is not uploaded.</summary>
    public static bool IsPlaceholder(string text) =>
        string.IsNullOrWhiteSpace(text) || (text.Length <= 300 && NotUploaded().IsMatch(text));

    /// <summary>The order text in the shape the extractor gives a one-page document, so chunking, page citations
    /// and address matching treat it like any other order.</summary>
    public static string AsExtractedText(string text) => $"--- Page 1 (native) ---\n{text}";

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6]|/table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(expired|access\s+denied|forbidden|unauthori[sz]ed|permission\s+deni\w*|invalid\s+(token|link|url|request)|error)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorWords();

    [GeneratedRegex(@"\b(orders?|judg(e)?ments?)\s+(is|are|has|have)\s+not\s+(been\s+)?(uploaded|available|found)\b|\bno\s+(orders?|records?)\s+(found|available|uploaded)\b|\bfile\s+does\s+not\s+exist\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotUploaded();
}
