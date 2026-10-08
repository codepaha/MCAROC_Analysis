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
    /// <summary>Not an order: an error body (JSON, the storage service's error document, an "Expired" / "Access
    /// Denied" page of any length) or content that mentions the PDF header — never stored as an order; retried.</summary>
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
        if (text.Length < MinOrderTextLength || IsErrorPage(text))
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

    /// <summary>The page carries no order: empty, or nothing but the source's notice that the order is not uploaded
    /// (optionally naming the case). A notice inside other text ("The earlier order is not uploaded. The respondent
    /// shall file…") is an order that mentions it, not a placeholder.</summary>
    public static bool IsPlaceholder(string text) =>
        string.IsNullOrWhiteSpace(text) || (text.Length <= 300 && NotUploadedNotice().IsMatch(text));

    /// <summary>An error page, whatever its length: a title or heading line that is nothing but error terms ("Access
    /// Denied", "403 Forbidden", "Access Denied / Session Expired"), or a phrase only a transport / sign-in error uses
    /// ("your session has expired", "sign in again", "invalid or expired"). A bare word is never enough — orders say
    /// "the limitation period has expired" or "error apparent on the face of the record", however short.</summary>
    private static bool IsErrorPage(string text) =>
        text.Split('\n').Take(3).Any(line => ErrorHeading().IsMatch(line)) || ErrorPhrase().IsMatch(text);

    /// <summary>The order text in the shape the extractor gives a one-page document, so chunking, page citations
    /// and address matching treat it like any other order.</summary>
    public static string AsExtractedText(string text) => $"--- Page 1 (native) ---\n{text}";

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6]|/table|/title)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();

    /// <summary>A line made only of error terms, optionally with an HTTP status code ("403 Forbidden", "Access Denied /
    /// Session Expired").</summary>
    [GeneratedRegex(@"^\W*(?:\d{3}\W*)?(?:access\s*denied|forbidden|unauthori[sz]ed|permission\s+denied|not\s+found|bad\s+request|(?:internal\s+)?server\s+error|service\s+unavailable|(?:session|link|token|request)\s+(?:has\s+)?expired|expired|error|invalid\s+(?:token|link|url|request))(?:\W+(?:\d{3}\W*)?(?:access\s*denied|forbidden|unauthori[sz]ed|permission\s+denied|not\s+found|bad\s+request|(?:internal\s+)?server\s+error|service\s+unavailable|(?:session|link|token|request)\s+(?:has\s+)?expired|expired|error|invalid\s+(?:token|link|url|request)))*\W*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorHeading();

    /// <summary>Phrases only a transport or sign-in error uses, never an order.</summary>
    [GeneratedRegex(@"\b(?:session\s+(?:has\s+)?(?:expired|timed\s+out)|(?:sign|log)\s*in\s+again|(?:link|token|url|signature)\s+(?:has\s+)?expired|request\s+has\s+expired|invalid\s+or\s+(?:has\s+)?expired|(?:token|signature)\s+(?:is\s+)?invalid|invalid\s+(?:token|signature|session)|you\s+do\s+not\s+have\s+permission\s+to\s+access|requested\s+(?:url|resource|document|file)\s+was\s+not\s+found)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPhrase();

    /// <summary>The whole text is one or more not-uploaded notices, each optionally naming the case ("Orders is not
    /// uploaded for case number CRLRC/292/2016"). The case reference must start with a capital or a digit, so a notice
    /// followed by a direction ("…not uploaded in the respondent's reply") never matches.</summary>
    [GeneratedRegex(@"^(?:\s*(?:the\s+)?(?:(?:orders?|judg(?:e)?ments?)\s+(?:is|are|has|have)\s+not\s+(?:yet\s+)?(?:been\s+)?(?:uploaded|available|found)|no\s+(?:orders?|records?)\s+(?:found|available|uploaded)|file\s+does\s+not\s+exist)(?:\s+(?:for|in|of)\s+(?:the\s+)?(?:case|matter|petition|appeal)?(?:\s+(?:number|no\.?))?\s*[:\-]?\s*(?-i:[A-Z0-9][A-Za-z0-9./()\-]*(?:\s+(?:of\s+)?[A-Z0-9][A-Za-z0-9./()\-]*)*))?\s*[.!]?)+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotUploadedNotice();
}
