// Turns a recorded reference-tool session into a committed fixture scenario (#514): the calls our client makes, in order, one file per call in the
// capture format (Tests/Fixtures/reference-tool/README.md), redacted by CaptureRedactor exactly as capture mode does. Writes nothing if any file
// would still hold a token, cookie, e-mail address, real host or IP address.
//
// Reads any of:
//   - a capture-mode directory (IntegrationCapture:Directory): the selected calls are redacted again by today's rules and re-checked;
//   - a browser trace: a JSON list of {url, method, status, action, req_payload, response};
//   - a named-call trace: a JSON object of {name: {url, method, payload, status_code, headers, body}}.
//
//   dotnet run --project Tools/FixturePromote -- --in=<file|dir> --out=<Tests/Fixtures/reference-tool/<scenario>> --source="<where it came from>"
//       [--actions=getAssetTeams,getDataStatus] [--append]

using System.Text.Json;
using System.Text.Json.Nodes;
using MCAROC_Analysis.Services.Capture;

var input = Arg("--in=") ?? throw Usage();
var output = Arg("--out=") ?? throw Usage();
var source = Arg("--source=") ?? throw Usage();
var append = args.Contains("--append");
var actions = (Arg("--actions=")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? ClientActions)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

var records = Directory.Exists(input) ? FromCaptureDirectory(input) : FromTrace(JsonNode.Parse(File.ReadAllText(input))!);
// Every record is redacted again by today's rules, whatever its input: a capture made before a field joined the list still carries it, and the
// leak check below can't see a numeric id.
records = records.Where(r => r["action"]?.GetValue<string>() is { } a && actions.Contains(a)).Select(CaptureRedactor.Reredact).ToList();
if (records.Count == 0)
{
    Console.Error.WriteLine("No calls matched; nothing written.");
    return 1;
}

var files = new List<(string Name, string Text)>();
var start = append && Directory.Exists(output) ? Directory.GetFiles(output, "*.json").Length : 0;
for (var i = 0; i < records.Count; i++)
{
    var r = records[i];
    r["source"] ??= source;
    files.Add(($"{start + i + 1:D2}-{r["action"]!.GetValue<string>()}.json", r.ToJsonString(CaptureRedactor.Json) + "\n"));
}

var leaks = files.SelectMany(f => CaptureRedactor.FindLeaks(f.Text).Select(l => $"{f.Name}: {l}")).ToList();
if (leaks.Count > 0)
{
    Console.Error.WriteLine("Not written; these would leak:");
    foreach (var l in leaks) Console.Error.WriteLine("  " + l);
    return 3;
}

Directory.CreateDirectory(output);
foreach (var (name, text) in files) File.WriteAllText(Path.Combine(output, name), text);
Console.WriteLine($"{files.Count} call(s) written to {output}:");
foreach (var (name, _) in files) Console.WriteLine("  " + name);
return 0;

static List<JsonObject> FromCaptureDirectory(string dir) =>
    Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(f => JsonNode.Parse(File.ReadAllText(f))!.AsObject()).ToList();

static List<JsonObject> FromTrace(JsonNode trace)
{
    var calls = trace switch
    {
        JsonArray list => list.OfType<JsonObject>().Select(c => (
            Url: c["url"]?.GetValue<string>(), Method: c["method"]?.GetValue<string>() ?? "GET", Status: c["status"]?.GetValue<int>() ?? 0,
            Action: c["action"]?.GetValue<string>(), Payload: c["req_payload"], ContentType: (string?)null, Body: c["response"])),
        JsonObject named => named.Select(kv => kv.Value).OfType<JsonObject>().Where(c => c["url"] is not null).Select(c => (
            Url: c["url"]?.GetValue<string>(), Method: c["method"]?.GetValue<string>() ?? "GET", Status: c["status_code"]?.GetValue<int>() ?? 0,
            Action: c["payload"]?["action"]?.GetValue<string>(), Payload: c["payload"], ContentType: c["headers"]?["Content-Type"]?.GetValue<string>(),
            Body: c["body"])),
        _ => throw new InvalidOperationException("Unrecognised trace: expected a list of calls or an object of named calls.")
    };

    var records = new List<JsonObject>();
    foreach (var c in calls)
    {
        if (c.Url is null || !Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) || !uri.AbsolutePath.StartsWith("/server/", StringComparison.Ordinal)) continue;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var action = c.Action ?? c.Payload?["action"]?.GetValue<string>() ?? query["action"];
        var account = CaptureRedactor.IsAccountCall(uri.AbsolutePath, action);
        var plain = new JsonObject();
        foreach (var key in query.AllKeys.Where(k => k is not null && k != "qp")) plain[key!] = query[key] ?? "";

        JsonNode? body = c.Body switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var text) => TryParse(CaptureRedactor.RedactBody(text, account)) ?? JsonValue.Create(CaptureRedactor.RedactBody(text, account)),
            var node => CaptureRedactor.RedactJson(node.DeepClone(), everything: account)
        };
        records.Add(new JsonObject
        {
            ["service"] = CaptureService.ReferenceTool,
            ["capturedUtc"] = null,
            ["method"] = c.Method,
            ["url"] = $"https://{CaptureRedactor.PlaceholderHost(CaptureService.ReferenceTool)}{uri.AbsolutePath}",
            ["action"] = action,
            ["payload"] = CaptureRedactor.RedactJson(c.Payload?.DeepClone(), everything: false),
            ["requestBody"] = null,
            ["query"] = CaptureRedactor.RedactJson(plain),
            ["status"] = c.Status,
            ["contentType"] = c.ContentType?.Split(';')[0].Trim() ?? (body is JsonObject or JsonArray ? "application/json" : "text/html"),
            ["contentLength"] = null,
            ["body"] = body,
        });
    }
    return records;
}

static JsonNode? TryParse(string text)
{
    try { return JsonNode.Parse(text); } catch (JsonException) { return null; }
}

string? Arg(string prefix) => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

static Exception Usage() => new ArgumentException(
    "Usage: --in=<trace file | capture dir> --out=<scenario dir> --source=\"<where it came from>\" [--actions=a,b] [--append]");

// The calls ReferenceToolClient makes. A browser trace also holds the tool's own UI calls (billing, tooltips, history), which no fixture needs.
public partial class Program
{
    internal static readonly string[] ClientActions =
    [
        "login", "getUserDetails", "getNameHints", "getCompanyPreview", "getAssetTeams", "getUpgradeStatusForCompanies", "getUpgradeStatusForLLPs",
        "getUpgradeStatusForUnlockingAsset", "addAsset", "requestProbeData", "requestProbeDataUpdate", "getDataEntryRequestStatus", "getStatus",
        "getDataStatus", "referenceDocs", "downloadPdf", "publishProbedData",
    ];
}
