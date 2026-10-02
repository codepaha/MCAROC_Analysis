using System.Globalization;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.Excel.Parsers;

public enum PropertyAssetClass { Immovable, Movable }

public enum PropertyKind
{
    Premises, Land, BuildingOrProject, Parking, CurrentAssets, BookDebtsReceivables,
    PlantAndMachinery, IntangiblesGoodwill, Shares, UncalledCapital, Vehicle
}

/// <summary><see cref="LargerLand"/> is the parent holding a charged parcel "forms part of" — context, not the
/// security itself.</summary>
public enum AreaBasis { Carpet, BuiltUp, SuperBuiltUp, Fsi, Plot, Land, LargerLand, Unspecified }

/// <summary>One measurement, in both units. A source that states the same area twice ("27864.63 sq ft eq. to
/// 2588.68 sq m") yields one area, carrying the stated sq m and sq ft rather than a converted one.</summary>
public sealed record NormalizedArea(decimal SquareMetres, decimal SquareFeet, AreaBasis Basis);

public sealed record NormalizedUnit(string UnitNumber, string? Floor, string? Building);

/// <param name="Scheme">CTS, Survey, Plot, Gat or Khasra.</param>
/// <param name="Qualifier">"New" or "Old" when the source says so.</param>
public sealed record SurveyNumberGroup(string Scheme, string? Qualifier, IReadOnlyList<string> Numbers);

public sealed record NormalizedLocation(
    IReadOnlyList<string> Localities, string? Village, string? Taluka, string? District, string? City, string? State, string? Pin)
{
    public bool IsEmpty => Localities.Count == 0 && Village is null && Taluka is null && District is null && City is null && State is null && Pin is null;
}

/// <summary>The structured reading of one charge event's "Particulars of Property Charged" (the charge report's
/// PROPERTY PARTICULARS column). Every field is only ever filled from wording actually present in the source —
/// anything not stated stays empty/null, and the raw text is always kept alongside it in the UI.</summary>
public sealed record NormalizedPropertyParticulars(
    IReadOnlyList<PropertyAssetClass> AssetClasses,
    IReadOnlyList<PropertyKind> Kinds,
    IReadOnlyList<NormalizedUnit> Units,
    IReadOnlyList<string> BuildingsOrProjects,
    IReadOnlyList<NormalizedArea> Areas,
    int? ParkingSpaces,
    IReadOnlyList<string> ParkingSpaceNumbers,
    IReadOnlyList<SurveyNumberGroup> SurveyNumbers,
    NormalizedLocation Location,
    IReadOnlyList<string> NamedEntities,
    bool DetailsOnlyInReferencedDocument)
{
    public static readonly NormalizedPropertyParticulars Empty = new([], [], [], [], [], null, [], [],
        new NormalizedLocation([], null, null, null, null, null, null), [], false);

    public bool HasContent => AssetClasses.Count > 0 || Kinds.Count > 0 || Units.Count > 0 || Areas.Count > 0
        || ParkingSpaces is not null || SurveyNumbers.Count > 0 || !Location.IsEmpty || NamedEntities.Count > 0 || DetailsOnlyInReferencedDocument;
}

/// <summary>Deterministic regex normaliser for charge-report property particulars — the same no-I/O, never-guess
/// style as <see cref="ChargeSecurityClassifier"/>. Built against real MCA wording (charge-report exports):
/// run-on paragraphs mixing a mortgaged unit, its parking, the underlying land and a hypothecation clause; areas
/// restated in a second unit; CTS lists with ranges and typos ("CST", "C.T. S", "Nos51"); location tokens strung
/// after "situated at". Stateless and cheap, so it runs at render time over every already-ingested charge.</summary>
public static partial class PropertyParticularsNormalizer
{
    private const decimal SqFtPerSqM = 10.7639104167097m;
    private const decimal SqMPerAcre = 4046.8564224m;

    public static NormalizedPropertyParticulars Normalize(string? propertyParticulars, string? propertyType = null)
    {
        var raw = propertyParticulars?.Trim();
        if (string.IsNullOrEmpty(raw) || raw == "-") return NormalizedPropertyParticulars.Empty;

        var text = Whitespace().Replace(raw, " ");
        var lower = text.ToLowerInvariant();
        var type = (propertyType ?? string.Empty).ToLowerInvariant();

        var kinds = Kinds(lower, type);
        var units = Units(text);
        var buildings = Buildings(text);
        var areas = Areas(text);
        var (parking, parkingNumbers) = Parking(text);
        var surveys = SurveyNumbers(text);
        var location = Location(text);
        var entities = NamedEntities(text);

        var referenceOnly = text.Length <= 240 && ReferenceOnly().IsMatch(text)
            && units.Count == 0 && areas.Count == 0 && surveys.Count == 0 && location.IsEmpty;

        return new NormalizedPropertyParticulars(AssetClasses(kinds, lower, type), kinds, units, buildings, areas,
            parking, parkingNumbers, surveys, location, entities, referenceOnly);
    }

    public static string Label(PropertyKind kind) => kind switch
    {
        PropertyKind.Premises => "Premises / unit",
        PropertyKind.BuildingOrProject => "Building / project",
        PropertyKind.CurrentAssets => "Current assets / stock",
        PropertyKind.BookDebtsReceivables => "Book debts / receivables",
        PropertyKind.PlantAndMachinery => "Plant & machinery",
        PropertyKind.IntangiblesGoodwill => "Intangibles / goodwill",
        PropertyKind.UncalledCapital => "Uncalled capital",
        _ => kind.ToString()
    };

    public static string Label(AreaBasis basis) => basis switch
    {
        AreaBasis.BuiltUp => "built-up",
        AreaBasis.SuperBuiltUp => "super built-up",
        AreaBasis.Fsi => "FSI",
        AreaBasis.LargerLand => "larger land (parent parcel)",
        AreaBasis.Unspecified => "area",
        _ => basis.ToString().ToLowerInvariant()
    };

    public static string Describe(NormalizedUnit u) =>
        string.Join(", ", new[] { $"Unit {u.UnitNumber}", u.Floor is null ? null : $"{u.Floor} floor", u.Building }.Where(x => x is not null));

    public static string Describe(NormalizedArea a) =>
        string.Create(CultureInfo.InvariantCulture, $"{a.SquareMetres:#,0.##} sq m ({a.SquareFeet:#,0.##} sq ft) {Label(a.Basis)}");

    public static string Describe(SurveyNumberGroup g) =>
        $"{(g.Qualifier is null ? "" : g.Qualifier + " ")}{g.Scheme} {string.Join(", ", g.Numbers)}";

    public static string Describe(NormalizedLocation l) =>
        string.Join(", ", l.Localities
            .Concat(new[] { l.Village is null ? null : $"Village {l.Village}", l.Taluka is null ? null : $"Taluka {l.Taluka}",
                l.District is null ? null : $"{l.District} District", l.City, l.State, l.Pin }.OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase));

    // ── Kinds and asset class ──────────────────────────────────────────────────────────────────────────

    private static readonly (Regex Pattern, PropertyKind Kind)[] KindRules =
    [
        (new(@"parcel\s+of\s+premises|\bpremises\s+(adm|admeasuring|bearing|known)|\boffice\s+premises|\b(unit|flat|shop|office)\s*no\b|\bapartment\b", RegexOptions.Compiled), PropertyKind.Premises),
        (new(@"\bland\b|\bplot\s*no|\bacres?\b|\bsurvey\s*no|\bc\.?\s*t\.?\s*s\b|\bcst\s*no|\barazi\b|\bgata?\b|\bkhata\b", RegexOptions.Compiled), PropertyKind.Land),
        (new(@"\bbuilding\b|\bbldg\b|\bproject\b|\bstructures?\b", RegexOptions.Compiled), PropertyKind.BuildingOrProject),
        (new(@"\bparking\b", RegexOptions.Compiled), PropertyKind.Parking),
        (new(@"current\s*assets?|\bstocks?\b|raw\s*materials?|finished\s*goods|\binventor(y|ies)\b", RegexOptions.Compiled), PropertyKind.CurrentAssets),
        (new(@"book[\s-]*debts?|receivables?", RegexOptions.Compiled), PropertyKind.BookDebtsReceivables),
        // "consumable stores and spares not relating to plant and machinery" is a current-asset carve-out, not P&M.
        (new(@"(?<!relating to )plant\s*(and|&)\s*machinery", RegexOptions.Compiled), PropertyKind.PlantAndMachinery),
        (new(@"goodwill|trade\s*marks?|patents?|intellectual\s*property|intangible", RegexOptions.Compiled), PropertyKind.IntangiblesGoodwill),
        (new(@"\bshares?\s+of\b|pledge\s+of\s+shares|share\s+pledge", RegexOptions.Compiled), PropertyKind.Shares),
        (new(@"uncalled\s*(share\s*)?capital", RegexOptions.Compiled), PropertyKind.UncalledCapital),
        (new(@"\bvehicles?\b", RegexOptions.Compiled), PropertyKind.Vehicle),
    ];

    private static List<PropertyKind> Kinds(string lower, string type)
    {
        var both = lower + " | " + type;
        return KindRules.Where(r => r.Pattern.IsMatch(both)).Select(r => r.Kind).ToList();
    }

    private static readonly PropertyKind[] ImmovableKinds = [PropertyKind.Premises, PropertyKind.Land, PropertyKind.BuildingOrProject, PropertyKind.Parking];
    private static readonly PropertyKind[] MovableKinds =
        [PropertyKind.CurrentAssets, PropertyKind.BookDebtsReceivables, PropertyKind.PlantAndMachinery, PropertyKind.IntangiblesGoodwill,
         PropertyKind.Shares, PropertyKind.UncalledCapital, PropertyKind.Vehicle];

    private static List<PropertyAssetClass> AssetClasses(List<PropertyKind> kinds, string lower, string type)
    {
        var classes = new List<PropertyAssetClass>();
        if (type.Contains("immovable") || lower.Contains("immovable") || lower.Contains("immoveable") || lower.Contains("mortgage")
            || kinds.Any(ImmovableKinds.Contains))
            classes.Add(PropertyAssetClass.Immovable);
        if (MovableWord().IsMatch(type) || MovableWord().IsMatch(lower) || type.Contains("book debts") || lower.Contains("hypothecation")
            || kinds.Any(MovableKinds.Contains))
            classes.Add(PropertyAssetClass.Movable);
        return classes;
    }

    // ── Units and buildings ────────────────────────────────────────────────────────────────────────────

    private static List<NormalizedUnit> Units(string text)
    {
        var floor = FloorPattern().Match(text) is { Success: true } f ? Ordinal(f.Groups["n"].Value) : null;
        var building = Buildings(text).FirstOrDefault();
        return UnitPattern().Matches(text)
            .Select(m => Regex.Replace(m.Groups["no"].Value, @"\s+", "").ToUpperInvariant())
            .Distinct()
            .Select(no => new NormalizedUnit(no, floor, building))
            .ToList();
    }

    private static List<string> Buildings(string text)
    {
        var names = new List<string>();
        foreach (Match m in BuildingPattern().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim(' ', '"', '“', '”', ',', '.');
            if (name.Length >= 3 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    private static string Ordinal(string n)
    {
        var i = int.Parse(n, CultureInfo.InvariantCulture);
        var suffix = (i % 100) is 11 or 12 or 13 ? "th" : (i % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return i + suffix;
    }

    // ── Areas ─────────────────────────────────────────────────────────────────────────────────────────

    private enum AreaUnit { SqFt, SqM, Acre }

    private sealed record Measurement(int Start, int End, decimal Value, AreaUnit Unit);

    private static List<NormalizedArea> Areas(string text)
    {
        var measurements = AreaPattern().Matches(text)
            .Select(m => new Measurement(m.Index, m.Index + m.Length,
                decimal.Parse(m.Groups["v"].Value.Replace(",", ""), CultureInfo.InvariantCulture),
                m.Groups["ft"].Success ? AreaUnit.SqFt : m.Groups["ac"].Success ? AreaUnit.Acre : AreaUnit.SqM))
            .ToList();

        // Join restatements of one area ("X sq ft eq. to Y sq m", "X acres i.e. equivalent to Y sq m").
        var groups = new List<List<Measurement>>();
        foreach (var m in measurements)
        {
            if (groups.Count > 0 && Equivalence().IsMatch(text[groups[^1][^1].End..m.Start]) && groups[^1].All(x => x.Unit != m.Unit))
                groups[^1].Add(m);
            else
                groups.Add([m]);
        }

        var areas = new List<NormalizedArea>();
        foreach (var g in groups)
        {
            var sqm = g.FirstOrDefault(x => x.Unit == AreaUnit.SqM)?.Value
                ?? (g.FirstOrDefault(x => x.Unit == AreaUnit.SqFt) is { } ft ? ft.Value / SqFtPerSqM : g[0].Value * SqMPerAcre);
            var sqft = g.FirstOrDefault(x => x.Unit == AreaUnit.SqFt)?.Value ?? sqm * SqFtPerSqM;
            var area = new NormalizedArea(decimal.Round(sqm, 2), decimal.Round(sqft, 2), Basis(text, g[0].Start, g[^1].End));
            // The same parcel is often restated later in one paragraph; keep each (size, basis) once.
            if (!areas.Any(a => a.Basis == area.Basis && Math.Abs(a.SquareMetres - area.SquareMetres) < 1m)) areas.Add(area);
        }
        return areas;
    }

    private static AreaBasis Basis(string text, int start, int end)
    {
        var after = text[end..Math.Min(text.Length, end + 40)].ToLowerInvariant();
        var before = text[Math.Max(0, start - 80)..start].ToLowerInvariant();
        if (after.Contains("carpet") || before.EndsWith("carpet area of ") || before.Contains("carpet area admeasuring")) return AreaBasis.Carpet;
        if (after.Contains("super built") || before.Contains("super built")) return AreaBasis.SuperBuiltUp;
        if (after.Contains("built") || before.Contains("built up") || before.Contains("built-up")) return AreaBasis.BuiltUp;
        if (after.Contains("fsi") || after.Contains("fsl") || before.TrimEnd().EndsWith("fsi")) return AreaBasis.Fsi;
        // Only the clause closest to the number decides land vs plot — "Plot No 75A admeasuring 900.3 sq mts".
        var clause = before[(before.LastIndexOfAny([',', ';', '(']) + 1)..];
        if (clause.Contains("plot")) return AreaBasis.Plot;
        if (clause.Contains("larger")) return AreaBasis.LargerLand;
        if (clause.Contains("land") || after.Contains("land") || after.TrimStart().StartsWith("out of")) return AreaBasis.Land;
        return AreaBasis.Unspecified;
    }

    // ── Parking ───────────────────────────────────────────────────────────────────────────────────────

    private static (int? Count, List<string> Numbers) Parking(string text)
    {
        var counts = ParkingCount().Matches(text).Select(m => int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)).ToList();
        if (counts.Count == 0) return (null, []);

        // "43 car parking space out of which 22 … and 21 …": the total is the largest count stated.
        var start = text.IndexOf("parking", StringComparison.OrdinalIgnoreCase);
        var stop = ParkingSectionEnd().Match(text, start) is { Success: true } e ? e.Index : text.Length;
        var numbers = new List<string>();
        foreach (Match m in ParkingNumber().Matches(text[start..stop]))
        {
            var prefix = m.Groups["p"].Value.ToUpperInvariant();
            var label = m.Groups["to"].Success ? $"{prefix}-{m.Groups["a"].Value}–{prefix}-{m.Groups["to"].Value}" : $"{prefix}-{m.Groups["a"].Value}";
            if (!numbers.Contains(label)) numbers.Add(label);
        }
        return (counts.Max(), numbers);
    }

    // ── Survey / CTS / plot numbers ───────────────────────────────────────────────────────────────────

    private static List<SurveyNumberGroup> SurveyNumbers(string text)
    {
        var groups = new List<SurveyNumberGroup>();
        void Add(string scheme, string? qualifier, IEnumerable<string> numbers)
        {
            var list = numbers.Select(NormalizeNumber).Where(n => n.Length > 0).ToList();
            if (list.Count == 0) return;
            var existing = groups.FindIndex(g => g.Scheme == scheme && g.Qualifier == qualifier);
            if (existing < 0) groups.Add(new SurveyNumberGroup(scheme, qualifier, CompressRanges(list.Distinct().ToList())));
            else groups[existing] = groups[existing] with { Numbers = CompressRanges(ExpandRanges(groups[existing].Numbers).Concat(list).Distinct().ToList()) };
        }

        foreach (Match m in CtsPattern().Matches(text))
            Add("CTS", Qualifier(m.Groups["q"].Value), SplitNumberList(m.Groups["list"].Value));
        foreach (Match m in SurveyPattern().Matches(text))
            Add("Survey", Qualifier(m.Groups["q"].Value), [m.Groups["no"].Value + (m.Groups["part"].Success ? "(P)" : "")]);
        foreach (Match m in PlotPattern().Matches(text))
            Add("Plot", null, SplitNumberList(m.Groups["list"].Value));
        foreach (Match m in GatKhasraPattern().Matches(text))
            Add(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Groups["s"].Value.ToLowerInvariant()), null, SplitNumberList(m.Groups["list"].Value));
        return groups;
    }

    private static string? Qualifier(string q) => q.Length == 0 ? null : char.ToUpperInvariant(q[0]) + q[1..].ToLowerInvariant();

    private static IEnumerable<string> SplitNumberList(string list)
    {
        foreach (Match m in NumberItem().Matches(list))
        {
            var number = m.Groups["n"].Value;
            if (m.Groups["to"].Success && number.Contains('/'))
            {
                // "52/1 to 17" → 52/1 … 52/17
                var cut = number.LastIndexOf('/');
                if (int.TryParse(number[(cut + 1)..], out var from) && int.TryParse(m.Groups["to"].Value, out var to) && to >= from && to - from <= 200)
                {
                    for (var i = from; i <= to; i++) yield return $"{number[..(cut + 1)]}{i}";
                    continue;
                }
            }
            yield return number + (m.Groups["part"].Success ? "(P)" : "");
        }
    }

    private static string NormalizeNumber(string n) => Regex.Replace(n.Trim().ToUpperInvariant(), @"\s+", "");

    /// <summary>"52/1, 52/2, … 52/17" → "52/1–52/17" so a long CTS list stays readable.</summary>
    private static List<string> CompressRanges(List<string> numbers)
    {
        var result = new List<string>();
        for (var i = 0; i < numbers.Count; i++)
        {
            var j = i;
            while (j + 1 < numbers.Count && Consecutive(numbers[j], numbers[j + 1])) j++;
            result.Add(j - i >= 2 ? $"{numbers[i]}–{numbers[j]}" : numbers[i]);
            if (j - i == 1) result.Add(numbers[j]);
            i = j;
        }
        return result;
    }

    private static IEnumerable<string> ExpandRanges(IEnumerable<string> numbers)
    {
        foreach (var n in numbers)
        {
            var dash = n.IndexOf('–');
            if (dash < 0) { yield return n; continue; }
            var (first, last) = (n[..dash], n[(dash + 1)..]);
            var cut = first.LastIndexOf('/');
            if (cut < 0 || !int.TryParse(first[(cut + 1)..], out var from) || !int.TryParse(last[(last.LastIndexOf('/') + 1)..], out var to)) { yield return n; continue; }
            for (var i = from; i <= to; i++) yield return $"{first[..(cut + 1)]}{i}";
        }
    }

    private static bool Consecutive(string a, string b)
    {
        var (ca, cb) = (a.LastIndexOf('/'), b.LastIndexOf('/'));
        return ca > 0 && cb > 0 && a[..ca] == b[..cb]
            && int.TryParse(a[(ca + 1)..], out var x) && int.TryParse(b[(cb + 1)..], out var y) && y == x + 1;
    }

    // ── Location ──────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] States =
    [
        "Andhra Pradesh", "Arunachal Pradesh", "Assam", "Bihar", "Chhattisgarh", "Goa", "Gujarat", "Haryana", "Himachal Pradesh",
        "Jharkhand", "Karnataka", "Kerala", "Madhya Pradesh", "Maharashtra", "Manipur", "Meghalaya", "Mizoram", "Nagaland",
        "Odisha", "Orissa", "Punjab", "Rajasthan", "Sikkim", "Tamil Nadu", "Telangana", "Tripura", "Uttar Pradesh", "Uttarakhand",
        "West Bengal", "Delhi", "Jammu and Kashmir", "Ladakh", "Puducherry", "Chandigarh"
    ];

    private static readonly string[] Cities =
    [
        "Navi Mumbai", "Mumbai", "Thane", "Pune", "Nagpur", "Nashik", "New Delhi", "Delhi", "Gurugram", "Gurgaon", "Noida", "Ghaziabad",
        "Bengaluru", "Bangalore", "Mysuru", "Chennai", "Coimbatore", "Hyderabad", "Secunderabad", "Kolkata", "Ahmedabad", "Surat",
        "Vadodara", "Chandigarh", "Mohali", "Jaipur", "Lucknow", "Kanpur", "Indore", "Bhopal", "Kochi", "Thiruvananthapuram",
        "Bhubaneswar", "Visakhapatnam", "Vijayawada", "Patna", "Ranchi", "Raipur", "Guwahati", "Goa", "Panaji", "Ludhiana", "Amritsar"
    ];

    /// <summary>#366: a state as written in filings, with or without its spaces ("Andhrapradesh").</summary>
    private static string StateAlternation => string.Join("|", States.SelectMany(s => new[] { Regex.Escape(s), Regex.Escape(s.Replace(" ", "")) }).Distinct());

    /// <summary>#366: a PIN written without the word "PIN" ("Chittoor 517408 Andhrapradesh") — six digits only count when
    /// a state or "India" follows directly, so an amount or a survey number never reads as one.</summary>
    private static readonly Regex PinBeforeState = new($@"\b(?<v>[1-9]\d{{5}})\b(?=[\s,.\-–]{{0,3}}(?:{StateAlternation}|india)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static NormalizedLocation Location(string text)
    {
        var state = States.FirstOrDefault(s => Regex.IsMatch(text, $@"\b(?:{Regex.Escape(s)}|{Regex.Escape(s.Replace(" ", ""))})\b", RegexOptions.IgnoreCase));
        var city = Cities.FirstOrDefault(c => Regex.IsMatch(text, $@"\b{Regex.Escape(c)}\b", RegexOptions.IgnoreCase));
        var taluka = TalukaPattern().Match(text) is { Success: true } t ? Title(t.Groups["v"].Value) : null;
        var district = DistrictPattern().Match(text) is { Success: true } d ? Title(d.Groups["v"].Value) : null;
        var village = VillagePattern().Match(text) is { Success: true } v ? Title(v.Groups["v"].Value) : null;
        var pin = PinPattern().Match(text) is { Success: true } p ? p.Groups["v"].Value
            : PinBeforeState.Match(text) is { Success: true } q ? q.Groups["v"].Value : null;

        var known = new[] { state, city, taluka, district, village }.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localities = new List<string>();
        foreach (Match m in SituatedAt().Matches(text))
        {
            foreach (var part in m.Groups["v"].Value.Split(','))
            {
                var token = Regex.Replace(part, @"\(.*?\)|\bvillage\b|\btaluka.*|\bdistrict\b|\bindia\b|[-–]\s*[1-9]\d{5}", "", RegexOptions.IgnoreCase).Trim(' ', '.', '-', '–');
                if (token.Length < 3 || token.Any(char.IsDigit) || known.Contains(token) || District(token, district)) continue;
                token = Title(token);
                if (!localities.Contains(token, StringComparer.OrdinalIgnoreCase)) localities.Add(token);
            }
        }
        return new NormalizedLocation(localities, village, taluka, district, city, state, pin);
    }

    private static bool District(string token, string? district) =>
        district is not null && token.StartsWith(district, StringComparison.OrdinalIgnoreCase);

    private static string Title(string s)
    {
        var trimmed = Whitespace().Replace(s.Trim(), " ");
        return trimmed.Any(char.IsLower) && trimmed.Any(char.IsUpper)
            ? trimmed
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
    }

    // ── Other entities ────────────────────────────────────────────────────────────────────────────────

    private static List<string> NamedEntities(string text)
    {
        var names = new List<string>();
        foreach (Match m in CompanyName().Matches(text))
        {
            var name = Whitespace().Replace(m.Value, " ").Trim().TrimEnd('.');
            name = Regex.Replace(name, @"\bPvt\.?(?=\s)", "Private", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\bLtd\.?$", "Limited", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^(?:(?:viz\.?,?|of|and|the|entire|current|assets?|associate|company)\s+)+", "", RegexOptions.IgnoreCase);
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    // ── Patterns ──────────────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"(?<!im)mov(e)?able")] private static partial Regex MovableWord();
    [GeneratedRegex(@"^(as per|as mentioned|as stated|as given|as detailed|as specified|mentioned in|part [a-z0-9]+ of|refer|more particularly|described in|as described|schedule|annexure)|\b(schedule|annexure)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceOnly();

    [GeneratedRegex(@"\b(?:unit|flat|office|shop|apartment)\s*no\.?\s*(?<no>\d+\s*[a-z]?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPattern();
    [GeneratedRegex(@"\b(?<n>\d{1,3})\s*(?:st|nd|rd|th)?\s+floor\b", RegexOptions.IgnoreCase)]
    private static partial Regex FloorPattern();
    [GeneratedRegex(@"(?:\b(?:building|bldg)\b(?:\s+known\s+as)?|\bprojects?\b\s*-?|\bprotect\b|\bcomprising\s+in(?:\s+the\s+building\s+known\s+as)?)\s*[""“](?<name>[^""”]{3,60})[""”]|\b(?:building|bldg)\s+known\s+as\s+(?-i:(?<name>[A-Z][\w&.-]*(?:\s+[A-Z][\w&.-]*){0,4}))", RegexOptions.IgnoreCase)]
    private static partial Regex BuildingPattern();

    [GeneratedRegex(@"(?<v>\d{1,3}(?:,\d{2,3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*(?:(?<ft>sq(?:uare)?\.?\s*(?:feet|fts?)\.?|sqft)|(?<ac>acres?)\b|(?<m>sq(?:uare)?\.?\s*/?\s*(?:m(?:e?t(?:er|re)s?|trs?|ts|ets|t)?)\b\.?|sqm\b))", RegexOptions.IgnoreCase)]
    private static partial Regex AreaPattern();
    [GeneratedRegex(@"^[\s,.(]*(?:eq(?:uivalent)?\.?|i\.?\s*e\.?|that is|or)?[\s,.]*(?:eq(?:uivalent)?\.?)?\s*(?:to)?[\s,.(]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Equivalence();

    [GeneratedRegex(@"(?<n>\d{1,4})\s*car\s*parking", RegexOptions.IgnoreCase)]
    private static partial Regex ParkingCount();
    [GeneratedRegex(@"\b(?:situated|constructed\s+on|lying|land\s+admeasuring)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ParkingSectionEnd();
    [GeneratedRegex(@"\b(?<p>[A-Z]{1,2})\s*-\s*(?<a>\d{1,4})(?:\s*to\s*(?:to\s*)?(?:\k<p>\s*-\s*)?(?<to>\d{1,4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex ParkingNumber();

    [GeneratedRegex(@"(?:\b(?<q>new|old)\s+)?(?:\bc\.?\s*t\.?\s*s\.?|\bcst\b)\s*[-\s]*(?:nos?\.?|numbers?)?[\s.,:-]*(?<list>\d+[a-z]?(?:/[0-9a-z]+)*(?:\s*\((?:p|part)\))?(?:\s*(?:,|\.|and|&)?\s*\d+[a-z]?(?:/[0-9a-z]+)*(?:\s*\((?:p|part)\))?(?:\s*to\s*\d+)?)*)", RegexOptions.IgnoreCase)]
    private static partial Regex CtsPattern();
    [GeneratedRegex(@"(?<n>\d+[a-z]?(?:/[0-9a-z]+)*)(?:\s*(?<part>\((?:p|part)\)))?(?:\s*to\s*(?<to>\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex NumberItem();
    [GeneratedRegex(@"(?:\b(?<q>new|old)\s+)?(?<![.\w])(?:survey|s\.|sy\.)\s*no\.?\s*(?<no>\d+[a-z]?(?:/\d+[a-z]?)*)(?<part>\s*(?:\(?\s*part\b\)?|\(p\)))?", RegexOptions.IgnoreCase)]
    private static partial Regex SurveyPattern();
    // A list joined by "+", ",", "&" or "and" ("Plot Nos: 728 + 729 + 730"); an item is never the head of a comma-grouped
    // amount ("Plot No. 5, 1,000 sq ft" is plot 5 only).
    [GeneratedRegex(@"\bplot\s*nos?\.?\s*[:.-]?\s*(?<list>\d+[a-z]?(?:/\d+[a-z]?)?(?!,?\d)(?:\s*(?:\+|,|&|and)\s*\d+[a-z]?(?:/\d+[a-z]?)?(?!,?\d))*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlotPattern();
    [GeneratedRegex(@"\b(?<s>gat|gata|khasra|arazi|khata)\s*nos?\.?\s*(?<list>\d+[a-z]?(?:/\d+[a-z]?)*(?:\s*(?:,|and|&)\s*\d+[a-z]?(?:/\d+[a-z]?)*)*)", RegexOptions.IgnoreCase)]
    private static partial Regex GatKhasraPattern();

    [GeneratedRegex(@"\btaluka\s*(?<v>[A-Za-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TalukaPattern();
    [GeneratedRegex(@"(?<v>\b[A-Za-z]+(?:\s+[A-Za-z]+)?)\s+district\b", RegexOptions.IgnoreCase)]
    private static partial Regex DistrictPattern();
    // "Village Malegaon" or "Samudrapalli Village" (the name before the word is capitalised, so "the village" never matches).
    [GeneratedRegex(@"\bvillage\s+(?<v>[A-Za-z]+(?:\s+(?:east|west|north|south))?)|(?-i:\b(?<v>[A-Z][a-z]{2,}))\s+village\b", RegexOptions.IgnoreCase)]
    private static partial Regex VillagePattern();
    [GeneratedRegex(@"(?:\bpin(?:\s*code)?[\s:.-]*|[-–]\s*)(?<v>[1-9]\d{5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PinPattern();
    [GeneratedRegex(@"\b(?:situated|located|lying(?:\s+and\s+being)?)\s+at\s+(?<v>[^.;]+?)(?=\s+(?:together|with|incl|including|comprising|forming|to\s+be|as\s+detailed|as\s+per|and\s+the|admeasuring)\b|[.;]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SituatedAt();
    [GeneratedRegex(@"(?:[A-Z][A-Za-z&]*\.?\s+){1,6}(?:Private|Pvt\.?)\s+(?:Limited|Ltd\.?)|(?:[A-Z][A-Za-z&]*\s+){1,6}Limited\b")]
    private static partial Regex CompanyName();
}
