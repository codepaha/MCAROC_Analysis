using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCAROC_Analysis.Services.Capture;

namespace MCAROC_Analysis.Tests;

/// <summary>The reference tool as it really answered (#514): replays a recorded scenario (<c>Fixtures/reference-tool/&lt;scenario&gt;</c>, one file per
/// call in the capture format) instead of replies written from our own assumptions, which is how the 2026-10-16 failures went unseen. Calls are
/// matched by action (the signed payload's, else the plain query's); each action's recorded replies are given in order and the last one repeats.
/// A reply recorded with <c>"after": "&lt;action&gt;"</c> is given only once that action has been called (the asset status after a paid unlock).
/// A call with no recording is answered 404 and listed in <see cref="Unanswered"/>, so a test can require that the client asked for nothing the
/// tool was never seen to answer. The company identifier in a recording is not checked: tests use their own identifiers to keep the shared test
/// database apart, and <see cref="RecordedBid"/> lets a test check the scenario's own. Sign-in calls (signing key, who-am-I) are answered here,
/// because their recordings are redacted whole.</summary>
public sealed class FixtureReferenceTool : HttpMessageHandler
{
    private const string KeyHex = "6b65792d666f722d7465737473";
    private readonly Dictionary<string, List<JsonObject>> _replies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonObject> _lastServed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public string Scenario { get; }
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<string> Unanswered { get; } = new();
    public int Count(string action) => Calls.Count(c => c.Equals(action, StringComparison.OrdinalIgnoreCase));

    public FixtureReferenceTool(string scenario)
    {
        Scenario = scenario;
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference-tool", scenario);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"No recorded scenario {scenario} at {dir}.");
        foreach (var file in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var record = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var action = record["action"]?.GetValue<string>() ?? throw new InvalidDataException($"{Path.GetFileName(file)} names no action.");
            if (!_replies.TryGetValue(action, out var list)) _replies[action] = list = [];
            list.Add(record);
        }
    }

    /// <summary>The company identifier (<c>bid</c>) the scenario's recorded calls were made for.</summary>
    public string? RecordedBid => _replies.Values.SelectMany(q => q).Select(r => r["payload"]?["bid"]?.GetValue<string>()).FirstOrDefault(b => b is not null);

    /// <summary>Replaces an action's recorded replies (a test's own variant: a different date, a failure the tool was not recorded giving).</summary>
    public FixtureReferenceTool With(string action, int status, object? body) => WithReplies(action, status, body);

    /// <summary>Replaces an action's recorded replies with several, given in order (pages of a search); the last one repeats.</summary>
    public FixtureReferenceTool WithReplies(string action, int status, params object?[] bodies)
    {
        lock (_sync)
            _replies[action] = bodies.Select(body => new JsonObject
            {
                ["action"] = action, ["status"] = status, ["contentType"] = "application/json",
                ["body"] = body is null ? null : JsonSerializer.SerializeToNode(body), ["source"] = "test override"
            }).ToList();
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("jwt/service.php", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new JsonObject { ["jwtToken"] = KeyHex }.ToJsonString());

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var payload = CaptureRedactor.DecodeSignedPayload(query["qp"]);
        if (payload is null && request.Content is FormUrlEncodedContent form)
            payload = CaptureRedactor.DecodeSignedPayload(System.Web.HttpUtility.ParseQueryString(await form.ReadAsStringAsync(ct))["pp"]);
        var action = payload?["action"]?.GetValue<string>() ?? query["action"] ?? path;
        Calls.Enqueue(action);
        if (action == "getUserDetails")
            return Json(HttpStatusCode.OK, """{"id":265271,"user_name":"tester"}""");

        JsonObject? reply;
        lock (_sync) reply = Next(action);
        if (reply is null)
        {
            Unanswered.Enqueue(action);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no recorded reply for {action} in {Scenario}") };
        }

        var status = (HttpStatusCode)(reply["status"]?.GetValue<int>() ?? 200);
        var contentType = reply["contentType"]?.GetValue<string>() ?? "application/json";
        return reply["body"] switch
        {
            // A document's recording keeps only its type: give the client a body of that type.
            null when contentType == "application/pdf" => Bytes(status, "%PDF-1.4\n%recorded document\n"u8.ToArray(), contentType),
            null => new HttpResponseMessage(status) { Content = new StringContent("") },
            JsonValue v when v.TryGetValue<string>(out var text) => new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, contentType) },
            var body => Json(status, body.ToJsonString()),
        };
    }

    // The first recorded reply now due (no "after", or its action already called); consumed unless it is the last. None due: the last one given repeats.
    private JsonObject? Next(string action)
    {
        if (!_replies.TryGetValue(action, out var list)) return null;
        var due = list.FirstOrDefault(r => r["after"]?.GetValue<string>() is not { } after || Calls.Contains(after, StringComparer.OrdinalIgnoreCase));
        if (due is null) return _lastServed.GetValueOrDefault(action);
        if (list.Count > 1) list.Remove(due);
        return _lastServed[action] = due;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(HttpStatusCode status, byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }
}
