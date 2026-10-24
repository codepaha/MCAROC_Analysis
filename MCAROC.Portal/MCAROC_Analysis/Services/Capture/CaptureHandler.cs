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
            await DescribeResponseAsync(record, response, cancellationToken);
            Write(record);
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

    private async Task DescribeResponseAsync(JsonObject record, HttpResponseMessage response, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        record["status"] = (int)response.StatusCode;
        record["contentType"] = mediaType;
        record["contentLength"] = response.Content.Headers.ContentLength;
        // Only JSON and plain-text replies are read; a body of any other type (or of unknown size beyond the cap) is never buffered, so a capture
        // can't consume or truncate a document the client is about to stream to disk.
        var isText = mediaType.Contains("json") || mediaType.StartsWith("text/");
        if (!isText || response.Content.Headers.ContentLength is null or > MaxTextBytes && !mediaType.Contains("json"))
        {
            record["body"] = null; // a document: its type and size are the fixture's facts, and it streams on unread
            return;
        }
        await response.Content.LoadIntoBufferAsync(MaxTextBytes, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var path = record["url"]!.GetValue<string>();
        var account = CaptureRedactor.IsAccountCall(path, record["action"]?.GetValue<string>());
        var redacted = CaptureRedactor.RedactBody(text, account);
        try { record["body"] = JsonNode.Parse(redacted); }
        catch (JsonException) { record["body"] = redacted; }
    }

    private void Write(JsonObject record)
    {
        Directory.CreateDirectory(directory);
        var seq = Interlocked.Increment(ref _sequence);
        var action = record["action"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(new Uri(record["url"]!.GetValue<string>()).AbsolutePath);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{seq:D5}-{service}-{Safe(action)}.json";
        File.WriteAllText(Path.Combine(directory, name), record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
