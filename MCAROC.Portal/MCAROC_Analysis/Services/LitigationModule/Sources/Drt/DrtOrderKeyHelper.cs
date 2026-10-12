using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Helper for DRT order URL normalization and deduplication keys (plan section 4.5).</summary>
public static partial class DrtOrderKeyHelper
{
    /// <summary>Extracts the normalised order file path from a DRT order URL (e.g. from <c>file=</c> param).</summary>
    public static string ExtractOrderKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var trimmed = url.Trim();

        try
        {
            var uri = new Uri(trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : "https://cis.drt.gov.in/" + trimmed.TrimStart('/'));

            var query = uri.Query.TrimStart('?');
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = Uri.UnescapeDataString(kv[1]);
                    // If base64-encoded file parameter
                    var decoded = TryDecodeBase64(raw);
                    if (decoded is not null)
                    {
                        // Some URLs embed raw HTML into the file parameter
                        if (decoded.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                            decoded.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
                        {
                            return "html-sha256:" + ComputeSha256Hex(decoded);
                        }

                        // Split off any ***<id>#1#<bench> suffix
                        var path = decoded.Split("***", 2)[0].Trim().Replace('\\', '/');
                        var m = DrtCourtRegex().Match(path);
                        if (m.Success)
                        {
                            return m.Groups[1].Value.TrimStart('/').ToLowerInvariant();
                        }
                        return path.TrimStart('/').ToLowerInvariant();
                    }
                }
            }
        }
        catch
        {
            // Fall back to clean URL if parsing fails
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>Builds the full composite OrderKey per plan section 4.5:
    /// <c>(tribunalId, caseTypeId, caseNo, caseYear, kind, orderDate, normalisedFilePath)</c>.
    /// Rejects incomplete identity fields rather than manufacturing placeholders to avoid key collisions.</summary>
    public static string BuildCompositeOrderKey(
        int tribunalId, int? caseTypeId, string? caseNo, int? caseYear, string kind, DateOnly? orderDate, string? url)
    {
        if (tribunalId <= 0)
        {
            throw new ArgumentException("DRT order key requires a positive tribunalId.", nameof(tribunalId));
        }

        if (!caseTypeId.HasValue || caseTypeId.Value <= 0)
        {
            throw new ArgumentException("DRT order key requires a valid caseTypeId.", nameof(caseTypeId));
        }

        if (string.IsNullOrWhiteSpace(caseNo))
        {
            throw new ArgumentException("DRT order key requires a non-empty caseNo.", nameof(caseNo));
        }

        if (!caseYear.HasValue || caseYear.Value <= 0)
        {
            throw new ArgumentException("DRT order key requires a valid caseYear.", nameof(caseYear));
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("DRT order key requires a non-empty kind.", nameof(kind));
        }

        if (!orderDate.HasValue)
        {
            throw new ArgumentException("DRT order key requires a valid orderDate.", nameof(orderDate));
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("DRT order key requires a non-empty url.", nameof(url));
        }

        var filePathKey = ExtractOrderKey(url);
        if (string.IsNullOrWhiteSpace(filePathKey))
        {
            throw new ArgumentException("DRT order key requires a valid order file URL.", nameof(url));
        }

        var ct = caseTypeId.Value;
        var cn = caseNo.Trim();
        var cy = caseYear.Value;
        var k = kind.Trim().ToLowerInvariant();
        var d = orderDate.Value.ToString("yyyy-MM-dd");
        return $"{tribunalId}|{ct}|{cn}|{cy}|{k}|{d}|{filePathKey}";
    }

    /// <summary>Daily and final report endpoints link to qrpdfview.php (an HTML viewer page).
    /// The actual PDF file is served by secure-pdf-serve.php or pdf1.php.</summary>
    public static string DirectPdfUrl(string url, string? orderBaseUrl = "https://cis.drt.gov.in/")
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var direct = url.Trim().Replace("/order/qrpdfview.php?", "/order/secure-pdf-serve.php?");

        if (!direct.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(orderBaseUrl))
        {
            var baseUri = new Uri(orderBaseUrl);
            direct = new Uri(baseUri, direct.TrimStart('/')).ToString();
        }

        return direct;
    }

    private static string? TryDecodeBase64(string raw)
    {
        try
        {
            var clean = raw.Trim();
            // Fix padding
            var pad = clean.Length % 4;
            if (pad > 0) clean += new string('=', 4 - pad);
            var bytes = Convert.FromBase64String(clean);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [GeneratedRegex(@"drtcourt/(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DrtCourtRegex();
}

