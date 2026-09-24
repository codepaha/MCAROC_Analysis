using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.Analysis.Rules;

/// <summary>The parts of a free-text Indian address that can be compared across differently formatted
/// sources: 6-digit PIN codes, plot/door/survey numbers ("A-36", "8-2-293/82/F-B-1/F", "428"), and
/// distinctive locality words ("NAYAPALLI", "BHUBANESWAR"). Generic address words, state names, dates,
/// amounts and land areas are left out, since they are shared by unrelated addresses.</summary>
public sealed record AddressFingerprint(
    IReadOnlySet<string> PinCodes,
    IReadOnlySet<string> PlotNumbers,
    IReadOnlySet<string> LocalityTerms);

public enum AddressMatchBasis
{
    /// <summary>Same PIN code, at least one shared plot number and at least one shared locality word.</summary>
    PinPlotAndLocality,
    /// <summary>No PIN code on one side; at least one shared plot number and at least two shared locality words
    /// (one is enough when the shared plot number is a long compound one, e.g. "8-2-293/82/F-B-1/F").</summary>
    PlotAndLocality
}

public sealed record AddressMatch(
    AddressMatchBasis Basis,
    IReadOnlyList<string> SharedPinCodes,
    IReadOnlyList<string> SharedPlotNumbers,
    IReadOnlyList<string> SharedLocalityTerms);

/// <summary>Decides whether two free-text addresses describe the same place (issue #191). Used to compare a
/// charge's property particulars ("Equitable mortgage on the land and building at Plot No. A-36, Nayapalli,
/// Bhubaneswar") with the company's own addresses (registered office, business address, EPFO
/// establishments). The two texts are never formatted the same way, so this compares fingerprints rather than
/// strings.
///
/// Deliberately precision-biased, like EntityCrossReferenceRules: a match needs a shared plot number (the
/// most specific part of an address) plus supporting locality evidence, and two different PIN codes always
/// rule a match out. A shared city or PIN code alone is never enough — many unrelated properties share
/// both. The cost is recall: spelling variants ("BHAUTI" vs "BHAUNTI") and addresses with no plot number
/// are not matched, and a non-match is never read as "this is someone else's property".</summary>
public static partial class AddressMatcher
{
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        // Address structure
        "ROAD", "STREET", "LANE", "MARG", "NAGAR", "COLONY", "SECTOR", "PHASE", "BLOCK", "FLOOR", "GROUND",
        "BUILDING", "BUILDINGS", "TOWER", "WING", "PLOT", "PLOTS", "NUMBER", "NUMBERS", "SURVEY", "KHASRA",
        "KHATA", "KHATIYAN", "ARAZI", "GATA", "VILLAGE", "MOUZA", "TALUK", "TALUKA", "TEHSIL", "TAHSIL",
        "MANDAL", "DISTRICT", "DIST", "POST", "OFFICE", "NEAR", "OPPOSITE", "BEHIND", "HOUSE", "DOOR",
        "AREA", "CITY", "TOWN", "STATE", "MUNICIPAL", "CORPORATION", "WARD", "SHOP", "UNIT", "UNITS", "FLAT",
        "APARTMENT", "APARTMENTS", "COMPLEX", "INDUSTRIAL", "ESTATE", "INDIA", "NORTH", "SOUTH", "EAST", "WEST",
        "MAIN", "CROSS", "HIGHWAY", "NATIONAL", "PREMISES", "CAMPUS", "CODE",
        // Charge/legal wording around the address
        "LAND", "LANDS", "PROPERTY", "PROPERTIES", "SITUATED", "SITUATE", "LYING", "BEING", "BEARING", "TOTAL",
        "MEASURING", "ADMEASURING", "EXTENT", "ACRE", "ACRES", "SQUARE", "FEET", "YARDS", "METERS", "METRES",
        "EQUITABLE", "MORTGAGE", "MORTGAGED", "REGISTERED", "HYPOTHECATION", "CHARGE", "FIRST", "SECOND",
        "EXCLUSIVE", "PARI", "PASSU", "IMMOVABLE", "MOVABLE", "FACTORY", "SHED", "SHEDS", "STRUCTURES",
        "STRUCTURE", "THEREON", "THERE", "UPON", "WITH", "FROM", "THAT", "THIS", "THEIR", "WHICH", "BOTH",
        "PRESENT", "FUTURE", "BOUNDED", "BOUNDARIES", "SCHEDULE", "DEED", "DATED", "OWNED", "COMPANY",
        "LIMITED", "PRIVATE", "ALONG", "TOGETHER", "ALL", "PIECE", "PARCEL", "PART", "PARCELS", "SITE",
        "CONSTRUCTED", "BUILT", "PLANT", "MACHINERY", "STOCKS", "BOOK", "DEBTS", "ASSETS", "CURRENT",
        "FIXED", "SECURITY", "SECURED", "LOAN", "FACILITY", "FACILITIES", "BANK", "BORROWER", "GUARANTEE",
        "PERSONAL", "CORPORATE", "SIMPLE", "EXTENSION", "CREATION", "MODIFICATION", "OTHER", "OTHERS",
        "STOCK", "NAME", "NAMES", "STANDING", "HOLDING", "SAID", "BELONGING", "BELONGS", "ABOVE", "BELOW",
        "MENTIONED", "DESCRIBED", "KNOWN", "CALLED", "ENTIRE", "WHOLE", "PARGANA",
        // States and union territories — shared by every address in the state, so never distinctive
        "ANDHRA", "PRADESH", "ARUNACHAL", "ASSAM", "BIHAR", "CHHATTISGARH", "GUJARAT", "HARYANA",
        "HIMACHAL", "JHARKHAND", "KARNATAKA", "KERALA", "MADHYA", "MAHARASHTRA", "MANIPUR", "MEGHALAYA",
        "MIZORAM", "NAGALAND", "ODISHA", "ORISSA", "PUNJAB", "RAJASTHAN", "SIKKIM", "TAMIL", "NADU",
        "TELANGANA", "TRIPURA", "UTTAR", "UTTARAKHAND", "UTTARANCHAL", "BENGAL", "DELHI", "JAMMU",
        "KASHMIR", "LADAKH", "CHANDIGARH", "PUDUCHERRY", "PONDICHERRY", "ANDAMAN", "NICOBAR",
        "LAKSHADWEEP", "DADRA", "HAVELI", "DAMAN", "CAPITAL", "TERRITORY"
    };

    /// <summary>Builds a fingerprint from free text. <paramref name="knownPinCode"/> is a PIN held in a
    /// separate structured field (CompanyProfile.RegisteredAddressPinCode), added when valid.</summary>
    public static AddressFingerprint Fingerprint(string? text, string? knownPinCode = null)
    {
        var pins = new HashSet<string>(StringComparer.Ordinal);
        var plots = new HashSet<string>(StringComparer.Ordinal);
        var localities = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(knownPinCode))
        {
            var digits = new string(knownPinCode.Where(char.IsDigit).ToArray());
            if (PinRegex().IsMatch(digits)) pins.Add(digits);
        }

        if (string.IsNullOrWhiteSpace(text))
            return new AddressFingerprint(pins, plots, localities);

        var upper = text.ToUpperInvariant();

        foreach (Match m in LabelledPinRegex().Matches(upper)) pins.Add(m.Groups[1].Value + m.Groups[2].Value);
        foreach (Match m in StandalonePinRegex().Matches(upper)) pins.Add(m.Value);

        // Drop the parts of charge wording that look like numbers but are not address parts. Order matters:
        // dates and amounts contain separators the tokenizer below would otherwise split into fragments.
        var cleaned = DateRegex().Replace(upper, " ");
        cleaned = AmountRegex().Replace(cleaned, " ");
        cleaned = AreaRegex().Replace(cleaned, " ");
        cleaned = LabelledPinRegex().Replace(cleaned, " ");
        // "NO.428" → "NO 428", while a decimal ("2.5") stays one token and is then ignored.
        cleaned = NonDecimalDotRegex().Replace(cleaned, " ");

        foreach (var raw in cleaned.Split([' ', '\t', '\r', '\n', ',', ';', ':', '(', ')', '[', ']', '&', '"', '\''],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('-', '/', '\\', '.', '#');
            if (token.Length == 0) continue;

            if (token.Any(char.IsDigit))
            {
                if (IsPlotNumber(token, pins)) plots.Add(token);
            }
            else if (token.Length >= 4 && token.All(char.IsLetter) && !GenericWords.Contains(token))
            {
                localities.Add(token);
            }
        }

        return new AddressFingerprint(pins, plots, localities);
    }

    /// <summary>Returns the match between two addresses, or null when they do not meet the bar described
    /// on this class.</summary>
    public static AddressMatch? Match(AddressFingerprint a, AddressFingerprint b)
    {
        var sharedPins = a.PinCodes.Intersect(b.PinCodes).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (a.PinCodes.Count > 0 && b.PinCodes.Count > 0 && sharedPins.Count == 0)
            return null; // two different PIN codes: different places, whatever else looks alike

        var sharedPlots = a.PlotNumbers.Intersect(b.PlotNumbers).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (sharedPlots.Count == 0) return null;

        var sharedLocality = a.LocalityTerms.Intersect(b.LocalityTerms).OrderBy(t => t, StringComparer.Ordinal).ToList();

        if (sharedPins.Count > 0 && sharedLocality.Count >= 1)
            return new AddressMatch(AddressMatchBasis.PinPlotAndLocality, sharedPins, sharedPlots, sharedLocality);
        var requiredLocality = sharedPlots.Any(IsCompoundPlotNumber) ? 1 : 2;
        if (sharedPins.Count == 0 && sharedLocality.Count >= requiredLocality)
            return new AddressMatch(AddressMatchBasis.PlotAndLocality, sharedPins, sharedPlots, sharedLocality);
        return null;
    }

    /// <summary>A token counts as a plot/door/survey number when it has at least two digits, or joins a
    /// digit with a separator ("A-36", "4/1"). Single digits ("No. 1"), all-zero fragments, 4-digit years
    /// and decimals are too common in charge wording to identify a place.</summary>
    private static bool IsPlotNumber(string token, HashSet<string> pins)
    {
        if (pins.Contains(token)) return false;
        if (token.Contains('.')) return false;

        var digitCount = token.Count(char.IsDigit);
        var hasSeparator = token.Contains('-') || token.Contains('/');
        if (digitCount < 2 && !hasSeparator) return false;
        if (token.All(c => c == '0')) return false;
        if (token.Length == 4 && token.All(char.IsDigit) && int.Parse(token) is >= 1900 and <= 2099) return false;
        return true;
    }

    /// <summary>A door number like "8-2-293/82/F-B-1/F": two or more separators and four or more digits. These
    /// are specific enough that two unrelated properties in the same city will not share one.</summary>
    private static bool IsCompoundPlotNumber(string token) =>
        token.Count(c => c is '-' or '/') >= 2 && token.Count(char.IsDigit) >= 4;

    [GeneratedRegex(@"^[1-9]\d{5}$")]
    private static partial Regex PinRegex();

    [GeneratedRegex(@"(?<![\d,./-])[1-9]\d{5}(?![\d,./-])")]
    private static partial Regex StandalonePinRegex();

    /// <summary>"PIN 751 012", "PIN CODE: 751012", "PINCODE-751012" — the spaced form is only trusted after
    /// the label, since "428 429" elsewhere is two plot numbers, not a PIN.</summary>
    [GeneratedRegex(@"\bPIN\s*(?:CODE)?\s*(?:NO\.?)?\s*[:.\-]?\s*([1-9]\d{2})\s?(\d{3})(?!\d)")]
    private static partial Regex LabelledPinRegex();

    /// <summary>"12.03.2019", "01/04/2020" — the same separator twice, and not part of a longer compound
    /// token, so a Hyderabad-style door number "8-2-293/82/F-B-1/F" is never mistaken for a date.</summary>
    [GeneratedRegex(@"(?<![\w/-])\d{1,2}([./-])\d{1,2}\1\d{2,4}(?![\w/-])")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?:\b(?:RS|INR)|₹)\s*\.?\s*[\d,]+(?:\.\d+)?(?:\s*(?:CRORES?|LAKHS?|LACS?|/-))?|[\d,]+(?:\.\d+)?\s*(?:CRORES?|LAKHS?|LACS?)\b")]
    private static partial Regex AmountRegex();

    [GeneratedRegex(@"[\d,]*\d(?:\.\d+)?\s*(?:SQ\.?\s*(?:FT|FEET|YDS|YARDS|MTRS?|METERS|METRES|M)\b|SQFT|SFT|SYDS|ACRES?\b|ACS\b|CENTS?\b|GUNTAS?\b|HECTARES?\b|KANALS?\b|MARLAS?\b|BIGHAS?\b)")]
    private static partial Regex AreaRegex();

    [GeneratedRegex(@"(?<!\d)\.|\.(?!\d)")]
    private static partial Regex NonDecimalDotRegex();
}
