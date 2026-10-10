using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>US dollars per million tokens for one model.</summary>
public sealed class ModelRate
{
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}

/// <summary>The price list the cost report applies. These are the published list prices at the time of writing and are meant to be checked against the
/// cloud bill and overridden in configuration (<c>AiPricing:Models:&lt;model&gt;:InputPerMillion</c>); a model with no entry is reported as unpriced rather
/// than silently counted as free.</summary>
public sealed class AiPricingOptions
{
    public const string SectionName = "AiPricing";

    /// <summary>For showing the total in rupees next to dollars.</summary>
    public decimal UsdToInr { get; set; } = 85m;

    public Dictionary<string, ModelRate> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-2.5-flash"] = new() { InputPerMillion = 0.30m, OutputPerMillion = 2.50m },
        ["gemini-2.5-flash-lite"] = new() { InputPerMillion = 0.10m, OutputPerMillion = 0.40m },
        ["gemini-2.5-pro"] = new() { InputPerMillion = 1.25m, OutputPerMillion = 10.00m },
        // Global-endpoint list prices (text/image/video input); the 3.x models are served from the global location only.
        ["gemini-3.1-flash-lite"] = new() { InputPerMillion = 0.25m, OutputPerMillion = 1.50m },
        ["gemini-3.5-flash-lite"] = new() { InputPerMillion = 0.30m, OutputPerMillion = 2.50m },
        ["gemini-3.5-flash"] = new() { InputPerMillion = 1.50m, OutputPerMillion = 9.00m },
        // 3.8 Flash: introductory $0.75 / $3.75 through 2026-12-31; this is the standard rate from 2027-01-01.
        ["gemini-3.8-flash"] = new() { InputPerMillion = 1.50m, OutputPerMillion = 7.50m },
        ["gemini-embedding-001"] = new() { InputPerMillion = 0.15m, OutputPerMillion = 0m },
    };
}

public static class AiCostCalculator
{
    /// <summary>The rate for a model id: an exact entry, else the longest entry the id starts with (so a dated or preview suffix still prices as its family),
    /// else null.</summary>
    public static ModelRate? RateFor(string model, AiPricingOptions pricing)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (pricing.Models.TryGetValue(model, out var exact)) return exact;
        return pricing.Models
            .Where(kv => model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => kv.Value)
            .FirstOrDefault();
    }

    /// <summary>The cost of one call in US dollars, or null when its model has no price. Reasoning tokens are billed as output.</summary>
    public static decimal? CostUsd(AiUsageRecord record, AiPricingOptions pricing)
    {
        var rate = RateFor(record.Model, pricing);
        if (rate is null) return null;
        return (record.InputTokens * rate.InputPerMillion + ((long)record.OutputTokens + record.ThoughtTokens) * rate.OutputPerMillion) / 1_000_000m;
    }
}
