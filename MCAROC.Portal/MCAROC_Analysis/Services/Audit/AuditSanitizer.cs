using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.Audit;

/// <summary>
/// Redaction and sanitization engine for audit error messages and unstructured diagnostic text.
/// Strips authorization tokens, credentials, connection strings, filesystem paths, PANs, GSTINs,
/// emails, and stack traces before storage or UI display.
/// </summary>
public static partial class AuditSanitizer
{
    public static string SanitizeAndCap(string? message, int maxChars = 500)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Unknown error";

        var text = message;

        // 1. Redact Bearer and Basic auth tokens
        text = BearerTokenPattern().Replace(text, "Bearer [REDACTED]");
        text = BasicAuthPattern().Replace(text, "Basic [REDACTED]");

        // 2. Redact key / api_key / token / password / secret assignments
        text = SecretAssignmentPattern().Replace(text, "$1=[REDACTED]");

        // 3. Redact database connection strings
        text = ConnectionStringPattern().Replace(text, "[CONNECTION_STRING_REDACTED]");

        // 4. Redact Windows drive paths, UNC paths, and file URIs
        text = WindowsDrivePathPattern().Replace(text, "[PATH_REDACTED]");
        text = UncPathPattern().Replace(text, "[PATH_REDACTED]");
        text = UriFilePathPattern().Replace(text, "[PATH_REDACTED]");

        // 5. Redact PAN numbers (5 uppercase letters, 4 digits, 1 uppercase letter)
        text = PanPattern().Replace(text, "[PAN_REDACTED]");

        // 6. Redact GSTIN (2 digits, 5 letters, 4 digits, 1 letter, 1 alphanumeric, 'Z', 1 alphanumeric)
        text = GstinPattern().Replace(text, "[GSTIN_REDACTED]");

        // 7. Redact Emails
        text = EmailPattern().Replace(text, "[EMAIL_REDACTED]");

        // 8. Strip stack trace lines starting with "at ..."
        text = StackTracePattern().Replace(text, "");

        // 9. Collapse multi-whitespace and trim
        text = MultiWhitespacePattern().Replace(text, " ").Trim();

        return text.Length <= maxChars ? text : text[..maxChars];
    }

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"\bBasic\s+[A-Za-z0-9+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex BasicAuthPattern();

    [GeneratedRegex(@"(?i)\b(key|api[-_]?key|token|password|secret|pwd)\s*[=:]\s*['""]?[^\s&""';]+['""]?")]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"(?i)\b(?:Server|Data Source|User ID|Initial Catalog)\s*=[^;]+(?:;|$)")]
    private static partial Regex ConnectionStringPattern();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\r\n""':;<>|?*,/\\]+\\)*[^\s\r\n""':;<>|?*,/\\]+")]
    private static partial Regex WindowsDrivePathPattern();

    [GeneratedRegex(@"\\\\[^\r\n""':;<>|?*,/\\]+\\(?:[^\r\n""':;<>|?*,/\\]+\\)*[^\s\r\n""':;<>|?*,/\\]+")]
    private static partial Regex UncPathPattern();

    [GeneratedRegex(@"(?:file:\/\/\/|\/)[a-zA-Z0-9_\-.\/]+\.[a-zA-Z0-9]+")]
    private static partial Regex UriFilePathPattern();

    [GeneratedRegex(@"\b[A-Z]{5}[0-9]{4}[A-Z]\b")]
    private static partial Regex PanPattern();

    [GeneratedRegex(@"\b[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z]{1}[1-9A-Z]{1}Z[0-9A-Z]{1}\b")]
    private static partial Regex GstinPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\s*at\s+.*$", RegexOptions.Multiline)]
    private static partial Regex StackTracePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespacePattern();
}
