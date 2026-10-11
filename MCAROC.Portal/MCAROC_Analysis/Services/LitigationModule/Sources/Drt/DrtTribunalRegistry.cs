namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Registry of all 44 authoritative DRT and DRAT tribunals with their states, districts, and name aliases.</summary>
public static class DrtTribunalRegistry
{
    private static readonly DrtTribunal[] AllTribunals =
    [
        // Delhi (DRT 1..3, DRAT Delhi)
        new(1, "Debts Recovery Tribunal Delhi (DRT 1)", false, "Delhi", "New Delhi", "DRT 1"),
        new(2, "Debts Recovery Tribunal Delhi (DRT 2)", false, "Delhi", "New Delhi", "DRT 2"),
        new(3, "Debts Recovery Tribunal Delhi (DRT 3)", false, "Delhi", "New Delhi", "DRT 3"),
        new(100, "Debts Recovery Appellate Tribunal Delhi", true, "Delhi", "New Delhi", "DRAT Delhi"),

        // Gujarat (Ahmedabad 1..2)
        new(9, "Debts Recovery Tribunal Ahmedabad (DRT 1)", false, "Gujarat", "Ahmedabad", "DRT 1"),
        new(10, "Debts Recovery Tribunal Ahmedabad (DRT 2)", false, "Gujarat", "Ahmedabad", "DRT 2"),

        // Uttar Pradesh (Allahabad, Lucknow, DRAT Allahabad)
        new(11, "Debts Recovery Tribunal Allahabad", false, "Uttar Pradesh", "Allahabad", "Allahabad"),
        new(29, "Debts Recovery Tribunal Lucknow", false, "Uttar Pradesh", "Lucknow", "Lucknow"),
        new(101, "Debts Recovery Appellate Tribunal Allahabad", true, "Uttar Pradesh", "Allahabad", "DRAT Allahabad"),

        // Maharashtra (Aurangabad, Mumbai 1..3, Nagpur, Pune, DRAT Mumbai)
        new(12, "Debts Recovery Tribunal Aurangabad", false, "Maharashtra", "Aurangabad", "Aurangabad"),
        new(31, "Debts Recovery Tribunal Mumbai (DRT 1)", false, "Maharashtra", "Mumbai", "DRT 1"),
        new(32, "Debts Recovery Tribunal Mumbai (DRT 2)", false, "Maharashtra", "Mumbai", "DRT 2"),
        new(33, "Debts Recovery Tribunal Mumbai (DRT 3)", false, "Maharashtra", "Mumbai", "DRT 3"),
        new(34, "Debts Recovery Tribunal Nagpur", false, "Maharashtra", "Nagpur", "Nagpur"),
        new(36, "Debts Recovery Tribunal Pune", false, "Maharashtra", "Pune", "Pune"),
        new(103, "Debts Recovery Appellate Tribunal Mumbai", true, "Maharashtra", "Mumbai", "DRAT Mumbai"),

        // Karnataka (Bangalore 1..2)
        new(13, "Debts Recovery Tribunal Bangalore (DRT 1)", false, "Karnataka", "Bengaluru", "DRT 1"),
        new(39, "Debts Recovery Tribunal Bangalore (DRT 2)", false, "Karnataka", "Bengaluru", "DRT 2"),

        // Chandigarh (Chandigarh 1..3)
        new(14, "Debts Recovery Tribunal Chandigarh (DRT 1)", false, "Chandigarh", "Chandigarh", "DRT 1"),
        new(15, "Debts Recovery Tribunal Chandigarh (DRT 2)", false, "Chandigarh", "Chandigarh", "DRT 2"),
        new(40, "Debts Recovery Tribunal Chandigarh (DRT 3)", false, "Chandigarh", "Chandigarh", "DRT 3"),

        // Tamil Nadu (Chennai 1..3, Coimbatore, Madurai, DRAT Chennai)
        new(16, "Debts Recovery Tribunal Chennai (DRT 1)", false, "Tamil Nadu", "Chennai", "DRT 1"),
        new(17, "Debts Recovery Tribunal Chennai (DRT 2)", false, "Tamil Nadu", "Chennai", "DRT 2"),
        new(18, "Debts Recovery Tribunal Chennai (DRT 3)", false, "Tamil Nadu", "Chennai", "DRT 3"),
        new(19, "Debts Recovery Tribunal Coimbatore", false, "Tamil Nadu", "Coimbatore", "Coimbatore"),
        new(30, "Debts Recovery Tribunal Madurai", false, "Tamil Nadu", "Madurai", "Madurai"),
        new(102, "Debts Recovery Appellate Tribunal Chennai", true, "Tamil Nadu", "Chennai", "DRAT Chennai"),

        // Odisha (Cuttack)
        new(20, "Debts Recovery Tribunal Cuttack", false, "Odisha", "Cuttack", "Cuttack"),

        // Kerala (Ernakulam 1..2)
        new(21, "Debts Recovery Tribunal Ernakulam (DRT 1)", false, "Kerala", "Ernakulam", "DRT 1"),
        new(42, "Debts Recovery Tribunal Ernakulam (DRT 2)", false, "Kerala", "Ernakulam", "DRT 2"),

        // Assam / North-East (Guwahati)
        new(22, "Debts Recovery Tribunal Guwahati", false, "Assam", "Guwahati", "Guwahati"),

        // Telangana / Andhra (Hyderabad 1..2, Visakhapatnam)
        new(23, "Debts Recovery Tribunal Hyderabad (DRT 1)", false, "Telangana", "Hyderabad", "DRT 1"),
        new(43, "Debts Recovery Tribunal Hyderabad (DRT 2)", false, "Telangana", "Hyderabad", "DRT 2"),
        new(38, "Debts Recovery Tribunal Visakhapatnam", false, "Andhra Pradesh", "Visakhapatnam", "Visakhapatnam"),

        // Madhya Pradesh (Jabalpur)
        new(24, "Debts Recovery Tribunal Jabalpur", false, "Madhya Pradesh", "Jabalpur", "Jabalpur"),

        // Rajasthan (Jaipur)
        new(25, "Debts Recovery Tribunal Jaipur", false, "Rajasthan", "Jaipur", "Jaipur"),

        // West Bengal (Kolkata 1..3, Siliguri, DRAT Kolkata)
        new(26, "Debts Recovery Tribunal Kolkata (DRT 1)", false, "West Bengal", "Kolkata", "DRT 1"),
        new(27, "Debts Recovery Tribunal Kolkata (DRT 2)", false, "West Bengal", "Kolkata", "DRT 2"),
        new(28, "Debts Recovery Tribunal Kolkata (DRT 3)", false, "West Bengal", "Kolkata", "DRT 3"),
        new(44, "Debts Recovery Tribunal Siliguri", false, "West Bengal", "Siliguri", "Siliguri"),
        new(104, "Debts Recovery Appellate Tribunal Kolkata", true, "West Bengal", "Kolkata", "DRAT Kolkata"),

        // Bihar (Patna)
        new(35, "Debts Recovery Tribunal Patna", false, "Bihar", "Patna", "Patna"),

        // Jharkhand (Ranchi)
        new(37, "Debts Recovery Tribunal Ranchi", false, "Jharkhand", "Ranchi", "Ranchi"),

        // Uttarakhand (Dehradun)
        new(41, "Debts Recovery Tribunal Dehradun", false, "Uttarakhand", "Dehradun", "Dehradun")
    ];

    private static readonly Dictionary<int, DrtTribunal> ById = AllTribunals.ToDictionary(t => t.Id);

    private static readonly Dictionary<string, int> NameToId = new(StringComparer.OrdinalIgnoreCase)
    {
        // Delhi
        ["debts recovery tribunal delhi(drt 1)"] = 1,
        ["debts recovery tribunal delhi (drt 1)"] = 1,
        ["debts recovery tribunal delhi drt 1"] = 1,
        ["debts recovery tribunal delhi(drt 2)"] = 2,
        ["debts recovery tribunal delhi (drt 2)"] = 2,
        ["debts recovery tribunal delhi drt 2"] = 2,
        ["debts recovery tribunal delhi(drt 3)"] = 3,
        ["debts recovery tribunal delhi (drt 3)"] = 3,
        ["debts recovery tribunal delhi drt 3"] = 3,
        ["debts recovery tribunal delhi"] = 1,
        ["drt delhi"] = 1,
        ["drt 1 delhi"] = 1,
        ["drt 2 delhi"] = 2,
        ["drt 3 delhi"] = 3,

        // DRAT Delhi
        ["debt recovery appellate tribunal - delhi"] = 100,
        ["debt recovery appellate tribunal delhi"] = 100,
        ["debts recovery appellate tribunal delhi"] = 100,
        ["drat delhi"] = 100,

        // Chandigarh
        ["debts recovery tribunal chandigarh (drt 1)"] = 14,
        ["debts recovery tribunal chandigarh(drt 1)"] = 14,
        ["debts recovery tribunal chandigarh (drt 2)"] = 15,
        ["debts recovery tribunal chandigarh(drt 2)"] = 15,
        ["debts recovery tribunal chandigarh (drt 3)"] = 40,
        ["debts recovery tribunal chandigarh(drt 3)"] = 40,
        ["debts recovery tribunal chandigarh"] = 14,
        ["drt chandigarh"] = 14,

        // Jaipur / Rajasthan
        ["debts recovery tribunal jaipur"] = 25,
        ["drt jaipur"] = 25,

        // Allahabad / Lucknow / UP
        ["debts recovery tribunal allahabad"] = 11,
        ["drt allahabad"] = 11,
        ["debts recovery tribunal lucknow"] = 29,
        ["drt lucknow"] = 29,
        ["debt recovery appellate tribunal - allahabad"] = 101,
        ["debt recovery appellate tribunal allahabad"] = 101,
        ["debts recovery appellate tribunal allahabad"] = 101,
        ["drat allahabad"] = 101,

        // Patna / Bihar
        ["debts recovery tribunal patna"] = 35,
        ["drt patna"] = 35,

        // Guwahati / Assam
        ["debts recovery tribunal guwahati"] = 22,
        ["drt guwahati"] = 22,

        // Kolkata / Siliguri / West Bengal
        ["debts recovery tribunal kolkata(drt 1)"] = 26,
        ["debts recovery tribunal kolkata (drt 1)"] = 26,
        ["debts recovery tribunal kolkata(drt 2)"] = 27,
        ["debts recovery tribunal kolkata (drt 2)"] = 27,
        ["debts recovery tribunal kolkata(drt 3)"] = 28,
        ["debts recovery tribunal kolkata (drt 3)"] = 28,
        ["debts recovery tribunal kolkata"] = 26,
        ["drt kolkata"] = 26,
        ["debts recovery tribunal siliguri"] = 44,
        ["drt siliguri"] = 44,
        ["debt recovery appellate tribunal - kolkata"] = 104,
        ["debt recovery appellate tribunal kolkata"] = 104,
        ["debts recovery appellate tribunal kolkata"] = 104,
        ["drat kolkata"] = 104,

        // Ranchi / Jharkhand
        ["debts recovery tribunal ranchi"] = 37,
        ["drt ranchi"] = 37,

        // Cuttack / Odisha
        ["debts recovery tribunal cuttack"] = 20,
        ["drt cuttack"] = 20,

        // Ahmedabad / Gujarat
        ["debts recovery tribunal ahmedabad(drt 1)"] = 9,
        ["debts recovery tribunal ahmedabad (drt 1)"] = 9,
        ["debts recovery tribunal ahmedabad(drt 2)"] = 10,
        ["debts recovery tribunal ahmedabad (drt 2)"] = 10,
        ["debts recovery tribunal ahmedabad"] = 9,
        ["drt ahmedabad"] = 9,

        // Jabalpur / MP
        ["debts recovery tribunal jabalpur"] = 24,
        ["drt jabalpur"] = 24,

        // Mumbai / Pune / Nagpur / Aurangabad / Maharashtra
        ["debts recovery tribunal mumbai (drt 1)"] = 31,
        ["debts recovery tribunal mumbai(drt 1)"] = 31,
        ["debts recovery tribunal mumbai (drt 2)"] = 32,
        ["debts recovery tribunal mumbai(drt 2)"] = 32,
        ["debts recovery tribunal mumbai (drt 3)"] = 33,
        ["debts recovery tribunal mumbai(drt 3)"] = 33,
        ["debts recovery tribunal mumbai"] = 31,
        ["drt mumbai"] = 31,
        ["debts recovery tribunal nagpur"] = 34,
        ["drt nagpur"] = 34,
        ["debts recovery tribunal pune"] = 36,
        ["drt pune"] = 36,
        ["debts recovery tribunal aurangabad"] = 12,
        ["drt aurangabad"] = 12,
        ["debt recovery appellate tribunal - mumbai"] = 103,
        ["debt recovery appellate tribunal mumbai"] = 103,
        ["debts recovery appellate tribunal mumbai"] = 103,
        ["drat mumbai"] = 103,

        // Hyderabad / Visakhapatnam / Telangana / Andhra Pradesh
        ["debts recovery tribunal hyderabad(drt 1)"] = 23,
        ["debts recovery tribunal hyderabad (drt 1)"] = 23,
        ["debts recovery tribunal hyderabad(drt 2)"] = 43,
        ["debts recovery tribunal hyderabad (drt 2)"] = 43,
        ["debts recovery tribunal hyderabad"] = 23,
        ["drt hyderabad"] = 23,
        ["debts recovery tribunal vishakhapatnam"] = 38,
        ["debts recovery tribunal visakhapatnam"] = 38,
        ["drt visakhapatnam"] = 38,

        // Ernakulam / Kerala
        ["debts recovery tribunal ernakulam(drt 1)"] = 21,
        ["debts recovery tribunal ernakulam (drt 1)"] = 21,
        ["debts recovery tribunal ernakulam(drt 2)"] = 42,
        ["debts recovery tribunal ernakulam (drt 2)"] = 42,
        ["debts recovery tribunal ernakulam"] = 21,
        ["drt ernakulam"] = 21,

        // Chennai / Coimbatore / Madurai / Tamil Nadu
        ["debts recovery tribunal chennai (drt 1)"] = 16,
        ["debts recovery tribunal chennai(drt 1)"] = 16,
        ["debts recovery tribunal chennai (drt 2)"] = 17,
        ["debts recovery tribunal chennai(drt 2)"] = 17,
        ["debts recovery tribunal chennai (drt 3)"] = 18,
        ["debts recovery tribunal chennai(drt 3)"] = 18,
        ["debts recovery tribunal chennai"] = 16,
        ["drt chennai"] = 16,
        ["debts recovery tribunal coimbatore"] = 19,
        ["drt coimbatore"] = 19,
        ["debts recovery tribunal madurai"] = 30,
        ["drt madurai"] = 30,
        ["debt recovery appellate tribunal - chennai"] = 102,
        ["debt recovery appellate tribunal chennai"] = 102,
        ["debts recovery appellate tribunal chennai"] = 102,
        ["drat chennai"] = 102,

        // Bangalore / Bengaluru / Karnataka
        ["debts recovery tribunal bangalore (drt 1)"] = 13,
        ["debts recovery tribunal bangalore(drt 1)"] = 13,
        ["debts recovery tribunal bangalore (drt 2)"] = 39,
        ["debts recovery tribunal bangalore(drt 2)"] = 39,
        ["debts recovery tribunal bangalore"] = 13,
        ["debts recovery tribunal bengaluru (drt 1)"] = 13,
        ["debts recovery tribunal bengaluru (drt 2)"] = 39,
        ["drt bangalore"] = 13,
        ["drt bengaluru"] = 13,

        // Dehradun / Uttarakhand
        ["debts recovery tribunal dehradun"] = 41,
        ["drt dehradun"] = 41
    };

    public static IReadOnlyList<DrtTribunal> GetAll() => AllTribunals;

    public static DrtTribunal? TryGetById(int id) => ById.GetValueOrDefault(id);

    public static bool IsDrat(int tribunalId) => tribunalId >= 100;

    public static bool IsDrat(string? tribunalName)
    {
        if (string.IsNullOrWhiteSpace(tribunalName)) return false;
        var lower = tribunalName.ToLowerInvariant();
        return lower.Contains("appellate") || lower.Contains("drat");
    }

    /// <summary>Resolves tribunal by raw name string, using exact mapping or substring fallback.</summary>
    public static DrtTribunal? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = name.Trim().ToLowerInvariant();

        if (NameToId.TryGetValue(clean, out var id) && ById.TryGetValue(id, out var tribunal))
            return tribunal;

        // Longest matching key substring (never reverse substring, to avoid bare "drt" false matching)
        string? bestKey = null;
        var bestLen = 0;
        foreach (var key in NameToId.Keys)
        {
            if (clean.Contains(key, StringComparison.OrdinalIgnoreCase) && key.Length > bestLen)
            {
                bestKey = key;
                bestLen = key.Length;
            }
        }

        if (bestKey is not null && NameToId.TryGetValue(bestKey, out var matchedId) && ById.TryGetValue(matchedId, out var matchedTribunal))
            return matchedTribunal;

        return null;
    }
}

