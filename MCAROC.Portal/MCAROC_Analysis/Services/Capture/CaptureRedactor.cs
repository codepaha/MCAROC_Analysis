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

    /// <summary>What a numeric account field (a team or user id) becomes: still a number, so a replayed reply parses as the client expects (an unlock
    /// needs a team to charge), and plainly not a real id.</summary>
    public const long RedactedNumber = 0;

    /// <summary>Indented, and without escaping "&lt;" or "&amp;": a fixture is read by people, and an escaped marker (backslash-u003C) would hide it from the leak check.</summary>
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Hosts a committed fixture may name: the placeholders written in place of the real services, and our own order-document bucket.</summary>
    public static readonly IReadOnlyList<string> AllowedHosts = ["reference.test", "litigation.test", "amazonaws.com", "w3.org", "xbrl.org"];

    // Sign-in and account fields, wherever they appear: login (u, p), the litigation sign-in (id, secret_key), user and team identities.
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "u", "p", "password", "pwd", "secret_key", "secretKey", "jwtToken", "jwt", "token", "access_token", "authorization", "cookie",
        "userId", "user_id", "userName", "user_name", "username", "email", "mobile", "phone", "teamId", "team_id", "mcc",
        // Who in our account did something, and our other teams (getAssetTeams), found reading the 2026-09-22 traces.
        "created_by", "createdBy", "added_by", "addedBy", "updated_by", "updatedBy", "srcTeamId", "src_team_id", "owner_id", "ownerId",
        // A presigned document link's access material, when it appears as its own field.
        "X-Amz-Credential", "X-Amz-Security-Token", "X-Amz-Signature", "X-Goog-Credential", "X-Goog-Signature", "AWSAccessKeyId", "Signature",
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
                    obj[key] = !SecretKeys.Contains(key) || value is JsonObject or JsonArray ? RedactJson(value?.DeepClone(), everything)
                        : value is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? JsonValue.Create(RedactedNumber)
                        : value is null ? null
                        : Redacted;
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
            return RedactJson(node, accountCall)?.ToJsonString(Json) ?? body;
        }
        catch (JsonException)
        {
            return accountCall ? Redacted : RedactText(body);
        }
    }

    /// <summary>Patterns that must never reach a fixture: a signed token, a session cookie, an e-mail address, a host other than <see cref="AllowedHosts"/>.</summary>
    public static string RedactText(string text)
    {
        // A signed link's credential, session token and signature are reusable access, not company facts: removed on every host, allowed or not.
        text = SignedUrlParameter().Replace(text, m => m.Groups["name"].Value + m.Groups["eq"].Value + Redacted);
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
        leaks.AddRange(SignedUrlParameter().Matches(text).Where(m => m.Groups["value"].Value is not (Redacted or "%3Credacted%3E"))
            .Select(m => "signed link " + m.Groups["name"].Value));
        // A wide encoding read as a narrow one leaves NULs between letters, and field names the redactor never recognised: not a fixture to trust.
        if (text.Contains('\0') || text.Contains("\\u0000", StringComparison.OrdinalIgnoreCase)) leaks.Add("encoding artifact (NUL characters)");
        return leaks;
    }

    private static bool IsAllowedHost(string host) =>
        AllowedHosts.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));

    /// <summary>A presigned link's access parameters, raw or URL-encoded (<c>X-Amz-Signature=</c>, <c>X-Amz-Signature%3D</c>), in a URL, a query
    /// string or JSON text.</summary>
    [GeneratedRegex(@"(?<name>X-Amz-(?:Credential|Security-Token|Signature)|X-Goog-(?:Credential|Signature)|AWSAccessKeyId|(?<=[?&]|%26|%3F)Signature)(?<eq>=|%3D)(?<value>(?:(?!%26)[^&\s""'#<\\])+)", RegexOptions.IgnoreCase)]
    private static partial Regex SignedUrlParameter();

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
