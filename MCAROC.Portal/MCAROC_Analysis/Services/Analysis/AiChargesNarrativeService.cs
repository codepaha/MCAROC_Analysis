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
using MCAROC_Analysis.Services.AiUsage;

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
    public const string ModelId = "gemini-3.1-flash-lite";
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

    protected AiChargesNarrativeService()
    {
        _client = null!;
        _logger = null!;
    }

    public AiChargesNarrativeService(string projectId, string location, string credentialsPath, ILogger<AiChargesNarrativeService> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    /// <summary>One row per charge actually sent to the model, after MaxChargesConsidered and the running
    /// character budget have both been applied — <see cref="SelectChargesForNarrative"/> builds this list.</summary>
    public record SelectedCharge(RocCharge Charge, RocChargeEvent? RepresentativeEvent);

    public virtual async Task<ChargesNarrativeOutcome> SynthesizeAsync(
        IReadOnlyList<SelectedCharge> selectedCharges, int totalOpenChargeCount, CancellationToken ct)
    {
        var prompt = BuildPrompt(selectedCharges, totalOpenChargeCount);

        string rawResponse;
        try
        {
            var config = new GenerateContentConfig { ResponseMimeType = "application/json" };
            var response = await _client.Models.GenerateContentAsync(ModelId, prompt, config, ct);
            AiUsageLog.RecordGeneration(AiUsagePurposes.ChargesNarrative, ModelId, response.UsageMetadata);
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
        // Every entry is "unit:number" (see FigureKey): a figure is only supported by the same number in the same unit, so a source
        // "9.5" does not support "9.5%", a "%" does not support "x", and "Rs 700 Lakh" does not support "700 crore".
        var allowed = new HashSet<string>();
        void AddBare(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (decimal.TryParse(text.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) allowed.Add("n:" + Norm(d));
            allowed.Add("n:" + text);
        }
        AddBare(selectedCharges.Count.ToString(CultureInfo.InvariantCulture));
        AddBare(totalOpenChargeCount.ToString(CultureInfo.InvariantCulture));

        void AddAmount(decimal? v)
        {
            if (v is not { } d) return;
            // The amount as the prompt shows it (N2) and its shorter written forms: 160.00 / 160.0 / 160, bare or as a rupee amount
            // in any scale (the prompt does not say which scale the narrative will use for it).
            foreach (var form in new[] { Math.Round(d, 0), Math.Round(d, 1), Math.Round(d, 2) })
            {
                allowed.Add("n:" + Norm(form));
                allowed.Add("amt:*:" + Norm(form));
            }
        }

        // Numbers that appear in the wording of the charge data the model was shown (the instrument, property, extent, terms and holder text):
        // "56 new centres", "Rs 700 Lakh", "25.02.2026" are quotations of the data, not inventions. Without them a faithful summary that
        // repeats a figure from the deed wording was rejected as "number not present in the data" (11 of 18 rejections in the 41-company
        // preflight). Sums and other derived figures are still not allowed: they appear nowhere in the data.
        void AddNumbersFrom(string? text)
        {
            foreach (var key in FigureKeys(text)) allowed.Add(key);
        }

        foreach (var sc in selectedCharges)
        {
            AddNumbersFrom(Truncate(sc.Charge.LatestChargeHolderRaw));
            if (sc.RepresentativeEvent is { } shown)
            {
                AddNumbersFrom(Truncate(shown.InstrumentDescription)); AddNumbersFrom(Truncate(shown.PropertyParticulars));
                AddNumbersFrom(Truncate(shown.ExtentAndOperation)); AddNumbersFrom(Truncate(shown.OtherTerms)); AddNumbersFrom(Truncate(shown.ModificationParticulars));
            }
            AddBare(sc.Charge.ChargeId.ToString(CultureInfo.InvariantCulture));
            AddBare(sc.Charge.RocChargeNumber);
            AddAmount(sc.Charge.CurrentAmount);
            if (sc.Charge.CreationDate is { } cd) AddBare(cd.Year.ToString(CultureInfo.InvariantCulture));
            if (sc.Charge.LatestModificationDate is { } md) AddBare(md.Year.ToString(CultureInfo.InvariantCulture));
            if (sc.RepresentativeEvent?.EventDate is { } ed) AddBare(ed.Year.ToString(CultureInfo.InvariantCulture));
            if (sc.RepresentativeEvent?.ChargeAmount is { } ca) AddAmount(ca);
        }

        return allowed;
    }

    private static string Norm(decimal d) => d.ToString("0.############", CultureInfo.InvariantCulture);

    // One figure: an optional rupee marker before, the number, an optional unit after (%, x, crore, lakh).
    [GeneratedRegex(@"(?<pre>₹\s?|\b(?:Rs\.?|INR)\s?)?(?<num>\d[\d,]*(?:\.\d+)?)(?<post>\s?(?:%|per\s?cent\b|percent\b|x\b|crore\b|cr\b|lakh\b|lac\b))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex FigureRegex();

    /// <summary>The figures in <paramref name="text"/> as "unit:number" keys, where the unit is read from what is written next to the
    /// number: "pct" for % / percent, "x" for a multiple, "amt:{scale}" for a rupee amount (₹/Rs/INR before, crore/lakh after), else "n".
    /// Used for both the data and the narrative, so the two are compared on the same terms.</summary>
    private static IEnumerable<string> FigureKeys(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (Match m in FigureRegex().Matches(text))
        {
            var digits = m.Groups["num"].Value.Replace(",", "");
            if (!decimal.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) continue;
            var n = Norm(value);
            var post = m.Groups["post"].Value.Trim().ToLowerInvariant();
            var hasPre = m.Groups["pre"].Success && m.Groups["pre"].Length > 0;
            string scale = post switch { "crore" or "cr" => "crore", "lakh" or "lac" => "lakh", _ => "" };
            if (post == "%" || post.StartsWith("per") ) yield return "pct:" + n;
            else if (post == "x") yield return "x:" + n;
            else if (hasPre || scale.Length > 0) yield return $"amt:{scale}:{n}";
            else yield return "n:" + n;
        }
    }

    private static bool ContainsUnsupportedNumber(string text, HashSet<string> allowed)
    {
        foreach (var key in FigureKeys(text))
        {
            if (allowed.Contains(key)) continue;
            // A rupee amount from the charge's own amount field is accepted in any scale.
            if (key.StartsWith("amt:", StringComparison.Ordinal) && allowed.Contains("amt:*:" + key[(key.LastIndexOf(':') + 1)..])) continue;
            return true;
        }
        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private record ChargesNarrativeDto(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("notableCollateralPoints")] List<NotableCollateralPointDto>? NotableCollateralPoints);

    private record NotableCollateralPointDto(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("chargeIds")] List<long>? ChargeIds);
}
