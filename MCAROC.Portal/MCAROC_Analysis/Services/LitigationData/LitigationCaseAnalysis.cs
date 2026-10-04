using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PropertyParticulars;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>One order as the analysis sees it: its identity, its date and type, and the text that is sent (null when none could be read).</summary>
public sealed record CaseAnalysisOrder(long OrderId, long? DocumentId, string? OrderDate, string? OrderType, string? Text, int TotalChars);

/// <summary>Everything one case's analysis is built from. <see cref="Orders"/> holds the text exactly as it is sent to the model — the
/// quotes it returns are checked against this text, nothing else.</summary>
public sealed record CaseAnalysisInput(
    long CaseId, string CaseJson, string EvidenceJson, string EvidenceHash, IReadOnlyList<CaseAnalysisOrder> Orders, bool TextTruncated)
{
    public bool HasText => Orders.Any(o => !string.IsNullOrWhiteSpace(o.Text));
}

/// <summary>Builds the per-case input of the v2.0 analysis: the case's record fields and every order with its full text, with a head-and-tail
/// cut for an order too long to send and, past a total budget, the oldest orders' text left out (flagged, never silent).</summary>
public static class LitigationCaseAnalysisInputBuilder
{
    public const int MaxOrderChars = 60_000;
    /// <summary>The most order text sent for one case over all its groups; past it the oldest orders' text is left out (flagged).</summary>
    public const int MaxCaseChars = 1_000_000;
    private const int HeadChars = 20_000;
    public const string OmittedMarker = "[... middle section omitted for length ...]";
    private static readonly System.Text.RegularExpressions.Regex PageMarkerLine =
        new(@"^--- Page \d+ \((?:native|OCR)\) ---\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static CaseAnalysisInput Build(
        LitigationCase c, IReadOnlyDictionary<long, LitigationOrderDocument> documentsByOrderId,
        string targetEntity, IReadOnlyCollection<string> targetNames, DateOnly asOf, IReadOnlySet<long>? onlyOrderIds = null,
        IReadOnlySet<long>? withoutTextOrderIds = null)
    {
        var ordered = c.Orders.Where(o => onlyOrderIds is null || onlyOrderIds.Contains(o.LitigationCaseOrderId))
            .OrderByDescending(o => LitigationCaseAges.ParseDate(o.OrderDate) ?? DateOnly.MinValue)
            .ThenByDescending(o => o.LitigationCaseOrderId).ToList();
        var budget = MaxCaseChars;
        var truncated = false;
        var orders = new List<CaseAnalysisOrder>();
        foreach (var o in ordered)
        {
            documentsByOrderId.TryGetValue(o.LitigationCaseOrderId, out var doc);
            var text = string.IsNullOrWhiteSpace(doc?.ExtractedText) ? null : doc!.ExtractedText!;
            var total = text?.Length ?? 0;
            // A group is built from the whole case's plan: an order whose text the whole case left out for length stays without text here,
            // so the text a group sends is exactly the text the case's evidence hash covers.
            if (text is not null && withoutTextOrderIds is not null && withoutTextOrderIds.Contains(o.LitigationCaseOrderId)) { text = null; truncated = true; }
            if (text is not null)
            {
                if (text.Length > MaxOrderChars)
                {
                    var tailStart = text.Length - (MaxOrderChars - HeadChars);
                    // The page marker in force at the start of the tail is usually in the omitted middle: put it back, or the tail's passages
                    // would be given the last page of the head.
                    var tailMarker = PageMarkerLine.Matches(text[..tailStart]).LastOrDefault();
                    text = text[..HeadChars] + "\n" + OmittedMarker + "\n" + (tailMarker is null ? "" : tailMarker.Value.Trim() + "\n") + text[tailStart..];
                    truncated = true;
                }
                if (text.Length > budget) { text = null; truncated = true; } // newest orders were taken first
                else budget -= text.Length;
            }
            orders.Add(new CaseAnalysisOrder(o.LitigationCaseOrderId, doc?.LitigationOrderDocumentId, o.OrderDate, o.OrderType, text, total));
        }

        var status = LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage);
        var obj = new JsonObject
        {
            ["case_key"] = c.LitigationCaseId.ToString(),
            ["cnr"] = c.Cnr, ["case_no"] = c.CaseNumber, ["court"] = c.Court,
            ["court_type"] = CourtType(c), ["party_direction"] = string.IsNullOrWhiteSpace(c.Direction) ? "unknown" : c.Direction!.ToLowerInvariant(),
            ["case_category"] = c.CourtCategory, ["case_status"] = status.ToString().ToUpperInvariant(), ["case_stage"] = c.CaseStage,
            ["case_type"] = c.CaseType, ["act"] = c.Act, ["filing_date"] = c.FilingDate, ["last_hearing_date"] = c.LastHearingDate,
            ["next_hearing_date"] = c.NextHearingDate, ["state"] = c.State, ["district"] = c.District,
            ["petitioners"] = Array(LitigationPartyNames.Parse(c.PetitionersJson)), ["respondents"] = Array(LitigationPartyNames.Parse(c.RespondentsJson)),
            ["petitioner_advocates"] = Array(LitigationPartyNames.Parse(c.PetitionerAdvocatesJson)),
            ["respondent_advocates"] = Array(LitigationPartyNames.Parse(c.RespondentAdvocatesJson)),
            ["target_entity"] = targetEntity, ["target_names"] = Array(targetNames),
            ["text_truncated"] = truncated
        };
        var withText = (JsonObject)obj.DeepClone();
        // Today's date goes to the model but not into the stored evidence: it would change the hash every day and defeat reuse.
        withText["as_of_date"] = asOf.ToString("yyyy-MM-dd");
        withText["orders"] = new JsonArray(orders.Select(o => (JsonNode)new JsonObject
            { ["order_id"] = o.OrderId, ["order_date"] = o.OrderDate, ["order_type"] = o.OrderType, ["order_text"] = o.Text }).ToArray());
        // What is stored with the analysis: the same, with each text replaced by its length and hash — the text itself stays in the order
        // document, so the evidence row stays small and still changes whenever any order's text does.
        var stored = (JsonObject)obj.DeepClone();
        stored["orders"] = new JsonArray(orders.Select(o => (JsonNode)new JsonObject
        {
            ["order_id"] = o.OrderId, ["document_id"] = o.DocumentId, ["order_date"] = o.OrderDate, ["order_type"] = o.OrderType,
            ["text_chars_sent"] = o.Text?.Length ?? 0, ["text_chars_total"] = o.TotalChars, ["text_sha256"] = o.Text is null ? null : Hash(o.Text)
        }).ToArray());
        var evidenceJson = stored.ToJsonString(JsonOpts);
        return new CaseAnalysisInput(c.LitigationCaseId, withText.ToJsonString(JsonOpts), evidenceJson, Hash(evidenceJson), orders, truncated);
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static string CourtType(LitigationCase c) => LitigationBaselineRisk.ForumOf(c.Type, c.Court, c.CourtCategory) switch
    {
        LitigationForum.SupremeCourt => "supreme", LitigationForum.HighCourt => "high", LitigationForum.DistrictCourt => "district",
        LitigationForum.Nclt => "nclt", LitigationForum.Nclat => "nclat", LitigationForum.Drt => "drt", LitigationForum.Drat => "drat",
        LitigationForum.Consumer => "consumer", LitigationForum.Itat => "itat", LitigationForum.Cestat => "cestat", LitigationForum.Rera => "rera",
        _ => "others"
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>What the portfolio synthesis reads of a case's analysis: the conclusions, not the per-order findings, quotes and extracted
/// lists, which would make the portfolio prompt several megabytes for a company with dozens of cases. An analysis in the older v1.0 shape is
/// passed through as it is.</summary>
public static class LitigationCaseAnalysisProjection
{
    private static readonly string[] Kept =
        ["case_key", "risk", "risk_trigger", "risk_category", "baseline_risk", "relevance", "case_nature", "liability_type", "subject", "summary", "key_points",
         "target_party_role", "legal_exposure_direction", "active_restraint", "restraint_type", "lis_pendens", "amount", "analysis_basis", "unknowns"];

    public static string? ForPortfolio(string? analysisJson)
    {
        if (string.IsNullOrWhiteSpace(analysisJson)) return analysisJson;
        try
        {
            if (JsonNode.Parse(analysisJson) is not JsonObject root || root["prompt_version"] is null) return analysisJson;
            var compact = new JsonObject { ["status"] = root["status"]?.DeepClone() };
            foreach (var key in Kept)
                if (root[key] is { } value) compact[key] = value.DeepClone();
            if (root["outcome"] is JsonObject outcome && outcome["final_order_summary"] is { } finalOrder) compact["final_order_summary"] = finalOrder.DeepClone();
            return compact.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException) { return analysisJson; }
    }
}

/// <summary>Plans and merges the analysis of a case too long for one call: its orders, newest first, are cut into groups of at most a given
/// amount of text; each group is analysed on its own and the results are merged by rules, not by another model call.</summary>
public static class LitigationCaseAnalysisGroups
{
    /// <summary>The order ids of each group, newest orders first. One group when the text sent is within <paramref name="splitAbove"/>.
    /// Orders with no text cost nothing and ride with the first group.</summary>
    public static List<HashSet<long>> Plan(CaseAnalysisInput whole, int splitAbove, int groupChars)
    {
        var all = whole.Orders.Select(o => o.OrderId).ToHashSet();
        if (whole.Orders.Sum(o => o.Text?.Length ?? 0) <= splitAbove) return [all];
        var groups = new List<HashSet<long>> { new() };
        var size = 0;
        foreach (var o in whole.Orders) // already newest first
        {
            var len = o.Text?.Length ?? 0;
            if (len > 0 && size > 0 && size + len > groupChars) { groups.Add([]); size = 0; }
            groups[^1].Add(o.OrderId);
            size += len;
        }
        return groups;
    }

    private static readonly string[] Risks = ["R0", "R1", "R2", "R3"];

    /// <summary>Merges validated group analyses (newest group first). The tier is the highest, with its trigger and reason; flags are true if
    /// any group says so; lists and per-order findings and quotes are joined; the summary reads newest group first; the other single
    /// fields come from the newest group that has them.</summary>
    public static string Merge(IReadOnlyList<string> groupJsons)
    {
        var groups = groupJsons.Select(j => (JsonObject)JsonNode.Parse(j)!).ToList();
        if (groups.Count == 1) return groupJsons[0];
        string? First(string key) => groups.Select(g => g[key]?.ToString()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var top = groups.OrderByDescending(g => Array.IndexOf(Risks, g["risk"]?.ToString())).First();
        JsonArray Join(string key)
        {
            var result = new JsonArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in groups)
                if (g[key] is JsonArray a) foreach (var x in a) if (x is not null && seen.Add(x.ToJsonString())) result.Add(x.DeepClone());
            return result;
        }
        bool Any(string key) => groups.Any(g => g[key]?.GetValue<bool>() == true);
        var restraintGroup = groups.FirstOrDefault(g => g["active_restraint"]?.GetValue<bool>() == true);
        var merged = (JsonObject)groups[0].DeepClone();
        merged["analysis_basis"] = groups.Any(g => g["analysis_basis"]?.ToString() == "Order-Confirmed") ? "Order-Confirmed" : "Metadata-Led";
        foreach (var key in new[] { "target_party_role", "legal_exposure_direction", "case_nature", "liability_type", "subject", "amount", "factual_background", "relief_sought", "core_issue" })
            merged[key] = First(key);
        merged["risk"] = top["risk"]?.DeepClone();
        merged["risk_trigger"] = top["risk_trigger"]?.DeepClone();
        merged["risk_reason"] = top["risk_reason"]?.DeepClone();
        merged["risk_category"] = top["risk_category"]?.DeepClone();
        merged["risk_raised"] = Any("risk_raised");
        merged["relevance"] = top["relevance"]?.DeepClone();
        merged["consequence_note"] = top["consequence_note"]?.DeepClone() ?? (First("consequence_note") is { } c ? JsonValue.Create(c) : null);
        merged["summary"] = top["summary"]?.DeepClone();
        merged["key_points"] = new JsonArray(groups.SelectMany(g => (g["key_points"] as JsonArray)?.Select(x => x?.ToString()) ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        foreach (var key in new[] { "judicial_reasoning", "provisions", "precedents", "connected_matters", "order_analysis", "evidence", "unknowns", "evidenceReferences", "validation_notes" })
            merged[key] = Join(key);
        // Whether a restraint or lis pendens is in force NOW is a matter of chronology, not of any group saying so: walk the groups oldest to
        // newest; a group that grants or confirms one sets it, a group whose orders lift or end it clears it, a group that says nothing leaves
        // the state as it was. (An OR over the groups would revive a restraint that a newer order vacated.)
        var restraintNow = false; JsonNode? restraintTypeNow = null; var lisNow = false;
        foreach (var g in Enumerable.Reverse(groups))
        {
            if (g["active_restraint"]?.GetValue<bool>() == true) { restraintNow = true; restraintTypeNow = g["restraint_type"]?.DeepClone(); }
            else if (g["restraint_lifted"]?.GetValue<bool>() == true) { restraintNow = false; restraintTypeNow = null; }
            if (g["lis_pendens"]?.GetValue<bool>() == true) lisNow = true;
            else if (g["lis_pendens_ended"]?.GetValue<bool>() == true) lisNow = false;
        }
        merged["active_restraint"] = restraintNow;
        merged["restraint_type"] = restraintNow ? restraintTypeNow ?? JsonValue.Create("NONE") : JsonValue.Create("NONE");
        merged["lis_pendens"] = lisNow;
        merged["restraint_lifted"] = Any("restraint_lifted");
        merged["lis_pendens_ended"] = Any("lis_pendens_ended");
        merged["outcome"] = groups.Select(g => g["outcome"]).FirstOrDefault(o => o is JsonObject)?.DeepClone();
        merged["split_groups"] = groups.Count;
        ((JsonArray)merged["unknowns"]!).Add($"A long case: its orders were read in {groups.Count} groups, newest first.");
        return merged.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

/// <summary>A quote the portal confirmed in an order's text, with the pages it sits on.</summary>
public sealed record CaseAnalysisEvidence(string Claim, long OrderId, long? DocumentId, string Quote, int Page, int EndPage);

public sealed record CaseAnalysisValidation(bool IsAccepted, string? AnalysisJson, string? RejectReason, IReadOnlyList<string> Notes);

/// <summary>Checks the model's v2.0 answer: the shape and every enum, the tier against the baseline (confirm or raise, never lower), and
/// each quoted claim against the text that was sent. A claim whose quote is not in the named order is dropped with what it supported —
/// a restraint or lis pendens flag without a confirmed quote goes back to false, a raised tier without one goes back to the baseline.</summary>
public static class LitigationCaseAnalysisValidator
{
    private static readonly string[] Risks = ["R0", "R1", "R2", "R3"];
    private static readonly string[] Relevance = ["Needs attention", "Monitor", "Background", "Not material"];
    private static readonly string[] Directions = ["BY_TARGET", "AGAINST_TARGET", "NEUTRAL", "UNKNOWN"];
    private static readonly string[] Claims = ["risk_trigger", "restraint", "lis_pendens", "restraint_lifted", "lis_pendens_ended", "operative_order", "outcome", "target_party"];
    private static readonly string[] RestraintTypes = ["INJUNCTION", "ATTACHMENT", "STATUS_QUO", "POSSESSION_RESTRAINT", "NONE"];
    private static readonly string[] Bases = ["Order-Confirmed", "Metadata-Led"];
    private const int MinQuote = 12, MaxQuote = 500;

    public static CaseAnalysisValidation Validate(string rawJson, CaseAnalysisInput input, LitigationRiskTier baseline, string baselineTrigger)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(PropertyParticularsAi.UnwrapSingleElementArray(rawJson)) as JsonObject; }
        catch (JsonException ex) { return Rejected($"Invalid JSON: {ex.Message}"); }
        if (root is null) return Rejected("The answer is not a JSON object.");

        var notes = new List<string>();
        string? Str(string key) => root[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

        if (Str("case_key") != input.CaseId.ToString()) return Rejected("case_key does not match the case that was sent.");
        var basis = Str("analysis_basis");
        var expectedBasis = input.HasText ? "Order-Confirmed" : "Metadata-Led";
        if (basis is null || !Bases.Contains(basis)) return Rejected("analysis_basis is missing or unsupported.");
        if (basis != expectedBasis) { notes.Add($"analysis_basis corrected from {basis} to {expectedBasis}."); basis = expectedBasis; }
        var summary = Str("summary");
        if (summary is null) return Rejected("Missing summary.");
        var risk = Str("risk");
        if (risk is null || !Risks.Contains(risk)) return Rejected("risk is missing or not R0-R3.");
        if (Str("risk_trigger") is null || Str("risk_reason") is null) return Rejected("risk_trigger or risk_reason is missing.");

        // Quotes: each is kept only when found in the order it names.
        var confirmed = new List<CaseAnalysisEvidence>();
        var indexes = new Dictionary<long, ChargeInstrumentAi.PageIndex>();
        foreach (var o in input.Orders.Where(o => !string.IsNullOrWhiteSpace(o.Text))) indexes[o.OrderId] = new ChargeInstrumentAi.PageIndex(o.Text!);
        var documents = input.Orders.ToDictionary(o => o.OrderId, o => o.DocumentId);
        if (root["evidence"] is JsonArray ev)
        {
            for (var i = 0; i < ev.Count; i++)
            {
                if (ev[i] is not JsonObject e) continue;
                var claim = (e["claim"] as JsonValue)?.TryGetValue<string>(out var cl) == true ? cl : null;
                var quote = ChargeInstrumentAi.Normalise((e["quote"] as JsonValue)?.TryGetValue<string>(out var q) == true ? q : "");
                var orderId = (e["order_id"] as JsonValue)?.TryGetValue<long>(out var oid) == true ? oid : -1;
                if (claim is null || !Claims.Contains(claim)) { notes.Add($"evidence[{i}]: unsupported claim - dropped."); continue; }
                if (quote.Length < MinQuote || quote.Length > MaxQuote) { notes.Add($"evidence[{i}] ({claim}): quote length out of range - dropped."); continue; }
                if (!indexes.TryGetValue(orderId, out var index)) { notes.Add($"evidence[{i}] ({claim}): not an order with text in this case - dropped."); continue; }
                var at = ChargeInstrumentAi.FindQuote(index.Text, quote);
                var end = at + quote.Length;
                if (at < 0 && FindIgnoringSpacing(index.Text, quote) is { } spaced)
                {
                    // Glued OCR text runs words together, so a cut inside a word is allowed here; a cut inside a number or identifier is not.
                    if (!CutsANumber(index.Text, spaced.Start, spaced.End)) (at, end) = spaced;
                }
                if (at < 0) { notes.Add($"evidence[{i}] ({claim}): not a verbatim quote of order {orderId} - dropped."); continue; }
                confirmed.Add(new CaseAnalysisEvidence(claim, orderId, documents[orderId], index.Text[at..end], index.PageAt(at), index.PageAt(end - 1)));
            }
        }
        bool Has(string claim) => confirmed.Any(c => c.Claim == claim);

        // Tier: confirm or raise, never lower; a raise stands only on a confirmed quote.
        var reported = risk;
        var baselineName = baseline.ToString();
        if (Array.IndexOf(Risks, risk) < (int)baseline) { notes.Add($"risk {risk} is below the record-based baseline {baselineName} - baseline kept."); risk = baselineName; }
        else if (Array.IndexOf(Risks, risk) > (int)baseline && !Has("risk_trigger"))
        {
            notes.Add($"risk raised from {baselineName} to {risk} without a confirmed quote - baseline kept.");
            risk = baselineName;
        }
        var raised = Array.IndexOf(Risks, risk) > (int)baseline;

        var restraint = root["active_restraint"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
        if (restraint && !Has("restraint")) { restraint = false; notes.Add("active_restraint set back to false: no confirmed quote."); }
        var lis = root["lis_pendens"] is JsonValue lv && lv.TryGetValue<bool>(out var lb) && lb;
        if (lis && !Has("lis_pendens")) { lis = false; notes.Add("lis_pendens set back to false: no confirmed quote."); }
        // Whether these orders END a restraint or lis pendens granted earlier: only meaningful across the groups of a long case, where it is
        // what lets a later group's order cancel an earlier group's grant. Like the flags themselves, it stands only on a confirmed quote.
        var restraintLifted = root["restraint_lifted"] is JsonValue rl && rl.TryGetValue<bool>(out var rlb) && rlb;
        if (restraintLifted && !Has("restraint_lifted")) { restraintLifted = false; notes.Add("restraint_lifted set back to false: no confirmed quote."); }
        var lisEnded = root["lis_pendens_ended"] is JsonValue le && le.TryGetValue<bool>(out var leb) && leb;
        if (lisEnded && !Has("lis_pendens_ended")) { lisEnded = false; notes.Add("lis_pendens_ended set back to false: no confirmed quote."); }
        var restraintType = Str("restraint_type");
        if (!restraint) restraintType = "NONE";
        else if (restraintType is null || !RestraintTypes.Contains(restraintType)) restraintType = "NONE";

        var direction = Str("legal_exposure_direction");
        if (direction is null || !Directions.Contains(direction)) direction = "UNKNOWN";
        var relevance = Str("relevance");
        if (relevance is null || !Relevance.Contains(relevance)) { relevance = risk is "R3" or "R2" ? "Needs attention" : "Monitor"; notes.Add("relevance missing or unsupported - derived from the tier."); }
        if (risk is "R3" or "R2") relevance = "Needs attention";

        // Orders: every entry must be an order of this case.
        var orderAnalysis = new JsonArray();
        if (root["order_analysis"] is JsonArray oa)
            foreach (var item in oa.OfType<JsonObject>())
            {
                var id = (item["order_id"] as JsonValue)?.TryGetValue<long>(out var oid) == true ? oid : -1;
                if (!documents.ContainsKey(id)) { notes.Add($"order_analysis entry for unknown order {id} dropped."); continue; }
                var copy = (JsonObject)item.DeepClone();
                copy["document_id"] = documents[id];
                orderAnalysis.Add(copy);
            }

        // Figures: damaged scans make a model "repair" numbers it cannot read. An amount that is not printed in any order is dropped; any other
        // large figure in the prose is kept but flagged, so the reader knows it is not confirmed by the printed text.
        var amount = Str("amount");
        var unknownsOut = Strings(root["unknowns"]);
        var printed = PrintedNumbers(input);
        if (amount is not null && !FiguresPrinted(amount, printed, out _, everyNumber: true))
        {
            notes.Add($"amount '{amount}' is not printed in any order text - dropped.");
            unknownsOut.Add($"An amount of '{amount}' was reported but could not be matched to the printed order text, so it is not shown.");
            amount = null;
        }
        var prose = string.Join(" ", new[] { summary, Str("risk_reason"), Str("consequence_note"), (root["outcome"] as JsonObject)?["final_order_summary"]?.ToString() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (input.HasText && !FiguresPrinted(prose, printed, out var unmatched))
            unknownsOut.Add($"The figure(s) {string.Join(", ", unmatched)} in this analysis are not confirmed by the printed order text (the scan may be damaged); check the order.");

        var outJson = new JsonObject
        {
            ["status"] = "Completed", ["prompt_version"] = LitigationCaseAnalysisPrompt.Version,
            ["case_key"] = input.CaseId.ToString(), ["analysis_basis"] = basis,
            ["target_party_role"] = Str("target_party_role") ?? "Unknown", ["legal_exposure_direction"] = direction,
            ["risk"] = risk, ["risk_trigger"] = raised ? Str("risk_trigger") : (reported == risk ? Str("risk_trigger") : baselineTrigger),
            ["risk_reason"] = Str("risk_reason"), ["risk_category"] = risk is "R3" or "R2" ? Str("risk_category") : null,
            ["baseline_risk"] = baselineName, ["baseline_trigger"] = baselineTrigger, ["risk_raised"] = raised,
            ["relevance"] = relevance, ["case_nature"] = Str("case_nature") ?? "Unknown", ["liability_type"] = Str("liability_type") ?? "Unknown",
            ["summary"] = summary, ["key_points"] = Bullets(root["key_points"]), ["subject"] = Str("subject"), ["amount"] = amount,
            ["factual_background"] = Str("factual_background"), ["relief_sought"] = Str("relief_sought"), ["core_issue"] = Str("core_issue"),
            ["judicial_reasoning"] = Strings(root["judicial_reasoning"]), ["provisions"] = Strings(root["provisions"]),
            ["precedents"] = Strings(root["precedents"]), ["connected_matters"] = Strings(root["connected_matters"]),
            ["active_restraint"] = restraint, ["restraint_type"] = restraintType, ["lis_pendens"] = lis,
            ["restraint_lifted"] = restraintLifted, ["lis_pendens_ended"] = lisEnded,
            ["order_analysis"] = orderAnalysis, ["outcome"] = root["outcome"]?.DeepClone(), ["consequence_note"] = Str("consequence_note"),
            ["evidence"] = new JsonArray(confirmed.Select(c => (JsonNode)new JsonObject
            {
                ["claim"] = c.Claim, ["order_id"] = c.OrderId, ["document_id"] = c.DocumentId, ["quote"] = c.Quote, ["page"] = c.Page, ["end_page"] = c.EndPage
            }).ToArray()),
            ["unknowns"] = unknownsOut,
            ["validation_notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray())
        };
        if (input.TextTruncated && outJson["unknowns"] is JsonArray u && !u.Any(x => x?.ToString().Contains("omitted", StringComparison.OrdinalIgnoreCase) == true))
            u.Add("Some order text was left out for length.");
        // Compatibility with readers of the v1.0 shape (status, summary, unknowns, evidenceReferences).
        outJson["evidenceReferences"] = new JsonArray(confirmed.Select(c => (JsonNode)new JsonObject
        {
            ["litigationCaseOrderId"] = c.OrderId, ["litigationOrderDocumentId"] = c.DocumentId ?? 0, ["pageNumber"] = c.Page, ["chunkIndex"] = 0
        }).ToArray());
        return new CaseAnalysisValidation(true, outJson.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), null, notes);
    }

    /// <summary>Court-order PDFs often have spaces missing or doubled in their extracted text ("Debts&amp; Bankruptcy", "appointedby", "OCTOBE R"),
    /// and a model reading them tends to write the words with normal spacing. A quote that differs from the text only in spacing is the same
    /// passage: it is found by comparing without any whitespace, and the stored quote is the text exactly as it is in the order.</summary>
    internal static (int Start, int End)? FindIgnoringSpacing(string text, string quote)
    {
        var squashedQuote = new string(quote.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (squashedQuote.Length < MinQuote - 2) return null;
        var map = new List<int>(text.Length);
        var squashed = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i])) { squashed.Append(text[i]); map.Add(i); }
        var at = squashed.ToString().IndexOf(squashedQuote, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : (map[at], map[at + squashedQuote.Length - 1] + 1);
    }

    /// <summary>The numbers printed in the text sent for a case, each as digits only (thousands commas dropped, a decimal part kept), one set
    /// over all orders. A number is a whole token: "12345" is not found in "1234500" or "12345.50".</summary>
    private static HashSet<string> PrintedNumbers(CaseAnalysisInput input)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in input.Orders.Where(o => o.Text is not null))
            foreach (System.Text.RegularExpressions.Match m in NumberToken.Matches(order.Text!)) set.Add(m.Value.Replace(",", ""));
        return set;
    }

    private static readonly System.Text.RegularExpressions.Regex NumberToken = new(@"\d(?:[\d,]*\d)?(?:\.\d+)?");

    /// <summary>True when every figure of four or more digits in <paramref name="prose"/> is one of the printed numbers; the ones that are
    /// not are returned. Commas and their grouping do not matter ("5,251,19,676.60" and "525119676.60" are the same number).</summary>
    private static bool FiguresPrinted(string prose, HashSet<string> printed, out List<string> unmatched, bool everyNumber = false)
    {
        unmatched = [];
        foreach (System.Text.RegularExpressions.Match m in NumberToken.Matches(prose))
        {
            var figure = m.Value.Replace(",", "");
            var digits = figure.Count(char.IsDigit);
            if (!everyNumber)
            {
                if (digits < 4) continue; // section numbers and counts are not amounts
                if (digits == 4 && figure.All(char.IsDigit) && int.Parse(figure) is >= 1900 and <= 2100) continue; // a year
            }
            if (!printed.Contains(figure)) unmatched.Add(m.Value);
        }
        return unmatched.Count == 0;
    }

    /// <summary>True when the passage begins or ends in the middle of a number: "Survey No. 12" out of "12/3", "305" out of "A305" or "3050".</summary>
    private static bool CutsANumber(string text, int start, int end)
    {
        if (ChargeInstrumentAi.SplitsIdentifierAcrossSpace(text, start, end)) return true; // "Survey No. 12" out of "12 / 3", "305" out of "A - 305"
        static bool Joiner(char c) => c is '/' or '-' or '.';
        if (end < text.Length && char.IsDigit(text[end - 1]))
        {
            if (char.IsLetterOrDigit(text[end])) return true;
            if (Joiner(text[end]) && end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])) return true;
        }
        if (start > 0 && char.IsDigit(text[start]))
        {
            if (char.IsLetterOrDigit(text[start - 1])) return true;
            if (Joiner(text[start - 1]) && start >= 2 && char.IsLetterOrDigit(text[start - 2])) return true;
        }
        return false;
    }

    /// <summary>The headline bullets: trimmed, no blanks or repeats, at most six, each cut to a sane length (the prompt asks for 25 words).</summary>
    private static JsonArray Bullets(JsonNode? node)
    {
        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonArray a)
            foreach (var x in a)
            {
                if (x is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) continue;
                s = s.Trim().TrimStart('-', '*', '\u2022', ' ');
                if (s.Length > 300) s = s[..297].TrimEnd() + "...";
                if (s.Length > 0 && seen.Add(s) && result.Count < 6) result.Add(s);
            }
        return result;
    }

    private static JsonArray Strings(JsonNode? node)
    {
        var result = new JsonArray();
        if (node is JsonArray a)
            foreach (var x in a)
                if (x is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
        return result;
    }

    private static CaseAnalysisValidation Rejected(string reason) => new(false, null, reason, []);
}
