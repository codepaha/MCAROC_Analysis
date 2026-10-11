using MCAROC_Analysis.Services.LitigationModule.Normalisation;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Registry and resolver for numeric DRT and DRAT case type IDs.</summary>
public static class DrtCaseTypeRegistry
{
    private static readonly Dictionary<string, int> DrtTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["original application"] = 1, ["original application-"] = 1, ["oa"] = 1, ["oa-"] = 1, ["inoa"] = 1,
        ["review application"] = 2, ["review application-"] = 2, ["ra"] = 2,
        ["misc application"] = 3, ["misc application-"] = 3, ["miscellaneous application"] = 3, ["ma"] = 3, ["ma-"] = 3, ["ia"] = 3, ["ia-"] = 3,
        ["appeal"] = 4, ["appeal-"] = 4, ["regular appeal"] = 4, ["regular appeal-"] = 4,
        ["ura"] = 5,
        ["transfer application"] = 6, ["transfer application-"] = 6, ["ta"] = 6, ["ta-"] = 6,
        ["securitization application"] = 7, ["securitization application-"] = 7, ["sa"] = 7, ["sa-"] = 7, ["insa"] = 7, ["tsa"] = 7, ["tsa-"] = 7,
        ["air"] = 8,
        ["counter claim"] = 10, ["cc"] = 10,
        ["transfer securitization application"] = 11, ["transfer securitization application-"] = 11,
        ["transfer recovery certificate"] = 12,
        ["execution"] = 13, ["ex"] = 13, ["ex-"] = 13, ["exa"] = 13, ["oaex"] = 13,
        ["chamber appeal"] = 14, ["ca"] = 14, ["ca-"] = 14,
        ["transfer miscellaneous application"] = 15, ["tma"] = 15, ["tma-"] = 15,
        ["misc appeal"] = 17, ["misc appeal-"] = 17,
        ["transfer appeal"] = 21, ["transfer appeal-"] = 21,
        ["transfer counter claim"] = 22,
        ["transfer chamber appeal"] = 23,
        ["transfer execution"] = 25, ["tex"] = 25,
        ["ibc-c"] = 100,
        ["ibc-a"] = 101
    };

    private static readonly Dictionary<string, int> DratTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["regular appeal"] = 4, ["appeal"] = 4,
        ["misc appeal"] = 13, ["miscellaneous appeal"] = 13,
        ["ia"] = 14,
        ["review application"] = 2, ["ra"] = 2,
        ["misc application"] = 3, ["miscellaneous application"] = 3, ["application"] = 3,
        ["chamber appeal"] = 12, ["ca"] = 12,
        ["transfer petition"] = 15,
        ["transfer regular appeal"] = 5,
        ["transfer misc appeal"] = 6,
        ["caveat filing"] = 20
    };

    private static readonly Dictionary<string, int[]> DrtAlternates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tsa"] = [11],
        ["appeal"] = [21, 17]
    };

    private static readonly Dictionary<string, int[]> DratAlternates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["appeal"] = [13],
        ["regular appeal"] = [13],
        ["misc appeal"] = [4]
    };

    public static bool IsRcTrc(string? caseType)
    {
        if (string.IsNullOrWhiteSpace(caseType)) return false;
        var clean = caseType.Trim().ToUpperInvariant().TrimEnd('-');
        return clean is "RC" or "TRC" or "TRANSFER RECOVERY CERTIFICATE";
    }

    public static int? GetCaseTypeId(string? caseType, bool isDrat = false)
    {
        if (string.IsNullOrWhiteSpace(caseType)) return null;
        var clean = caseType.Trim().ToLowerInvariant().TrimEnd('-').Trim();
        var map = isDrat ? DratTypes : DrtTypes;

        if (map.TryGetValue(clean, out var id)) return id;

        // Try code resolution
        var code = CaseTypeCodes.Resolve(caseType);
        if (code is not null && map.TryGetValue(code.ToLowerInvariant(), out var codeId))
            return codeId;

        return null;
    }

    public static IReadOnlyList<int> GetCandidates(string? caseType, bool isDrat = false)
    {
        if (string.IsNullOrWhiteSpace(caseType)) return [];
        var clean = caseType.Trim().ToLowerInvariant().TrimEnd('-').Trim();
        var first = GetCaseTypeId(caseType, isDrat);
        var alts = (isDrat ? DratAlternates : DrtAlternates).GetValueOrDefault(clean) ?? [];

        var result = new List<int>();
        if (first.HasValue) result.Add(first.Value);
        foreach (var alt in alts)
        {
            if (!result.Contains(alt)) result.Add(alt);
        }
        return result;
    }
}

