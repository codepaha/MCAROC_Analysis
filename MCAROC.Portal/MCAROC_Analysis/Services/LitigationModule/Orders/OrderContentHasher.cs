using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationModule.Orders;

/// <summary>Computes cryptographic content hashes for order bytes and normalized HTML text (plan section 4.5).</summary>
public static partial class OrderContentHasher
{
    /// <summary>Computes the lowercase hexadecimal SHA-256 hash of raw document bytes.</summary>
    public static string ComputeBytesHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Normalizes HTML order content (stripping markup tags, unescaping entities, collapsing whitespace)
    /// and returns the canonical text.</summary>
    public static string NormalizeHtmlText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Replace break/paragraph tags with space before stripping
        var withSpaces = HtmlLineBreaksRegex().Replace(html, " ");

        // Strip HTML tags
        var stripped = HtmlTagRegex().Replace(withSpaces, " ");

        // Decode HTML entities (e.g. &nbsp;, &amp;)
        var decoded = WebUtility.HtmlDecode(stripped);

        // Normalize whitespace: collapse runs of whitespace to a single space
        var collapsed = WhitespaceRegex().Replace(decoded, " ").Trim();

        return collapsed;
    }

    /// <summary>Computes the lowercase hexadecimal SHA-256 hash of normalized HTML order text.</summary>
    public static string ComputeHtmlTextHash(string? html)
    {
        var normalized = NormalizeHtmlText(html);
        if (string.IsNullOrEmpty(normalized)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return ComputeBytesHash(bytes);
    }

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"<(?:br|p|div|tr|td|th)[^>]*\/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlLineBreaksRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

