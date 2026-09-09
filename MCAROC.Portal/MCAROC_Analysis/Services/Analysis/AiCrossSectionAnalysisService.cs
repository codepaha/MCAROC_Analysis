using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Analysis;

public record ExecutiveSummary(
    string BusinessPerformance, string FinancialPosition, string BorrowingSecurity,
    string GovernanceCompliance, IReadOnlyList<string> KeyReviewItems);

public record FindingNarrative(string Code, string WhyThisMatters, string RecommendedReview);

public record AiCrossSectionFinding(string Title, FindingSeverity Severity, string Narrative, IReadOnlyList<string> RelatedCodes);

public record AiSynthesisOutcome(
    bool Success,
    ExecutiveSummary? ExecutiveSummary,
    IReadOnlyList<FindingNarrative> FindingNarratives,
    IReadOnlyList<AiCrossSectionFinding> CrossSectionFindings,
    string? FailureReason);

/// <summary>Calls Gemini 2.5 Flash-Lite via Vertex AI (same construction pattern as Phase 2's
/// VertexAiExtractionService) to synthesize an executive summary, per-finding narratives (Critical/Review
/// only), and a small number of additional cross-section findings — never severity, never priority, never a
/// new numeric fact. One call per AnalysisRun.</summary>
public partial class AiCrossSectionAnalysisService
{
    public const string ModelId = "gemini-2.5-flash-lite";
    public const string PromptVersion = "1.0";
    private const int MaxAiCrossSectionFindings = 3;

    private readonly GenAiClient _client;
    private readonly ILogger<AiCrossSectionAnalysisService> _logger;

    public AiCrossSectionAnalysisService(string projectId, string location, string credentialsPath, ILogger<AiCrossSectionAnalysisService> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    public async Task<AiSynthesisOutcome> SynthesizeAsync(
        IReadOnlyList<AnalysisFinding> findings, ReviewPriority priority,
        IReadOnlyList<(string Code, string Reason)> dataSufficiencyNotes, CancellationToken ct)
    {
        var prompt = BuildPrompt(findings, priority, dataSufficiencyNotes);

        string rawResponse;
        try
        {
            var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
            var response = await _client.Models.GenerateContentAsync(ModelId, prompt, config, ct);
            rawResponse = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vertex AI cross-section synthesis call failed");
            return new AiSynthesisOutcome(false, null, [], [], ex.Message);
        }

        return Validate(rawResponse, findings);
    }

    private static string BuildPrompt(IReadOnlyList<AnalysisFinding> findings, ReviewPriority priority, IReadOnlyList<(string Code, string Reason)> dataSufficiencyNotes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a credit-analysis assistant summarizing an MCA/ROC company review for a BFSI analyst.");
        sb.AppendLine("A deterministic rule engine has already computed every finding below, including its severity and the overall review priority.");
        sb.AppendLine($"Overall review priority (already decided, you cannot change it): {priority}.");
        sb.AppendLine();
        sb.AppendLine("STRICT RULES:");
        sb.AppendLine("- Never assert an unproven causal fact (e.g. do not say a company 'cannot pay salaries' or 'is insolvent'). Correlate and hedge — use language like 'may indicate potential...'.");
        sb.AppendLine("- Never introduce a number (amount, percentage, ratio, or year) that is not present in the finding data below.");
        sb.AppendLine("- Only propose a cross-section finding when it genuinely combines findings from different domains — not a rephrasing of one finding.");
        sb.AppendLine("- Respond with ONLY a JSON object matching this exact shape (no markdown fences, no commentary):");
        sb.AppendLine("""
            {
              "executiveSummary": {
                "businessPerformance": "string", "financialPosition": "string", "borrowingSecurity": "string",
                "governanceCompliance": "string", "keyReviewItems": ["string"]
              },
              "findingNarratives": [{ "code": "string", "whyThisMatters": "string", "recommendedReview": "string" }],
              "crossSectionFindings": [{ "title": "string", "severity": "Critical|Review", "narrative": "string", "relatedCodes": ["string"] }]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("=== FINDINGS (including positive/counter-signal findings — use them to avoid contradicting a stabilizing signal) ===");
        foreach (var f in findings)
        {
            sb.AppendLine($"- Code: {f.Code} | Section: {f.Section} | Severity: {f.Severity} | Temporal: {f.TemporalStatus}");
            sb.AppendLine($"  Title: {f.Title}");
            sb.AppendLine($"  Summary: {f.SummaryText}");
            if (f.MetricsJson is not null) sb.AppendLine($"  Metrics: {f.MetricsJson}");
        }

        if (dataSufficiencyNotes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== DATA NOT EVALUATED (do not treat these as adverse — data was simply unavailable) ===");
            foreach (var (code, reason) in dataSufficiencyNotes)
                sb.AppendLine($"- {code}: {reason}");
        }

        return sb.ToString();
    }

    internal static AiSynthesisOutcome Validate(string rawResponse, IReadOnlyList<AnalysisFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new AiSynthesisOutcome(false, null, [], [], "Empty model response.");

        AiResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AiResponseDto>(rawResponse, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new AiSynthesisOutcome(false, null, [], [], $"Model did not return valid JSON: {ex.Message}");
        }
        if (dto is null)
            return new AiSynthesisOutcome(false, null, [], [], "Model response parsed to null.");

        var findingsByCode = findings.ToDictionary(f => f.Code);
        var deterministicCrossSectionCodeSets = findings
            .Where(f => f.Section == FindingSection.CrossSection)
            .Select(f => ParseSupportingCodes(f.SupportingSignalsJson))
            .ToList();

        var narratives = new List<FindingNarrative>();
        var seenCodes = new HashSet<string>();
        foreach (var n in dto.FindingNarratives ?? [])
        {
            if (n.Code is null || !seenCodes.Add(n.Code)) continue;
            if (!findingsByCode.TryGetValue(n.Code, out var finding)) continue;
            if (finding.Severity is not (FindingSeverity.Critical or FindingSeverity.Review)) continue;
            if (string.IsNullOrWhiteSpace(n.WhyThisMatters) || string.IsNullOrWhiteSpace(n.RecommendedReview)) continue;

            var allowed = BuildAllowedNumbers(finding);
            if (ContainsUnsupportedFinancialNumber(n.WhyThisMatters, allowed) || ContainsUnsupportedFinancialNumber(n.RecommendedReview, allowed))
                continue; // dropped, not the whole run

            narratives.Add(new FindingNarrative(n.Code, n.WhyThisMatters, n.RecommendedReview));
        }

        var crossSection = new List<AiCrossSectionFinding>();
        foreach (var c in dto.CrossSectionFindings ?? [])
        {
            if (crossSection.Count >= MaxAiCrossSectionFindings) break;
            if (c.RelatedCodes is not { Count: >= 2 } relatedCodes) continue;
            if (!relatedCodes.All(findingsByCode.ContainsKey)) continue;
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Narrative)) continue;

            var relatedCodeSet = relatedCodes.ToHashSet();
            if (deterministicCrossSectionCodeSets.Any(existing => relatedCodeSet.IsSubsetOf(existing) || existing.IsSubsetOf(relatedCodeSet)))
                continue; // duplicative of an already-fired deterministic cross-section finding

            var relatedSeverities = relatedCodes.Select(rc => findingsByCode[rc].Severity).ToList();
            var highestAdverse = relatedSeverities.Where(s => s != FindingSeverity.Positive).DefaultIfEmpty(FindingSeverity.Watch).Max();
            var requestedSeverity = ParseSeverity(c.Severity) ?? highestAdverse;
            // No escalation authority: capped at the highest adverse related severity, never higher.
            var clampedSeverity = (FindingSeverity)Math.Min((int)requestedSeverity, (int)highestAdverse);

            var allowedForCombo = relatedCodes.SelectMany(rc => BuildAllowedNumbers(findingsByCode[rc])).ToHashSet();
            if (ContainsUnsupportedFinancialNumber(c.Narrative, allowedForCombo))
                continue;

            crossSection.Add(new AiCrossSectionFinding(c.Title, clampedSeverity, c.Narrative, relatedCodes));
        }

        // The executive summary is a synthesis across the whole analysis, not one specific finding, so it's
        // checked against the union of every finding's own allowed numbers (round-2-review fix — this path
        // previously persisted the summary fields with no numeric validation at all, unlike
        // findingNarratives/crossSectionFindings above, so Gemini could introduce an unsupported amount,
        // percentage, ratio, or year straight into the headline summary a user reads first).
        var allowedAcrossAllFindings = findings.SelectMany(BuildAllowedNumbers).ToHashSet();
        ExecutiveSummary? summary = dto.ExecutiveSummary is { } es
            ? BuildValidatedExecutiveSummary(es, allowedAcrossAllFindings)
            : null;

        return new AiSynthesisOutcome(true, summary, narratives, crossSection, null);
    }

    /// <summary>Sanitizes each executive-summary field independently — a field containing an unsupported
    /// number is blanked to "", not treated as a reason to discard the whole summary, matching this file's
    /// "drop the specific offending piece, not the whole run" discipline elsewhere.</summary>
    private static ExecutiveSummary BuildValidatedExecutiveSummary(ExecutiveSummaryDto es, HashSet<string> allowed)
    {
        string Sanitize(string? text) =>
            string.IsNullOrWhiteSpace(text) || ContainsUnsupportedFinancialNumber(text, allowed) ? "" : text;

        return new ExecutiveSummary(
            Sanitize(es.BusinessPerformance),
            Sanitize(es.FinancialPosition),
            Sanitize(es.BorrowingSecurity),
            Sanitize(es.GovernanceCompliance),
            (es.KeyReviewItems ?? []).Where(item => !ContainsUnsupportedFinancialNumber(item, allowed)).ToList());
    }

    private static HashSet<string> ParseSupportingCodes(string? supportingSignalsJson)
    {
        if (supportingSignalsJson is null) return [];
        try { return JsonSerializer.Deserialize<List<string>>(supportingSignalsJson)?.ToHashSet() ?? []; }
        catch (JsonException) { return []; }
    }

    private static FindingSeverity? ParseSeverity(string? raw) =>
        Enum.TryParse<FindingSeverity>(raw, ignoreCase: true, out var value) ? value : null;

    /// <summary>Numbers this specific finding's own data supports: its MetricsJson values (rounded to 0
    /// and 1 decimal for tolerance), 4-digit years from its PeriodLabel, and small counting cardinalities
    /// (1-10, e.g. "over two years") which are never treated as financial claims.</summary>
    private static HashSet<string> BuildAllowedNumbers(AnalysisFinding finding)
    {
        var allowed = new HashSet<string>();
        for (var i = 1; i <= 10; i++) allowed.Add(i.ToString(CultureInfo.InvariantCulture));

        if (finding.PeriodLabel is not null)
            foreach (Match m in YearRegex().Matches(finding.PeriodLabel))
                allowed.Add(m.Value);

        if (finding.MetricsJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(finding.MetricsJson);
                CollectNumbers(doc.RootElement, allowed);
            }
            catch (JsonException) { /* leave allowed as-is */ }
        }

        return allowed;
    }

    private static void CollectNumbers(JsonElement element, HashSet<string> allowed)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject()) CollectNumbers(prop.Value, allowed);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectNumbers(item, allowed);
                break;
            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var d))
                {
                    allowed.Add(Math.Round(d, 0).ToString(CultureInfo.InvariantCulture));
                    allowed.Add(Math.Round(d, 1).ToString(CultureInfo.InvariantCulture));
                    allowed.Add(Math.Round(Math.Abs(d), 0).ToString(CultureInfo.InvariantCulture));
                }
                break;
        }
    }

    /// <summary>Only financial/material numeric claims are checked strictly — currency amounts,
    /// percentages, ratios, and years — never general counting language ("over two years"), which this
    /// pattern deliberately does not match.</summary>
    private static bool ContainsUnsupportedFinancialNumber(string text, HashSet<string> allowed)
    {
        foreach (Match m in FinancialNumberRegex().Matches(text))
        {
            var digitsOnly = new string(m.Value.Where(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            if (digitsOnly.Length == 0 || !decimal.TryParse(digitsOnly, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                continue;

            var rounded0 = Math.Round(value, 0).ToString(CultureInfo.InvariantCulture);
            var rounded1 = Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
            if (!allowed.Contains(rounded0) && !allowed.Contains(rounded1) && !allowed.Contains(digitsOnly))
                return true;
        }
        return false;
    }

    // Amounts are recognized under ₹, "Rs."/"Rs", and "INR" prefixes, each optionally followed by a
    // crore/lakh magnitude suffix, plus bare magnitude-suffixed amounts with no currency marker at all
    // (e.g. "999 crore") — "crore"/"lakh" are themselves financial-scale words, unlike plain counting
    // language ("over two years"), so a bare magnitude-suffixed number is still a material numeric claim
    // that must be validated against the allowed set.
    [GeneratedRegex(
        @"₹\s?[\d,]+(\.\d+)?(\s?(?:crore|cr\.?|lakh))?" +
        @"|\b(?:Rs\.?|INR)\s?[\d,]+(\.\d+)?(\s?(?:crore|cr\.?|lakh))?" +
        @"|\b[\d,]+(\.\d+)?\s?(?:crore|lakh)\b" +
        @"|[\d.]+\s?%|[\d.]+x\b|FY\s?\d{4}|\b(19|20)\d{2}\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FinancialNumberRegex();

    [GeneratedRegex(@"(19|20)\d{2}")]
    private static partial Regex YearRegex();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private record AiResponseDto(
        [property: JsonPropertyName("executiveSummary")] ExecutiveSummaryDto? ExecutiveSummary,
        [property: JsonPropertyName("findingNarratives")] List<FindingNarrativeDto>? FindingNarratives,
        [property: JsonPropertyName("crossSectionFindings")] List<CrossSectionFindingDto>? CrossSectionFindings);

    private record ExecutiveSummaryDto(
        [property: JsonPropertyName("businessPerformance")] string? BusinessPerformance,
        [property: JsonPropertyName("financialPosition")] string? FinancialPosition,
        [property: JsonPropertyName("borrowingSecurity")] string? BorrowingSecurity,
        [property: JsonPropertyName("governanceCompliance")] string? GovernanceCompliance,
        [property: JsonPropertyName("keyReviewItems")] List<string>? KeyReviewItems);

    private record FindingNarrativeDto(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("whyThisMatters")] string? WhyThisMatters,
        [property: JsonPropertyName("recommendedReview")] string? RecommendedReview);

    private record CrossSectionFindingDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("narrative")] string? Narrative,
        [property: JsonPropertyName("relatedCodes")] List<string>? RelatedCodes);
}
