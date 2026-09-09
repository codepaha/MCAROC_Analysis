using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>Feeds <c>Details/_ChargeDrawer.cshtml</c> — one charge plus the Phase 3 findings that name it
/// (via <see cref="AnalysisFinding.SourceReferenceJson"/>). Built in <c>_ChargesTab</c> from the request VM.</summary>
public record ChargeDrawerViewModel(RocCharge Charge, IReadOnlyList<AnalysisFinding> RelatedFindings)
{
    /// <summary>Charges-section findings whose SourceReferenceJson entityIds contain this charge's PK.
    /// Portfolio-level findings (lender concentration, registered exposure) name no single charge and
    /// are excluded.</summary>
    public static ChargeDrawerViewModel For(RocCharge charge, IEnumerable<AnalysisFinding> allFindings)
    {
        var related = allFindings
            .Where(f => f.Section == FindingSection.Charges && NamesCharge(f.SourceReferenceJson, charge.ChargeId))
            .ToList();
        return new ChargeDrawerViewModel(charge, related);
    }

    private static bool NamesCharge(string? sourceReferenceJson, long chargeId)
    {
        if (string.IsNullOrWhiteSpace(sourceReferenceJson)) return false;
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(sourceReferenceJson);
            if (doc.ValueKind != JsonValueKind.Object) return false;
            if (!doc.TryGetProperty("entityType", out var et) || et.GetString() != nameof(RocCharge)) return false;
            return doc.TryGetProperty("entityIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                && ids.EnumerateArray().Any(e => e.TryGetInt64(out var v) && v == chargeId);
        }
        catch (JsonException) { return false; }
    }
}
