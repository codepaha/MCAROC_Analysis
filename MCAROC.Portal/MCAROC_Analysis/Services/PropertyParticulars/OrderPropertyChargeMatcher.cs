using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;

namespace MCAROC_Analysis.Services.PropertyParticulars;

/// <summary>One immovable property an open charge secures, in the same grounded shape as an order's property, so the two compare field by field.</summary>
public sealed record ChargedProperty(long ChargeId, string? ChargeNumber, string? Holder, AiProperty Property);

/// <summary><see cref="SameProperty"/>: the order names the charged asset itself. <see cref="ChargedProject"/>: the order names another unit or part of a
/// project or building the charge secures (a home buyer's flat in a project whose land is mortgaged).</summary>
public enum PropertyChargeTieKind { SameProperty, ChargedProject }

public sealed record PropertyChargeTie(PropertyChargeTieKind Kind, ChargedProperty Charge);

/// <summary>Compares the properties court orders name (#554) with the properties the company's open charges secure. The portal's own rules, not the
/// model's: the same asset only by <see cref="CaseOrderProperties.SameProperty"/> (a shared unit with its building or project, or a survey number in
/// the same village or PIN, and nothing they both state differing); the same project only by its name, with at least two words and one beyond the
/// company's own name, and no conflicting place. Anything else is a property not under an open charge.</summary>
public static class OrderPropertyChargeMatcher
{
    /// <summary>The immovable properties of the open charges: the charge's own validated extraction when there is one, else the rules reading
    /// (one property per unit, or one for the whole text).</summary>
    public static IReadOnlyList<ChargedProperty> ChargedProperties(IReadOnlyList<RocCharge> openCharges,
        IReadOnlyDictionary<string, PropertyParticularsExtraction>? extractions)
    {
        var result = new List<ChargedProperty>();
        foreach (var charge in openCharges)
            foreach (var e in charge.Events.Where(e => !string.IsNullOrWhiteSpace(e.PropertyParticulars)))
            {
                var ai = extractions is not null && PropertyParticularsAi.IsExtractable(e.PropertyParticulars)
                    && extractions.TryGetValue(PropertyParticularsAi.HashOf(e.PropertyParticulars!, e.PropertyType), out var x)
                    && x.Status == PropertyParticularsExtractionStatus.Completed
                    ? PropertyParticularsAi.Deserialize(x.ExtractionJson) : null;
                var properties = ai is { Properties.Count: > 0 }
                    ? ai.Properties.Where(p => p.AssetClass == PropertyAssetClass.Immovable)
                    : FromRules(PropertyParticularsNormalizer.Normalize(e.PropertyParticulars, e.PropertyType));
                result.AddRange(properties.Select(p => new ChargedProperty(charge.ChargeId, charge.RocChargeNumber, charge.LatestChargeHolderRaw, p)));
            }
        // The same asset restated by a modification is one charged property per charge.
        return result.GroupBy(c => (c.ChargeId, Key: CaseOrderProperties.Describe(c.Property))).Select(g => g.First()).ToList();
    }

    private static IEnumerable<AiProperty> FromRules(NormalizedPropertyParticulars n)
    {
        if (!n.AssetClasses.Contains(PropertyAssetClass.Immovable) && n.AssetClasses.Count > 0) yield break;
        var kind = n.Kinds.FirstOrDefault(k => k is PropertyKind.Premises or PropertyKind.Land or PropertyKind.BuildingOrProject, PropertyKind.Land);
        var project = n.BuildingsOrProjects.FirstOrDefault();
        var l = n.Location;
        if (n.Units.Count == 0)
        {
            yield return new AiProperty(PropertyAssetClass.Immovable, kind, n.Owner, null, null, null, project, [], null, n.SurveyNumbers, l.Localities,
                l.Village, l.Taluka, l.District, l.City, l.State, l.Pin);
            yield break;
        }
        foreach (var u in n.Units)
            yield return new AiProperty(PropertyAssetClass.Immovable, PropertyKind.Premises, n.Owner, u.UnitNumber, u.Floor, u.Building, project, [], null,
                n.SurveyNumbers, l.Localities, l.Village, l.Taluka, l.District, l.City, l.State, l.Pin);
    }

    /// <summary>Every charged property the order's property is, or lies in the project of. The same asset wins over the same project for a charge.</summary>
    public static IReadOnlyList<PropertyChargeTie> Ties(AiProperty orderProperty, IReadOnlyList<ChargedProperty> charged, IReadOnlyCollection<string> companyNames)
    {
        var companyWords = companyNames.SelectMany(Words).ToHashSet(StringComparer.Ordinal);
        var ties = new List<PropertyChargeTie>();
        foreach (var byCharge in charged.GroupBy(c => c.ChargeId))
        {
            var same = byCharge.FirstOrDefault(c => CaseOrderProperties.SameProperty(orderProperty, c.Property));
            if (same is not null) { ties.Add(new(PropertyChargeTieKind.SameProperty, same)); continue; }
            var project = byCharge.FirstOrDefault(c => SameProject(orderProperty, c.Property, companyWords));
            if (project is not null) ties.Add(new(PropertyChargeTieKind.ChargedProject, project));
        }
        return ties;
    }

    /// <summary>The order's project or building is named in the charged property's project or building: every one of its words, in order, at least
    /// two words with one that is not the company's own name ("Godrej Two", not "Godrej"), and no place both state differing.</summary>
    internal static bool SameProject(AiProperty order, AiProperty charged, IReadOnlySet<string> companyWords)
    {
        if (Conflict(order.Village, charged.Village) || Conflict(order.City, charged.City) || Conflict(order.Pin, charged.Pin)) return false;
        var chargedNames = new[] { charged.Project, charged.Building }.OfType<string>().Select(n => Words(n).ToList()).Where(w => w.Count > 0).ToList();
        foreach (var name in new[] { order.Project, order.Building }.OfType<string>())
        {
            var words = Words(name).ToList();
            if (words.Count < 2 || words.All(companyWords.Contains)) continue;
            if (chargedNames.Any(c => ContainsRun(c, words))) return true;
        }
        return false;
    }

    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "THE", "OF", "AT", "IN", "AND", "PROJECT", "PROJECTS", "SCHEME", "COMPLEX", "BUILDING", "RESIDENTIAL", "COMMERCIAL", "HOUSING", "SOCIETY",
        "PHASE", "TOWER", "TOWERS", "WING", "BLOCK", "CALLED", "KNOWN", "AS", "NAMED"
    };

    private static IEnumerable<string> Words(string? name) =>
        (name ?? "").ToUpperInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(w => !Generic.Contains(w));

    private static readonly char[] Separators = [' ', ',', '.', '-', '/', '(', ')', '"', '\'', '&', ':', ';'];

    private static bool ContainsRun(IReadOnlyList<string> haystack, IReadOnlyList<string> run)
    {
        for (var i = 0; i + run.Count <= haystack.Count; i++)
            if (run.Select((w, k) => haystack[i + k] == w).All(x => x)) return true;
        return false;
    }

    private static string N(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool Conflict(string? a, string? b) => N(a).Length > 0 && N(b).Length > 0 && N(a) != N(b);
}
