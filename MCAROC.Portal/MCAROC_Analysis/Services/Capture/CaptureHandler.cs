using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCAROC_Analysis.Services.Capture;

public static class CaptureService
{
    public const string ReferenceTool = "reference-tool";
    public const string LitigationApi = "litigation-api";
}

/// <summary>Development only, off unless <c>IntegrationCapture:Directory</c> is set (#514): every live call made by the reference-tool or litigation-API
/// client is written there, redacted, one file per call, so a live E2E run leaves the responses that become test fixtures. Live behaviour our fakes
/// had never seen (the MCA ALERT, a never-probed company's 404, XBRL attachments) was each found mid-run; a capture keeps it. Recorded: the method,
/// path, the signed payload decoded (never the token itself), status, content type and the body (text only; a PDF, workbook or archive is recorded
/// as its type and size and passed through unread). Capturing never changes or fails the call.</summary>
public sealed class CaptureHandler(string service, string directory, ILogger<CaptureHandler> logger) : DelegatingHandler
{
    public const string SectionName = "IntegrationCapture";
    private const long MaxTextBytes = 4 * 1024 * 1024;
    private static long _sequence;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        JsonObject? record = null;
        try { record = await DescribeRequestAsync(request, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Capture could not read a {Service} request", service); }

        var response = await base.SendAsync(request, cancellationToken);
        if (record is null) return response;
        try
        {
            await CaptureResponseAsync(record, response, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Capture could not record a {Service} response", service);
        }
        return response;
    }

    private async Task<JsonObject> DescribeRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var payload = CaptureRedactor.DecodeSignedPayload(query["qp"]);
        JsonNode? body = null;
        if (request.Content is FormUrlEncodedContent form)
        {
            var fields = System.Web.HttpUtility.ParseQueryString(await form.ReadAsStringAsync(ct));
            payload ??= CaptureRedactor.DecodeSignedPayload(fields["pp"]);
        }
        else if (request.Content is { } content && content.Headers.ContentType?.MediaType?.Contains("json") == true)
        {
            try { body = JsonNode.Parse(await content.ReadAsStringAsync(ct)); } catch (JsonException) { }
        }
        // Document downloads name their action in the plain query, not in a signed payload.
        var action = payload?["action"]?.GetValue<string>() ?? query["action"];
        var record = new JsonObject
        {
            ["service"] = service,
            ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
            ["method"] = request.Method.Method,
            ["url"] = $"{uri.Scheme}://{CaptureRedactor.PlaceholderHost(service)}{uri.AbsolutePath}",
            ["action"] = action,
            ["payload"] = CaptureRedactor.RedactJson(payload),
            ["requestBody"] = CaptureRedactor.RedactJson(body, everything: CaptureRedactor.IsAccountCall(uri.AbsolutePath, action)),
        };
        // Plain query parameters (app versions, platform, a download's key and user id); the signed one is the decoded payload above. Redacted by
        // the same field rules as a payload, so a user id sent in the query is removed too.
        var plain = new JsonObject();
        foreach (var key in query.AllKeys.Where(k => k is not null && k != "qp"))
            plain[key!] = query[key] ?? "";
        record["query"] = CaptureRedactor.RedactJson(plain);
        return record;
    }

    /// <summary>A document (any type but JSON or plain text/HTML, XML attachments included), or a reply declared larger than the cap, is recorded by
    /// type and size and left untouched. A text reply is never buffered either: the caller reads it through <see cref="CaptureTeeStream"/>, which hands
    /// on every byte, error and cancellation exactly as received and keeps a copy of up to <see cref="MaxTextBytes"/>; the record is written once the
    /// caller has read to the end (or disposed the reply).</summary>
    private async Task CaptureResponseAsync(JsonObject record, HttpResponseMessage response, CancellationToken ct)
    {
        var original = response.Content;
        var mediaType = original.Headers.ContentType?.MediaType ?? "";
        record["status"] = (int)response.StatusCode;
        record["contentType"] = mediaType;
        record["contentLength"] = original.Headers.ContentLength;
        var isText = mediaType.Contains("json") || mediaType is "text/plain" or "text/html";
        if (!isText || original.Headers.ContentLength > MaxTextBytes)
        {
            record["body"] = null; // its type and size are the fixture's facts; it streams on unread
            Write(record);
            return;
        }

        var account = CaptureRedactor.IsAccountCall(record["url"]!.GetValue<string>(), record["action"]?.GetValue<string>());
        var inner = await original.ReadAsStreamAsync(ct);
        // Nothing below can fail: once the reply's stream has been taken, the caller must get it back whatever happens to the capture.
        var charset = original.Headers.ContentType?.CharSet;
        var isJson = mediaType.Contains("json");
        var tee = new CaptureTeeStream(inner, original, MaxTextBytes, (bytes, complete) => Finish(record, bytes, complete, account, charset, isJson));
        var replacement = new StreamContent(tee);
        foreach (var header in original.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
    }

    private void Finish(JsonObject record, byte[]? bytes, bool complete, bool account, string? charset, bool isJson)
    {
        try
        {
            string? Omit(string why)
            {
                record["body"] = null;
                record["bodyNote"] = why;
                return null;
            }
            var text = bytes is null ? Omit($"larger than {MaxTextBytes / (1024 * 1024)} MB; not kept")
                : !complete ? Omit("the caller did not read it to the end; not kept")
                : Decode(bytes, charset) ?? Omit($"not decodable as {charset ?? "UTF-8"}; not kept");
            if (text is not null)
            {
                // A JSON reply is kept only once it parses, so its fields are redacted by name; text that only looks like JSON could carry them unredacted.
                var parsed = isJson ? TryParse(text) : null;
                if (isJson && parsed is null) Omit("declared JSON but did not parse; not kept");
                else if (parsed is not null) record["body"] = JsonNode.Parse(CaptureRedactor.RedactBody(text, account));
                else record["body"] = CaptureRedactor.RedactBody(text, account);
            }
            Write(record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Capture could not record a {Service} response", service);
        }
    }

    /// <summary>The text as <see cref="HttpContent.ReadAsStringAsync()"/> would read it: a byte-order mark first, else the declared charset, else UTF-8.
    /// Strict: an unknown charset, bytes invalid in it, or a NUL in the result (a wide encoding read as a narrow one) give null, not mangled text.</summary>
    internal static string? Decode(byte[] bytes, string? charset)
    {
        System.Text.Encoding encoding;
        var bom = 0;
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) { encoding = new System.Text.UTF8Encoding(false, true); bom = 3; }
        else if (bytes is [0xFF, 0xFE, ..]) { encoding = new System.Text.UnicodeEncoding(false, false, true); bom = 2; }
        else if (bytes is [0xFE, 0xFF, ..]) { encoding = new System.Text.UnicodeEncoding(true, false, true); bom = 2; }
        else
        {
            try
            {
                var named = string.IsNullOrWhiteSpace(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset.Trim('"', ' '));
                encoding = (System.Text.Encoding)named.Clone();
                encoding.DecoderFallback = System.Text.DecoderFallback.ExceptionFallback;
            }
            catch (ArgumentException) { return null; } // a charset this runtime does not know
        }
        try
        {
            var text = encoding.GetString(bytes, bom, bytes.Length - bom);
            return text.Contains('\0') ? null : text;
        }
        catch (System.Text.DecoderFallbackException) { return null; }
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); } catch (JsonException) { return null; }
    }

    private void Write(JsonObject record)
    {
        Directory.CreateDirectory(directory);
        var seq = Interlocked.Increment(ref _sequence);
        var action = record["action"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(new Uri(record["url"]!.GetValue<string>()).AbsolutePath);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{seq:D5}-{service}-{Safe(action)}.json";
        File.WriteAllText(Path.Combine(directory, name), record.ToJsonString(CaptureRedactor.Json));
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
