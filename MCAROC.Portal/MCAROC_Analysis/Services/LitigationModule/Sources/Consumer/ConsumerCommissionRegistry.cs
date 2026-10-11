using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>In-memory registry of all 731 consumer commissions in India (NCDRC, 54 SCDRC states/benches, and ~676 DCDRC districts).
/// Loaded deterministically from baked embedded resources without external disk dependencies.</summary>
public static partial class ConsumerCommissionRegistry
{
    private static readonly Lazy<RegistryData> Data = new(LoadData, LazyThreadSafetyMode.ExecutionAndPublication);

    public const int NcdrcCommissionId = 11000000;

    /// <summary>Gets the two-digit state code embedded in an e-Jagriti commission ID (e.g. 11080000 -> "08").</summary>
    public static string StateCode(int commissionId)
    {
        var str = commissionId.ToString().PadLeft(8, '0');
        return str.Length >= 4 ? str.Substring(2, 2) : "00";
    }

    /// <summary>Normalizes a name for matching (whitespace collapsed, uppercase).</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return WhitespaceRegex().Replace(name.Trim(), " ").ToUpperInvariant();
    }

    /// <summary>Retrieves a commission by its e-Jagriti commission ID.</summary>
    public static ConsumerCommission? GetById(int commissionId)
    {
        return Data.Value.ById.GetValueOrDefault(commissionId);
    }

    /// <summary>Finds a commission by its case number prefix (e.g. "DC/497", "NC", "SC/35").</summary>
    public static ConsumerCommission? FindByPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        var p = prefix.Trim().Trim('/').ToUpperInvariant();
        return Data.Value.ByPrefix.GetValueOrDefault(p);
    }

    /// <summary>Finds a commission by its display/court name (e.g. "Thane", "SCDRC BENCH JODHPUR", "NCDRC").</summary>
    public static ConsumerCommission? FindByCourtName(string courtName)
    {
        if (string.IsNullOrWhiteSpace(courtName)) return null;
        var norm = NormalizeName(courtName);

        if (Data.Value.ByName.TryGetValue(norm, out var direct))
        {
            return direct;
        }

        var compact = norm.Replace(" ", "");
        if (Data.Value.ByCompactName.TryGetValue(compact, out var compactMatch))
        {
            return compactMatch;
        }

        // Check if court name ends with district name or matches district
        foreach (var c in Data.Value.All)
        {
            if (c.DistrictName is { } dist && NormalizeName(dist) == norm)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>Resolves a selection into a commission (plan section 4.9).</summary>
    /// <param name="commission">"NCDRC", "SCDRC", or "DCDRC".</param>
    /// <param name="state">State name or bench name.</param>
    /// <param name="district">District name (for DCDRC).</param>
    public static ConsumerCommission? Find(string commission, string? state = null, string? district = null)
    {
        var comm = (commission ?? "NCDRC").Trim().ToUpperInvariant();
        if (comm == "NCDRC")
        {
            return GetById(NcdrcCommissionId);
        }

        if (string.IsNullOrWhiteSpace(state)) return null;
        var normState = NormalizeName(state);

        if (comm == "SCDRC")
        {
            if (Data.Value.ByName.TryGetValue(normState, out var scdrcMatch) && scdrcMatch.Tier == ConsumerCommissionTier.State)
            {
                return scdrcMatch;
            }
            var compactState = normState.Replace(" ", "");
            if (Data.Value.ByCompactName.TryGetValue(compactState, out var compactMatch) && compactMatch.Tier == ConsumerCommissionTier.State)
            {
                return compactMatch;
            }
            return null;
        }

        if (comm == "DCDRC")
        {
            if (string.IsNullOrWhiteSpace(district)) return null;
            var normDist = NormalizeName(district);
            var compactDist = normDist.Replace(" ", "");

            foreach (var c in Data.Value.All)
            {
                if (c.Tier != ConsumerCommissionTier.District) continue;
                if (!string.Equals(NormalizeName(c.StateName), normState, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(NormalizeName(c.StateName).Replace(" ", ""), normState.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(NormalizeName(c.DistrictName), normDist, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeName(c.DistrictName).Replace(" ", ""), compactDist, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }
        }

        return null;
    }

    /// <summary>Returns all 731 queryable consumer commissions.</summary>
    public static IReadOnlyList<ConsumerCommission> GetAll() => Data.Value.All;

    /// <summary>Returns selectable state/bench options.</summary>
    public static IReadOnlyList<ConsumerStateInfo> GetStates(string commission = "SCDRC")
    {
        var isDcdrc = string.Equals(commission, "DCDRC", StringComparison.OrdinalIgnoreCase);
        return Data.Value.States
            .Where(s => !isDcdrc || !s.IsBench)
            .ToList();
    }

    /// <summary>Returns district options for the given state.</summary>
    public static IReadOnlyList<ConsumerDistrictInfo> GetDistricts(string stateName)
    {
        var norm = NormalizeName(stateName);
        if (Data.Value.DistrictsByState.TryGetValue(norm, out var list))
        {
            return list;
        }
        var compact = norm.Replace(" ", "");
        foreach (var (key, distList) in Data.Value.DistrictsByState)
        {
            if (key.Replace(" ", "") == compact) return distList;
        }
        return [];
    }

    /// <summary>Builds the full portal case number (e.g. "DC/497/CC/599/2017") from a short number and commission ID.
    /// Safe to call on already-full numbers (idempotent).</summary>
    public static string? ToFullCaseNumber(string caseNumber, int commissionId)
    {
        if (string.IsNullOrWhiteSpace(caseNumber)) return null;
        var commission = GetById(commissionId);
        var prefix = commission?.Prefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return caseNumber.Trim();
        }

        var trimmedCase = caseNumber.Trim().Trim('/');
        var trimmedPrefix = prefix.Trim().Trim('/');

        if (trimmedCase.StartsWith(trimmedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedCase;
        }

        return $"{trimmedPrefix}/{trimmedCase}";
    }

    private sealed record RegistryData(
        IReadOnlyList<ConsumerCommission> All,
        Dictionary<int, ConsumerCommission> ById,
        Dictionary<string, ConsumerCommission> ByPrefix,
        Dictionary<string, ConsumerCommission> ByName,
        Dictionary<string, ConsumerCommission> ByCompactName,
        IReadOnlyList<ConsumerStateInfo> States,
        Dictionary<string, IReadOnlyList<ConsumerDistrictInfo>> DistrictsByState);

    private static RegistryData LoadData()
    {
        var asm = typeof(ConsumerCommissionRegistry).Assembly;

        // Load court_codes.json for prefixes
        var prefixesByCommissionId = new Dictionary<int, string>();
        using (var stream = asm.GetManifestResourceStream("MCAROC_Analysis.Services.LitigationModule.Sources.Consumer.Data.court_codes.json"))
        {
            if (stream is not null)
            {
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("codes", out var codesEl) && codesEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in codesEl.EnumerateObject())
                    {
                        if (int.TryParse(prop.Name, out var id) && prop.Value.TryGetProperty("prefix", out var pEl))
                        {
                            prefixesByCommissionId[id] = pEl.GetString()?.Trim() ?? string.Empty;
                        }
                    }
                }
            }
        }

        // Load commissions_data.json
        var all = new List<ConsumerCommission>();
        var statesList = new List<ConsumerStateInfo>();
        var districtsByState = new Dictionary<string, IReadOnlyList<ConsumerDistrictInfo>>(StringComparer.OrdinalIgnoreCase);

        using (var stream = asm.GetManifestResourceStream("MCAROC_Analysis.Services.LitigationModule.Sources.Consumer.Data.commissions_data.json"))
        {
            if (stream is not null)
            {
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                var ncdrcId = root.TryGetProperty("ncdrc_commission_id", out var nEl) ? nEl.GetInt32() : NcdrcCommissionId;
                var ncdrcPrefix = prefixesByCommissionId.GetValueOrDefault(ncdrcId, "NC");
                var ncdrc = new ConsumerCommission(
                    CommissionId: ncdrcId,
                    CommissionTypeId: 1,
                    Tier: ConsumerCommissionTier.National,
                    CommissionName: "National Consumer Disputes Redressal Commission",
                    StateName: "National",
                    DistrictName: null,
                    IsBench: false,
                    ParentState: null,
                    Prefix: ncdrcPrefix);
                all.Add(ncdrc);

                // Build parent state lookup by 2-digit state code for SCDRC
                var stateNameByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("scdrc_states", out var scdrcEl) && scdrcEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in scdrcEl.EnumerateArray())
                    {
                        var id = s.GetProperty("id").GetInt32();
                        var name = s.GetProperty("name").GetString() ?? string.Empty;
                        var isBench = s.TryGetProperty("isBench", out var bEl) && bEl.GetBoolean();
                        if (!isBench)
                        {
                            stateNameByCode[StateCode(id)] = name;
                        }
                    }

                    foreach (var s in scdrcEl.EnumerateArray())
                    {
                        var id = s.GetProperty("id").GetInt32();
                        var name = s.GetProperty("name").GetString() ?? string.Empty;
                        var isBench = s.TryGetProperty("isBench", out var bEl) && bEl.GetBoolean();
                        var code = StateCode(id);
                        var parentState = isBench ? stateNameByCode.GetValueOrDefault(code, name) : name;
                        var prefix = prefixesByCommissionId.GetValueOrDefault(id);

                        var comm = new ConsumerCommission(
                            CommissionId: id,
                            CommissionTypeId: 2,
                            Tier: ConsumerCommissionTier.State,
                            CommissionName: name,
                            StateName: parentState,
                            DistrictName: isBench ? name : null,
                            IsBench: isBench,
                            ParentState: parentState,
                            Prefix: prefix);
                        all.Add(comm);
                        statesList.Add(new ConsumerStateInfo(id, name, isBench, parentState));
                    }
                }

                // DCDRC districts
                if (root.TryGetProperty("dcdrc_districts", out var dcdrcEl) && dcdrcEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var stateProp in dcdrcEl.EnumerateObject())
                    {
                        var stateName = stateProp.Name;
                        var distList = new List<ConsumerDistrictInfo>();

                        if (stateProp.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var d in stateProp.Value.EnumerateArray())
                            {
                                var id = d.GetProperty("id").GetInt32();
                                var name = d.GetProperty("name").GetString() ?? string.Empty;
                                var prefix = prefixesByCommissionId.GetValueOrDefault(id);

                                var comm = new ConsumerCommission(
                                    CommissionId: id,
                                    CommissionTypeId: 3,
                                    Tier: ConsumerCommissionTier.District,
                                    CommissionName: name,
                                    StateName: stateName,
                                    DistrictName: name,
                                    IsBench: false,
                                    ParentState: stateName,
                                    Prefix: prefix);
                                all.Add(comm);
                                distList.Add(new ConsumerDistrictInfo(id, name, stateName));
                            }
                        }

                        districtsByState[NormalizeName(stateName)] = distList;
                    }
                }
            }
        }

        var byId = all.ToDictionary(c => c.CommissionId);
        var byPrefix = new Dictionary<string, ConsumerCommission>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in all)
        {
            if (!string.IsNullOrWhiteSpace(c.Prefix) && !byPrefix.ContainsKey(c.Prefix.Trim('/').ToUpperInvariant()))
            {
                byPrefix[c.Prefix.Trim('/').ToUpperInvariant()] = c;
            }
        }

        var byName = new Dictionary<string, ConsumerCommission>(StringComparer.OrdinalIgnoreCase);
        var byCompact = new Dictionary<string, ConsumerCommission>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in all)
        {
            var norm = NormalizeName(c.CommissionName);
            byName.TryAdd(norm, c);
            byCompact.TryAdd(norm.Replace(" ", ""), c);
        }

        return new RegistryData(all, byId, byPrefix, byName, byCompact, statesList, districtsByState);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

