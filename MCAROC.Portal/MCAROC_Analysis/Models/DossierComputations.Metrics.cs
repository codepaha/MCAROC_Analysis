using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Models;

/// <summary>The derived-metrics layer — every computed BFSI figure the dossier and the portal show,
/// built once here as pure functions over the assembled <see cref="DossierModel"/>. Contract:
/// <c>docs/analytics-catalogue.json</c>. Wave-4 issues fill the sections in:
/// D1 charges (#56) · D2 financial trend/leverage (#57) · D3 GST (#58) · D4 shareholding (#59) ·
/// D5 legal (#60) · D6 directors (#61) · D7 EPFO (#62) · D8 peers (#63) · D9 cost/forex (#64) ·
/// D10 RPT (#65) · D11 credit ratings (#66).
///
/// Guardrails: not a score (never combine metrics into an index); every metric returns a
/// <see cref="MetricResult"/> with its inputs, period and — when it cannot be computed — an
/// insufficiency reason instead of a fake 0; the whole layer renders in ALL dossier variants
/// including SourceRecord (no-AI).</summary>
public static partial class DossierComputations
{
    /// <summary>All metric groups for a dossier, in render order. Empty until the D-wave issues add
    /// sections — the plumbing (this method, the Snapshot "Key Indicators" block, the portal
    /// <c>_KeyIndicators</c> partial) ships first in D0 so every later section is regression-guarded
    /// from the moment it lands.</summary>
    public static IReadOnlyList<MetricGroup> BuildMetricGroups(Dossier.DossierModel model)
    {
        var groups = new List<MetricGroup>();

        // D1..D11 append their MetricGroup here, e.g.:
        //   groups.Add(ChargeRegisterMetrics(model));
        // Each builder returns a MetricGroup whose Metrics are MetricResult.Ok / .Insufficient.

        return groups.Where(g => g.HasAny).ToList();
    }
}
