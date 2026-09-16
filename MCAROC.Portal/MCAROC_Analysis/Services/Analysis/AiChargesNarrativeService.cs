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

public record NotableCollateralPoint(string Text, IReadOnlyList<long> ChargeIds);

public record ChargesNarrative(
    string Summary, IReadOnlyList<NotableCollateralPoint> NotableCollateralPoints,
    int CoveredChargeCount, int TotalOpenChargeCount);

public record ChargesNarrativeOutcome(bool Success, ChargesNarrative? Narrative, string? FailureReason);

/// <summary>Calls Gemini 2.5 Flash-Lite via Vertex AI (same construction pattern as
/// AiCrossSectionAnalysisService) to synthesize a short read of the largest open charges — what's actually
/// secured, and anything worth a second look (overlapping collateral, unusually broad wording, joint/
/// consortium holdings). One call per AnalysisRun, entirely independent of the cross-section synthesis
/// call: own prompt, own PromptVersion, own failure mode that never touches AnalysisRun.Status.</summary>
public partial class AiChargesNarrativeService
{
    public const string ModelId = "gemini-2.5-flash-lite";
    public const string PromptVersion = "1.0";

    /// <summary>Charges are sorted by CurrentAmount descending (DossierComputations.OpenChargesByAmount)
    /// before this cap is applied, so "top N" always means the N largest, never an arbitrary N.</summary>
    internal const int MaxChargesConsidered = 50;

    /// <summary>Per-field truncation for any single free-text charge field (PropertyParticulars,
    /// ExtentAndOperation, etc.) — some source values run to several sentences, and 50 charges × ~6 such
    /// fields each can still blow a practical prompt budget even after the charge-count cap alone.</summary>
    internal const int MaxFieldLength = 500;

    /// <summary>Running prompt-character budget across all charges sent — stops adding further charges
    /// once exceeded, independent of MaxChargesConsidered (truncation may cut the list shorter than N).</summary>
    internal const int MaxPromptCharacterBudget = 12_000;

    internal const int MaxNotableCollateralPoints = 5;

    private readonly GenAiClient _client;
    private readonly ILogger<AiChargesNarrativeService> _logger;

    public AiChargesNarrativeService(string projectId, string location, string credentialsPath, ILogger<AiChargesNarrativeService> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    /// <summary>One row per charge actually sent to the model, after MaxChargesConsidered and the running
    /// character budget have both been applied — <see cref="SelectChargesForNarrative"/> builds this list.</summary>
    public record SelectedCharge(RocCharge Charge, RocChargeEvent? RepresentativeEvent);

    public async Task<ChargesNarrativeOutcome> SynthesizeAsync(
        IReadOnlyList<SelectedCharge> selectedCharges, int totalOpenChargeCount, CancellationToken ct)
    {
        var prompt = BuildPrompt(selectedCharges, totalOpenChargeCount);

        string rawResponse;
        try
        {
            var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
            var response = await _client.Models.GenerateContentAsync(ModelId, prompt, config, ct);
            rawResponse = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vertex AI charges-narrative synthesis call failed");
            return new ChargesNarrativeOutcome(false, null, ex.Message);
        }

        return Validate(rawResponse, selectedCharges, totalOpenChargeCount);
    }

    /// <summary>Given a run's open charges (already filtered to open via DossierComputations.IsOpenCharge/
    /// OpenChargesByAmount by the caller — this method does not re-derive "open" itself) sorted by
    /// CurrentAmount descending, produces the top-N to actually send, each paired with its deterministically
    /// resolved latest property-bearing event. Two independent caps apply: MaxChargesConsidered (a hard
    /// count) and MaxPromptCharacterBudget (a running total of the truncated field lengths actually
    /// emitted) — whichever is hit first stops the list, so CoveredChargeCount can end up below N.</summary>
    internal static IReadOnlyList<SelectedCharge> SelectChargesForNarrative(IReadOnlyList<RocCharge> openChargesByAmountDescending)
    {
        var result = new List<SelectedCharge>();
        var budgetUsed = 0;

        foreach (var charge in openChargesByAmountDescending.Take(MaxChargesConsidered))
        {
            // Explicit ordering before LastOrDefault — EF's .Include()-populated child collection order is
            // not guaranteed, and ChargeCard (DossierPdfComposer.Annexures.cs) already establishes the
            // correct pattern: order by EventDate, tiebreak by id, then take the latest property-bearing one.
            var representative = charge.Events
                .OrderBy(e => e.EventDate ?? DateOnly.MinValue).ThenBy(e => e.ChargeEventId)
                .LastOrDefault(e => !string.IsNullOrWhiteSpace(e.PropertyParticulars));

            var fieldLength = TruncatedFieldsLength(charge, representative);
            if (result.Count > 0 && budgetUsed + fieldLength > MaxPromptCharacterBudget)
                break;

            result.Add(new SelectedCharge(charge, representative));
            budgetUsed += fieldLength;
        }

        return result;
    }

    private static int TruncatedFieldsLength(RocCharge charge, RocChargeEvent? ev) =>
        Truncate(charge.LatestChargeHolderRaw).Length +
        (ev is null ? 0 : Truncate(ev.InstrumentDescription).Length + Truncate(ev.PropertyParticulars).Length +
            Truncate(ev.ExtentAndOperation).Length + Truncate(ev.OtherTerms).Length +
            Truncate(ev.ModificationParticulars).Length + Truncate(ev.RateOfInterest).Length + Truncate(ev.TermsOfPayment).Length);

    /// <summary>Truncates to MaxFieldLength with a visible "…" marker — truncation is never silent.</summary>
    private static string Truncate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Length <= MaxFieldLength ? s : s[..MaxFieldLength] + "…";
    }

    internal static string BuildPrompt(IReadOnlyList<SelectedCharge> selectedCharges, int totalOpenChargeCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a credit-analysis assistant summarizing the registered charges (ROC/MCA security) of a company for a BFSI analyst.");
        sb.AppendLine($"Below are the largest {selectedCharges.Count} of {totalOpenChargeCount} open charges by amount, each with its most recent property-bearing lifecycle event.");
        sb.AppendLine("This is NOT the complete set of open charges — never imply completeness. Always make clear the narrative covers only the charges shown below.");
        sb.AppendLine();
        sb.AppendLine("STRICT RULES:");
        sb.AppendLine("- Never introduce an amount, percentage, date, or year that is not present in the charge data below.");
        sb.AppendLine("- Never assert a legal conclusion (e.g. do not say a charge is 'invalid' or 'unenforceable') — describe and hedge, e.g. 'may warrant a closer legal review'.");
        sb.AppendLine("- Do your best not to invent a lender/holder name not present below, but note this is not mechanically verified downstream — describe wording, don't fabricate parties.");
        sb.AppendLine("- Respond with ONLY a JSON object matching this exact shape (no markdown fences, no commentary):");
        sb.AppendLine("""
            {
              "summary": "string",
              "notableCollateralPoints": [{ "text": "string", "chargeIds": [123] }]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("=== CHARGES (largest by amount first) ===");
        foreach (var sc in selectedCharges)
        {
            var c = sc.Charge;
            sb.AppendLine($"- ChargeId: {c.ChargeId} | Charge {c.RocChargeNumber} | Holder: {Truncate(c.LatestChargeHolderRaw)} | Amount: {c.CurrentAmount?.ToString("N2", CultureInfo.InvariantCulture) ?? "-"}");
            if (sc.RepresentativeEvent is { } ev)
            {
                if (!string.IsNullOrWhiteSpace(ev.InstrumentDescription)) sb.AppendLine($"  Instrument: {Truncate(ev.InstrumentDescription)}");
                if (!string.IsNullOrWhiteSpace(ev.PropertyParticulars)) sb.AppendLine($"  Property particulars: {Truncate(ev.PropertyParticulars)}");
                if (!string.IsNullOrWhiteSpace(ev.ExtentAndOperation)) sb.AppendLine($"  Extent and operation: {Truncate(ev.ExtentAndOperation)}");
                if (!string.IsNullOrWhiteSpace(ev.OtherTerms)) sb.AppendLine($"  Other terms: {Truncate(ev.OtherTerms)}");
                if (!string.IsNullOrWhiteSpace(ev.ModificationParticulars)) sb.AppendLine($"  Modification particulars: {Truncate(ev.ModificationParticulars)}");
                if (ev.JointHolding is true) sb.AppendLine("  Joint holding: yes");
                if (ev.ConsortiumHolding is true) sb.AppendLine("  Consortium holding: yes");
            }
        }

        return sb.ToString();
    }

    internal static ChargesNarrativeOutcome Validate(string rawResponse, IReadOnlyList<SelectedCharge> selectedCharges, int totalOpenChargeCount)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new ChargesNarrativeOutcome(false, null, "Empty model response.");

        ChargesNarrativeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ChargesNarrativeDto>(rawResponse, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new ChargesNarrativeOutcome(false, null, $"Model did not return valid JSON: {ex.Message}");
        }
        if (dto is null)
            return new ChargesNarrativeOutcome(false, null, "Model response parsed to null.");

        var sentChargeIds = selectedCharges.Select(sc => sc.Charge.ChargeId).ToHashSet();
        var allowed = BuildAllowedNumbers(selectedCharges, totalOpenChargeCount);

        // Summary is one cohesive paragraph, not a discardable list — it cannot be partially redacted the
        // way a bullet point can without producing broken prose, so an unsupported number here fails the
        // whole outcome rather than being silently dropped.
        if (string.IsNullOrWhiteSpace(dto.Summary))
            return new ChargesNarrativeOutcome(false, null, "Model response has no summary.");
        if (ContainsUnsupportedNumber(dto.Summary, allowed))
            return new ChargesNarrativeOutcome(false, null, "Summary contains a number not present in the charge data sent.");

        var points = new List<NotableCollateralPoint>();
        foreach (var p in dto.NotableCollateralPoints ?? [])
        {
            if (points.Count >= MaxNotableCollateralPoints) break;
            if (string.IsNullOrWhiteSpace(p.Text)) continue;
            if (ContainsUnsupportedNumber(p.Text, allowed)) continue; // dropped, not the whole outcome

            var chargeIds = (p.ChargeIds ?? []).Where(id => sentChargeIds.Contains(id)).ToList();
            if (chargeIds.Count == 0) continue; // no verifiable reference to a charge actually sent — dropped

            points.Add(new NotableCollateralPoint(p.Text, chargeIds));
        }

        // Server-derived, never model-provided — overwritten regardless of what (if anything) the model's
        // JSON claimed for these two fields, on every outcome path that reaches here.
        var narrative = new ChargesNarrative(dto.Summary, points, selectedCharges.Count, totalOpenChargeCount);
        return new ChargesNarrativeOutcome(true, narrative, null);
    }

    /// <summary>Every number the model was actually shown, and nothing else — deliberately NOT a blanket
    /// small-integer allow-list (that would let a fabricated count like "5 material charges" through
    /// unchecked when only 1 charge was ever sent). selectedCharges.Count/totalOpenChargeCount are here so
    /// a genuine "the largest of the N charges shown" reads as supported; each charge's own ChargeId and
    /// RocChargeNumber are here because BuildPrompt shows both to the model, so a prose reference to either
    /// (not just the structured chargeIds field already cross-checked separately) must not be rejected as
    /// invented.</summary>
    private static HashSet<string> BuildAllowedNumbers(IReadOnlyList<SelectedCharge> selectedCharges, int totalOpenChargeCount)
    {
        var allowed = new HashSet<string>
        {
            selectedCharges.Count.ToString(CultureInfo.InvariantCulture),
            totalOpenChargeCount.ToString(CultureInfo.InvariantCulture)
        };

        void AddAmount(decimal? v)
        {
            if (v is not { } d) return;
            allowed.Add(Math.Round(d, 0).ToString(CultureInfo.InvariantCulture));
            allowed.Add(Math.Round(d, 1).ToString(CultureInfo.InvariantCulture));
            allowed.Add(Math.Round(d, 2).ToString(CultureInfo.InvariantCulture));
        }

        foreach (var sc in selectedCharges)
        {
            allowed.Add(sc.Charge.ChargeId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(sc.Charge.RocChargeNumber)) allowed.Add(sc.Charge.RocChargeNumber);
            AddAmount(sc.Charge.CurrentAmount);
            if (sc.Charge.CreationDate is { } cd) allowed.Add(cd.Year.ToString(CultureInfo.InvariantCulture));
            if (sc.Charge.LatestModificationDate is { } md) allowed.Add(md.Year.ToString(CultureInfo.InvariantCulture));
            if (sc.RepresentativeEvent?.EventDate is { } ed) allowed.Add(ed.Year.ToString(CultureInfo.InvariantCulture));
            if (sc.RepresentativeEvent?.ChargeAmount is { } ca) AddAmount(ca);
        }

        return allowed;
    }

    private static bool ContainsUnsupportedNumber(string text, HashSet<string> allowed)
    {
        foreach (Match m in FinancialNumberRegex().Matches(text))
        {
            var digitsOnly = new string(m.Value.Where(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            if (digitsOnly.Length == 0 || !decimal.TryParse(digitsOnly, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                continue;

            var rounded0 = Math.Round(value, 0).ToString(CultureInfo.InvariantCulture);
            var rounded1 = Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
            var rounded2 = Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
            if (!allowed.Contains(rounded0) && !allowed.Contains(rounded1) && !allowed.Contains(rounded2) && !allowed.Contains(digitsOnly))
                return true;
        }
        return false;
    }

    // Starts from the same amount/percentage/year recognition as AiCrossSectionAnalysisService's regex
    // (₹/Rs./INR-prefixed and bare crore/lakh-suffixed amounts, percentages, ratios, years), plus one more
    // alternative this service needs that the sibling doesn't: a fully bare number with no currency marker,
    // no crore/lakh suffix, nothing (e.g. a fabricated "999" or "5 charges"). Without it, an unmarked
    // invented figure was never even inspected. Kept as a trailing fallback, not a replacement for the
    // marker-specific alternatives above, because those need their own trailing boundary (`%`, `x`, `crore`)
    // to correctly bound the match where the bare pattern's `\b` wouldn't (e.g. "3x" has no word boundary
    // between the digit and the letter).
    [GeneratedRegex(
        @"₹\s?[\d,]+(\.\d+)?(\s?(?:crore|cr\.?|lakh))?" +
        @"|\b(?:Rs\.?|INR)\s?[\d,]+(\.\d+)?(\s?(?:crore|cr\.?|lakh))?" +
        @"|\b[\d,]+(\.\d+)?\s?(?:crore|lakh)\b" +
        @"|[\d.]+\s?%|[\d.]+x\b|FY\s?\d{4}|\b(19|20)\d{2}\b" +
        @"|\b\d[\d,]*(\.\d+)?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FinancialNumberRegex();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private record ChargesNarrativeDto(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("notableCollateralPoints")] List<NotableCollateralPointDto>? NotableCollateralPoints);

    private record NotableCollateralPointDto(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("chargeIds")] List<long>? ChargeIds);
}
