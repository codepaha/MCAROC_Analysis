using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.Capture;

/// <summary>What is removed from a captured integration call before it is written (#514): anything that identifies our account or signs us in.
/// Company data (names, identifiers, filings, cases) is public record and kept: that is what a fixture is for.</summary>
public static partial class CaptureRedactor
{
    public const string Redacted = "<redacted>";

    /// <summary>Hosts a committed fixture may name: the placeholders written in place of the real services, and our own order-document bucket.</summary>
    public static readonly IReadOnlyList<string> AllowedHosts = ["reference.test", "litigation.test", "amazonaws.com", "w3.org", "xbrl.org"];

    // Sign-in and account fields, wherever they appear: login (u, p), the litigation sign-in (id, secret_key), user and team identities.
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "u", "p", "password", "pwd", "secret_key", "secretKey", "jwtToken", "jwt", "token", "access_token", "authorization", "cookie",
        "userId", "user_id", "userName", "user_name", "username", "email", "mobile", "phone", "teamId", "team_id", "mcc",
    };

    /// <summary>Calls whose whole reply is about our account (who is signed in, the sign-in itself): every value is replaced, the shape kept.</summary>
    private static readonly string[] AccountActions = ["getUserDetails", "login", "sec/authenticate", "userDetailsService", "jwt/service"];

    public static bool IsAccountCall(string path, string? action) =>
        AccountActions.Any(a => (action ?? "").Equals(a, StringComparison.OrdinalIgnoreCase) || path.Contains(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>A JSON value with every secret field replaced; with <paramref name="everything"/>, every scalar value.</summary>
    public static JsonNode? RedactJson(JsonNode? node, bool everything = false)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                    obj[key] = SecretKeys.Contains(key) && value is not (JsonObject or JsonArray) ? Redacted : RedactJson(value?.DeepClone(), everything);
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) arr[i] = RedactJson(arr[i]?.DeepClone(), everything);
                return arr;
            case JsonValue v when everything:
                return JsonValue.Create(Redacted);
            case JsonValue v when v.TryGetValue<string>(out var text):
                return JsonValue.Create(RedactText(text));
            case JsonValue v:
                return v;
            default:
                return node;
        }
    }

    /// <summary>A response body as text: JSON redacted field by field, anything else by pattern (tokens, cookies, e-mail addresses, hosts).</summary>
    public static string RedactBody(string body, bool accountCall)
    {
        if (accountCall && Jwt().IsMatch(body.Trim()) && !body.TrimStart().StartsWith('{')) return Redacted; // a raw token reply
        try
        {
            var node = JsonNode.Parse(body);
            return RedactJson(node, accountCall)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? body;
        }
        catch (JsonException)
        {
            return accountCall ? Redacted : RedactText(body);
        }
    }

    /// <summary>Patterns that must never reach a fixture: a signed token, a session cookie, an e-mail address, a host other than <see cref="AllowedHosts"/>.</summary>
    public static string RedactText(string text)
    {
        text = Jwt().Replace(text, Redacted);
        text = SessionCookie().Replace(text, "$1=" + Redacted);
        text = Email().Replace(text, Redacted);
        text = Url().Replace(text, m => IsAllowedHost(m.Groups["host"].Value) ? m.Value : m.Value.Replace(m.Groups["host"].Value, "redacted.test"));
        return text;
    }

    /// <summary>A real URL's host, as written in a fixture: the reference tool and the litigation service become placeholders.</summary>
    public static string PlaceholderHost(string service) => service == CaptureService.LitigationApi ? "litigation.test" : "reference.test";

    /// <summary>Everything in <paramref name="text"/> that <see cref="RedactText"/> would have removed: the check run over every committed fixture.</summary>
    public static IReadOnlyList<string> FindLeaks(string text)
    {
        var leaks = new List<string>();
        leaks.AddRange(Jwt().Matches(text).Select(m => "token " + m.Value[..Math.Min(12, m.Value.Length)] + "…"));
        leaks.AddRange(SessionCookie().Matches(text).Where(m => m.Groups[2].Value != Redacted).Select(m => "cookie " + m.Groups[1].Value));
        leaks.AddRange(Email().Matches(text).Select(m => "e-mail " + m.Value));
        leaks.AddRange(Url().Matches(text).Where(m => !IsAllowedHost(m.Groups["host"].Value) && m.Groups["host"].Value != "redacted.test")
            .Select(m => "host " + m.Groups["host"].Value));
        leaks.AddRange(IpAddress().Matches(text).Select(m => "address " + m.Value));
        return leaks;
    }

    private static bool IsAllowedHost(string host) =>
        AllowedHosts.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(PHPSESSID|session|sid)=([^;\s""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SessionCookie();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"https?://(?<host>[A-Za-z0-9.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IpAddress();

    /// <summary>The payload of a signed request parameter (<c>qp</c>/<c>pp</c>): the middle part of the token, decoded. Null when it is not one.</summary>
    public static JsonNode? DecodeSignedPayload(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - b64.Length % 4) % 4);
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
