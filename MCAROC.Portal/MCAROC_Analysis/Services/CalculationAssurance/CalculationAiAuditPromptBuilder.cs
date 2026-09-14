using System.Text;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Builds the bounded prompt sent to Vertex AI for one snapshot's second-line review. Every
/// ledger row is given an opaque tag (L1, L2, ...) — the model may only ever cite a tag from this exact
/// set, which is what CalculationAiAuditValidator enforces as a structural rejection of a fabricated
/// source reference. Sends the row's own already-computed value/label/period/unit and its InputsJson
/// (field <em>names</em> only, e.g. "FinancialYearData.Revenue" — never resolved values, source-document
/// text, OCR content, or any other request's data).</summary>
public static class CalculationAiAuditPromptBuilder
{
    public const string PromptVersion = "1.0";

    public record BuiltPrompt(string PromptText, IReadOnlyDictionary<string, CalculationLedgerEntry> TagMap);

    public static BuiltPrompt Build(IReadOnlyList<CalculationLedgerEntry> ledgerEntries, int maxRows)
    {
        var capped = ledgerEntries.Take(maxRows).ToList();
        var tagMap = new Dictionary<string, CalculationLedgerEntry>();

        var sb = new StringBuilder();
        sb.AppendLine("You are a second-line reviewer auditing already-computed calculations for an MCA/ROC company report.");
        sb.AppendLine("A deterministic engine has already computed every value below and a separate deterministic check registry has already re-verified several of them independently.");
        sb.AppendLine("Your job is to spot anything that registry could not catch: arithmetic mistakes, trend anomalies, tolerance breaches, or unit mismatches. You never compute a new figure yourself.");
        sb.AppendLine();
        sb.AppendLine("STRICT RULES:");
        sb.AppendLine("- You may only cite the tags (L1, L2, ...) listed below. Never invent a tag, a filename, a page number, or an entity id.");
        sb.AppendLine("- Never introduce a number (amount, percentage, ratio, or year) that is not present in the rows below.");
        sb.AppendLine("- \"actualValue\" must be exactly the row's own stored value you are flagging — it proves you read the real number. \"expectedValue\" is your own claim about what it should be instead.");
        sb.AppendLine("- You are a candidate generator only. You cannot approve, modify, confirm, or release anything — a human reviewer confirms or rejects every candidate you raise.");
        sb.AppendLine("- If nothing looks wrong, return an empty candidates array and set noIssuesFound to true. Do not manufacture an issue to have something to report.");
        sb.AppendLine("- Respond with ONLY a JSON object matching this exact shape (no markdown fences, no commentary):");
        sb.AppendLine("""
            {
              "candidates": [
                { "ledgerTag": "string", "claimType": "ArithmeticMismatch|TrendAnomaly|ToleranceBreach|UnitMismatch|Other",
                  "expectedValue": 0, "actualValue": 0, "explanation": "string, only cites given tags",
                  "relatedLedgerTags": ["string"], "suggestedSeverity": "Minor|Material|Critical" }
              ],
              "noIssuesFound": false
            }
            """);
        sb.AppendLine();
        sb.AppendLine("=== LEDGER ROWS ===");

        var i = 1;
        foreach (var entry in capped)
        {
            var tag = $"L{i++}";
            tagMap[tag] = entry;

            sb.AppendLine($"- Tag: {tag} | Key: {entry.CalculationKey} | Period: {entry.Period} | Unit: {entry.Unit}");
            sb.AppendLine($"  Label: {entry.MetricLabel}");
            if (entry.InsufficiencyReason is not null)
                sb.AppendLine($"  Not evaluated: {entry.InsufficiencyReason}");
            else if (entry.ValueNumeric is not null)
                sb.AppendLine($"  Value: {entry.ValueNumeric}");
            else
                sb.AppendLine($"  Value: {entry.ValueText ?? "null"}");
            sb.AppendLine($"  Inputs (field names only): {entry.InputsJson}");
        }

        return new BuiltPrompt(sb.ToString(), tagMap);
    }
}
