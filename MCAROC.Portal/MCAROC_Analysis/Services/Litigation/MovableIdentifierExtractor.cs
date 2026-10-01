using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

public enum MovableIdentifierKind
{
    VehicleRegistration,
    EngineNumber,
    ChassisNumber,
    SerialNumber
}

/// <summary>A specific movable asset named in charge text. <see cref="Normalized"/> is uppercase letters and digits
/// only, so "MH-12 AB 1234" and "MH12AB1234" are the same asset.</summary>
public sealed record MovableIdentifier(MovableIdentifierKind Kind, string Raw, string Normalized)
{
    public string KindLabel => Kind switch
    {
        MovableIdentifierKind.VehicleRegistration => "vehicle registration",
        MovableIdentifierKind.EngineNumber => "engine number",
        MovableIdentifierKind.ChassisNumber => "chassis number",
        _ => "serial number"
    };
}

/// <summary>Where an identifier was found in an order: the page and a short excerpt around it.</summary>
public sealed record MovableIdentifierHit(MovableIdentifier Identifier, int PageNumber, string Excerpt);

/// <summary>Finds specific movable assets (vehicles, engines, chassis, serial-numbered machinery) in a charge's
/// free text and looks for exactly those identifiers in order text. Precision over recall, like the address
/// matcher: a category word such as "stock" or "plant and machinery" names no particular asset and is never
/// matched; an identifier has to be a recognisable registration/number, long enough to be specific, and is
/// matched only as a whole token (no letters or digits touching either end).</summary>
public static partial class MovableIdentifierExtractor
{
    /// <summary>RTO state/UT codes that begin a vehicle registration. A two-letter prefix outside this set is not a
    /// registration ("TO 12 AB 2021" style fragments in a narrative are not vehicles).</summary>
    private static readonly HashSet<string> RtoCodes = new(StringComparer.Ordinal)
    {
        "AN", "AP", "AR", "AS", "BR", "CG", "CH", "DD", "DL", "DN", "GA", "GJ", "HP", "HR", "JH", "JK", "KA", "KL",
        "LA", "LD", "MH", "ML", "MN", "MP", "MZ", "NL", "OD", "OR", "PB", "PY", "RJ", "SK", "TN", "TR", "TS", "UK", "UP", "WB"
    };

    public static IReadOnlyList<MovableIdentifier> Extract(params string?[] texts)
    {
        var found = new Dictionary<string, MovableIdentifier>(StringComparer.Ordinal);
        foreach (var raw in texts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var text = raw.ToUpperInvariant().Replace("&AMP;", "&");

            foreach (Match m in RegistrationRegex().Matches(text))
            {
                var state = m.Groups["state"].Value;
                if (!RtoCodes.Contains(state)) continue;
                Add(found, MovableIdentifierKind.VehicleRegistration, m.Value, Compact(m.Value));
            }
            foreach (Match m in BharatSeriesRegex().Matches(text))
                Add(found, MovableIdentifierKind.VehicleRegistration, m.Value, Compact(m.Value));

            foreach (Match m in LabelledNumberRegex().Matches(text))
            {
                var label = m.Groups["label"].Value;
                var id = m.Groups["id"].Value;
                var compact = Compact(id);
                if (!LooksSpecific(compact, minLength: label is "SERIAL" or "MACHINE" ? 7 : 6, minDigits: label is "SERIAL" or "MACHINE" ? 4 : 3)) continue;
                var kind = label.StartsWith("ENG", StringComparison.Ordinal) ? MovableIdentifierKind.EngineNumber
                    : label.StartsWith("CHAS", StringComparison.Ordinal) ? MovableIdentifierKind.ChassisNumber
                    : MovableIdentifierKind.SerialNumber;
                Add(found, kind, id, compact);
            }

            foreach (Match m in VinRegex().Matches(text))
            {
                var compact = Compact(m.Value);
                if (LooksSpecific(compact, minLength: 17, minDigits: 3) && compact.Any(char.IsLetter))
                    Add(found, MovableIdentifierKind.ChassisNumber, m.Value, compact);
            }
        }
        return found.Values.ToList();
    }

    private static void Add(Dictionary<string, MovableIdentifier> found, MovableIdentifierKind kind, string raw, string compact)
    {
        if (compact.Length == 0) return;
        found.TryAdd(compact, new MovableIdentifier(kind, raw.Trim(), compact));
    }

    /// <summary>At least <paramref name="minLength"/> letters/digits and <paramref name="minDigits"/> digits, so
    /// words ("NUMBER") and short codes are never treated as an asset identifier.</summary>
    private static bool LooksSpecific(string compact, int minLength, int minDigits) =>
        compact.Length >= minLength && compact.Count(char.IsDigit) >= minDigits;

    /// <summary>The text with every occurrence of the given identifiers (in any spacing) replaced by a space.</summary>
    public static string? Mask(string? text, IReadOnlyList<MovableIdentifier> identifiers)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var id in identifiers)
            text = BoundaryRegexFor(id.Normalized).Replace(text, " ");
        return text;
    }

    /// <summary>Uppercase letters and digits only.</summary>
    public static string Compact(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Finds the charge's identifiers in one page of order text. A compact-form containment check rejects
    /// almost every page cheaply; only a page that could contain an identifier pays for the boundary regex.</summary>
    public static List<MovableIdentifierHit> FindInPage(IReadOnlyList<MovableIdentifier> identifiers, int pageNumber, string pageText)
    {
        var hits = new List<MovableIdentifierHit>();
        if (identifiers.Count == 0 || string.IsNullOrWhiteSpace(pageText)) return hits;

        var compactPage = Compact(pageText);
        foreach (var id in identifiers)
        {
            if (!compactPage.Contains(id.Normalized, StringComparison.Ordinal)) continue;
            var match = BoundaryRegexFor(id.Normalized).Match(pageText);
            if (!match.Success) continue; // the characters were only adjacent across word boundaries
            hits.Add(new MovableIdentifierHit(id, pageNumber, Excerpt(pageText, match.Index, match.Length)));
        }
        return hits;
    }

    /// <summary>The identifier's characters in order, optionally separated by spaces, hyphens, slashes or dots, but
    /// only where a letter meets a digit (orders write "MH 12 AB 1234", never "MH12 A B1234" or "123 4"), with no
    /// further letter or digit touching either end.</summary>
    private static Regex BoundaryRegexFor(string normalized)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < normalized.Length; i++)
        {
            if (i > 0 && char.IsDigit(normalized[i]) != char.IsDigit(normalized[i - 1])) sb.Append(@"[\s\-/.]{0,2}");
            sb.Append(Regex.Escape(normalized[i].ToString()));
        }
        return new Regex($@"(?<![A-Za-z0-9]){sb}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    private static string Excerpt(string text, int index, int length, int radius = 110)
    {
        var start = Math.Max(0, index - radius);
        var end = Math.Min(text.Length, index + length + radius);
        var cleaned = Regex.Replace(text[start..end], @"\s+", " ").Trim();
        return (start > 0 ? "..." : "") + cleaned + (end < text.Length ? "..." : "");
    }

    // e.g. MH12AB1234, DL-3C-AB-1234 (district "3C"), KA 01 AB 1234
    [GeneratedRegex(@"(?<![A-Z0-9])(?<state>[A-Z]{2})[\s\-]?\d{1,2}[A-Z]?[\s\-]?[A-Z]{1,3}[\s\-]?\d{4}(?![A-Z0-9])")]
    private static partial Regex RegistrationRegex();

    // Bharat series: 22 BH 1234 AA
    [GeneratedRegex(@"(?<![A-Z0-9])\d{2}[\s\-]?BH[\s\-]?\d{4}[\s\-]?[A-Z]{1,2}(?![A-Z0-9])")]
    private static partial Regex BharatSeriesRegex();

    // "Engine No. G4LC123456", "Chassis Number: MA3EWDE1S00123456", "Serial No. SN-88123"
    [GeneratedRegex(@"(?<![A-Z])(?<label>ENGINE|ENG|CHASSIS|CHASIS|SERIAL|MACHINE)\s*(?:NO|NUMBER|NUM|#)?\.?\s*[:\-]?\s*(?<id>(?=[A-Z0-9\-/]*\d)[A-Z0-9][A-Z0-9\-/]{5,24})(?![A-Z0-9])")]
    private static partial Regex LabelledNumberRegex();

    // A bare 17-character vehicle identification number (never contains I, O or Q).
    [GeneratedRegex(@"(?<![A-Z0-9])[A-HJ-NPR-Z0-9]{17}(?![A-Z0-9])")]
    private static partial Regex VinRegex();
}
