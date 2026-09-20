using System.Text.Json;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// Flattens BPR's nested court-wise JSON report into source-faithful case and order records.
/// It deliberately preserves provider strings and JSON for parties rather than applying matching or
/// presentation transformations at import time.
/// </summary>
public static class BprLitigationReportParser
{
    public static BprLitigationReport Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("BPR report root must be a JSON object.");

        var root = document.RootElement;
        var request = root.TryGetProperty("request_details", out var requestDetails)
            ? ParseRequest(requestDetails)
            : new BprLitigationRequest(null, null, []);
        var cases = new List<BprLitigationCase>();
        Visit(root, [], cases);
        return new BprLitigationReport(request, cases);
    }

    private static void Visit(JsonElement node, IReadOnlyList<string> path, List<BprLitigationCase> cases)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) Visit(item, path, cases);
                return;
            case JsonValueKind.Object:
                if (IsCase(node))
                {
                    cases.Add(ParseCase(node, path));
                    return;
                }

                foreach (var property in node.EnumerateObject())
                {
                    if (property.NameEquals("request_details")) continue;
                    Visit(property.Value, [.. path, property.Name], cases);
                }
                return;
        }
    }

    private static bool IsCase(JsonElement item) =>
        item.TryGetProperty("case_no", out _) || item.TryGetProperty("cnr_number", out _);

    private static BprLitigationCase ParseCase(JsonElement item, IReadOnlyList<string> path) => new(
        ProviderCaseId: Value(item, "_id"),
        CspId: Value(item, "csp_id"),
        CnrNumber: Value(item, "cnr_number"),
        CourtCategory: path.FirstOrDefault(IsCourtCategory),
        Direction: path.FirstOrDefault(value => value is "by" or "against"),
        CaseClassification: Classification(path),
        Type: Value(item, "type"),
        Court: Value(item, "court"),
        Bench: Value(item, "bench"),
        CaseNumber: Value(item, "case_no"),
        CaseType: Value(item, "case_type"),
        CaseYear: Value(item, "case_year"),
        CaseStage: Value(item, "case_stage"),
        CaseStatus: Value(item, "case_status"),
        Act: Value(item, "act"),
        FilingDate: Value(item, "filing_date"),
        LastHearingDate: Value(item, "last_hearing_date"),
        NextHearingDate: Value(item, "next_hearing_date"),
        DecisionDate: Value(item, "decision_date"),
        State: Value(item, "state"),
        District: Value(item, "district"),
        PetitionersJson: RawValue(item, "petitioners"),
        RespondentsJson: RawValue(item, "respondents"),
        PetitionerAdvocatesJson: RawValue(item, "petitioner_advocates"),
        RespondentAdvocatesJson: RawValue(item, "respondent_advocates"),
        Orders: ParseOrders(item));

    private static BprLitigationRequest ParseRequest(JsonElement request) => new(
        JobId: Value(request, "job_id"),
        ReportDate: Value(request, "report_date"),
        Keywords: request.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Array
            ? keywords.EnumerateArray().Select(value => Value(value) ?? string.Empty).Where(value => value.Length > 0).ToList()
            : []);

    private static IReadOnlyList<BprLitigationOrder> ParseOrders(JsonElement item)
    {
        if (!item.TryGetProperty("orders", out var orders) || orders.ValueKind != JsonValueKind.Array) return [];
        return orders.EnumerateArray()
            .Where(order => order.ValueKind == JsonValueKind.Object)
            .Select(order => new BprLitigationOrder(Value(order, "pdf_url"), Value(order, "order_date"), Value(order, "order_type")))
            .ToList();
    }

    private static string? Classification(IReadOnlyList<string> path)
    {
        var direction = path.Select((value, index) => new { value, index }).FirstOrDefault(item => item.value is "by" or "against");
        return direction is not null && direction.index + 1 < path.Count ? path[direction.index + 1] : null;
    }

    private static bool IsCourtCategory(string value) => value is "supreme_court" or "high_court" or "district_court"
        or "high_risk_court" or "tribunal_cases" or "defaulter_cases";

    private static string? Value(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) ? Value(value) : null;

    private static string? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null
    };

    private static string? RawValue(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.GetRawText()
            : null;
}

public sealed record BprLitigationReport(BprLitigationRequest Request, IReadOnlyList<BprLitigationCase> Cases);

public sealed record BprLitigationRequest(string? JobId, string? ReportDate, IReadOnlyList<string> Keywords);

public sealed record BprLitigationCase(
    string? ProviderCaseId, string? CspId, string? CnrNumber, string? CourtCategory, string? Direction,
    string? CaseClassification, string? Type, string? Court, string? Bench, string? CaseNumber, string? CaseType,
    string? CaseYear, string? CaseStage, string? CaseStatus, string? Act, string? FilingDate, string? LastHearingDate,
    string? NextHearingDate, string? DecisionDate, string? State, string? District, string? PetitionersJson,
    string? RespondentsJson, string? PetitionerAdvocatesJson, string? RespondentAdvocatesJson,
    IReadOnlyList<BprLitigationOrder> Orders);

public sealed record BprLitigationOrder(string? PdfUrl, string? OrderDate, string? OrderType);
