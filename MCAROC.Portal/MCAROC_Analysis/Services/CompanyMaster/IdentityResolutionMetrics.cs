using System.Globalization;
using System.Text;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>One <see cref="IdentityResolution"/> row, as the live metrics need it.</summary>
public sealed record ResolutionMetricRow(
    long RequestId, ResolutionMethod? Method, string ReasonCode, string? InputIdentifier,
    string? ChosenIdentifier, string? RecommendedIdentifier, bool AutoSelectEligible);

/// <param name="NameDecisions">Resolutions of a typed name (no identifier supplied).</param>
/// <param name="AutoSelected">Name decisions the resolver chose on its own.</param>
/// <param name="AutoSelectEligible">Name decisions that met every auto-select condition — what enabling auto-select would do.</param>
/// <param name="HumanSelections">Companies selected by a person.</param>
/// <param name="HumanSelectionsAfterSuggestion">Of those, selections made after the resolver chose or recommended a company.</param>
/// <param name="HumanOverrides">Of those, selections of a different company than the resolver's.</param>
/// <param name="FalseAccepts">Auto-selections the reference tool's preview refuted.</param>
public sealed record LiveResolutionMetrics(
    int NameDecisions, int AutoSelected, int AutoSelectEligible, int HumanSelections, int HumanSelectionsAfterSuggestion,
    int HumanOverrides, int FalseAccepts)
{
    public double? AutoSelectRate => NameDecisions == 0 ? null : (double)AutoSelected / NameDecisions;
    public double? HumanOverrideRate => HumanSelectionsAfterSuggestion == 0 ? null : (double)HumanOverrides / HumanSelectionsAfterSuggestion;
}

/// <summary>Live identity-resolution metrics (plan §5A.4, issue #295): auto-select rate, human-override rate and
/// false-accept count, from what <see cref="IdentityResolutionService"/> records. Read-only; reported by
/// <c>Tools/EvaluateNameResolution</c> next to the offline precision report, so the decision to enable
/// auto-select (or raise <c>Resolve:SpendThreshold</c>) can be checked against real traffic afterwards.</summary>
public static class IdentityResolutionMetrics
{
    public static LiveResolutionMetrics Compute(IEnumerable<ResolutionMetricRow> rowsInOrder)
    {
        int decisions = 0, auto = 0, eligible = 0, human = 0, afterSuggestion = 0, overrides = 0, falseAccepts = 0;
        foreach (var request in rowsInOrder.GroupBy(r => r.RequestId))
        {
            string? suggestion = null;
            foreach (var row in request)
            {
                if (row.ReasonCode == ResolutionReasonCodes.FalseAccept)
                {
                    falseAccepts++;
                    suggestion = null; // the refuted company is not a suggestion a person could have followed
                    continue;
                }
                if (row.Method == ResolutionMethod.HumanSelected)
                {
                    human++;
                    if (suggestion is not null)
                    {
                        afterSuggestion++;
                        if (!string.Equals(suggestion, row.ChosenIdentifier, StringComparison.OrdinalIgnoreCase)) overrides++;
                    }
                    suggestion = null;
                    continue;
                }
                if (row.InputIdentifier is null)
                {
                    decisions++;
                    if (row.Method == ResolutionMethod.AutoSelected) auto++;
                    if (row.AutoSelectEligible) eligible++;
                    suggestion = row.ChosenIdentifier ?? row.RecommendedIdentifier;
                }
            }
        }
        return new LiveResolutionMetrics(decisions, auto, eligible, human, afterSuggestion, overrides, falseAccepts);
    }

    public static async Task<LiveResolutionMetrics> ReadAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var command = new SqlCommand("""
            SELECT RequestId, Method, ReasonCode, InputIdentifier, ChosenIdentifier, RecommendedIdentifier, AutoSelectEligible
            FROM dbo.IdentityResolutions
            WHERE RequestId IS NOT NULL
            ORDER BY RequestId, CreatedUtc, IdentityResolutionId;
            """, connection);
        var rows = new List<ResolutionMetricRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string? Str(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            rows.Add(new ResolutionMetricRow(
                reader.GetInt64(0),
                Str(1) is { } method && Enum.TryParse<ResolutionMethod>(method, out var m) ? m : null,
                reader.GetString(2), Str(3), Str(4), Str(5), reader.GetBoolean(6)));
        }
        return Compute(rows);
    }

    public static string Format(LiveResolutionMetrics m)
    {
        static string Rate(double? r) => r is { } v ? (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a";
        var sb = new StringBuilder();
        sb.AppendLine("## Live metrics (recorded resolutions)");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Name resolutions | {m.NameDecisions:N0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Auto-selected | {m.AutoSelected:N0} ({Rate(m.AutoSelectRate)}) |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Would auto-select (eligible) | {m.AutoSelectEligible:N0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Selected by a person | {m.HumanSelections:N0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Person chose differently from the resolver | {m.HumanOverrides:N0} of {m.HumanSelectionsAfterSuggestion:N0} ({Rate(m.HumanOverrideRate)}) |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| False accepts (refuted by the reference tool) | {m.FalseAccepts:N0} |");
        return sb.ToString();
    }
}
