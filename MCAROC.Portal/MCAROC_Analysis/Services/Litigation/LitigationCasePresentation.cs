using System.Globalization;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>The fixed list of forums a litigation summary reports on. Declaration order is the display order: the
/// courts by seniority first, then the tribunals.</summary>
public enum LitigationInstitution
{
    SupremeCourt,
    HighCourt,
    DistrictCourt,
    ConsumerCourt,
    Cestat,
    Nclt,
    Nclat,
    Itat,
    Drt,
    Drat,
    Others
}

/// <summary>Presentation rules shared by the litigation report and the portal: which forum a case belongs to, how long the case has been open, and the order cases are listed in. Pure functions of
/// the stored case — nothing here reads the database or the vendor.</summary>
public static partial class LitigationCasePresentation
{
    public static string Label(LitigationInstitution institution) => institution switch
    {
        LitigationInstitution.SupremeCourt => "Supreme Court",
        LitigationInstitution.HighCourt => "High Court",
        LitigationInstitution.DistrictCourt => "District Court",
        LitigationInstitution.ConsumerCourt => "Consumer Court",
        LitigationInstitution.Cestat => "CESTAT",
        LitigationInstitution.Nclt => "NCLT",
        LitigationInstitution.Nclat => "NCLAT",
        LitigationInstitution.Itat => "ITAT",
        LitigationInstitution.Drt => "DRT",
        LitigationInstitution.Drat => "DRAT",
        _ => "Others"
    };

    /// <summary>The forum a case sits in, from the same reading of the vendor's type and court name that the baseline risk uses
    /// (<see cref="LitigationBaselineRisk.ForumOf"/>), so a case is never in one forum for its risk and another for its summary.
    /// RERA and anything unrecognised are "Others". The vendor's court category is only a last resort for the three court tiers: its
    /// "high_risk_court" is not a forum.</summary>
    public static LitigationInstitution Resolve(string? type, string? court, string? courtCategory)
    {
        switch (LitigationBaselineRisk.ForumOf(type, court, courtCategory))
        {
            case LitigationForum.SupremeCourt: return LitigationInstitution.SupremeCourt;
            case LitigationForum.HighCourt: return LitigationInstitution.HighCourt;
            case LitigationForum.DistrictCourt: return LitigationInstitution.DistrictCourt;
            case LitigationForum.Consumer: return LitigationInstitution.ConsumerCourt;
            case LitigationForum.Cestat: return LitigationInstitution.Cestat;
            case LitigationForum.Nclt: return LitigationInstitution.Nclt;
            case LitigationForum.Nclat: return LitigationInstitution.Nclat;
            case LitigationForum.Itat: return LitigationInstitution.Itat;
            case LitigationForum.Drt: return LitigationInstitution.Drt;
            case LitigationForum.Drat: return LitigationInstitution.Drat;
            case LitigationForum.Rera: return LitigationInstitution.Others; // a known forum that is not in the list: never a court tier
        }
        // Not recognised from the type or the court name: the vendor's category settles the three court tiers.
        return courtCategory?.Trim().ToLowerInvariant() switch
        {
            "supreme_court" => LitigationInstitution.SupremeCourt,
            "high_court" => LitigationInstitution.HighCourt,
            "district_court" => LitigationInstitution.DistrictCourt,
            _ => LitigationInstitution.Others
        };
    }

    /// <summary>The company's side as the report and the register show it: the same <see cref="LitigationCompanySides.Determine"/> the
    /// case page uses.</summary>
    public static LitigationCompanySide Side(string? vendorDirection, string? petitionersJson, string? respondentsJson, IEnumerable<string> companyNames) =>
        LitigationCompanySides.Determine(
            LitigationCaseCardMapper.ParseNames(petitionersJson), LitigationCaseCardMapper.ParseNames(respondentsJson), companyNames, vendorDirection);

    /// <summary>The case as it is cited — "WP 11374/2024", "OMP 488/2016". The source keeps the type, number and
    /// year apart and in several shapes: a bare serial ("488") with the year only in its own field, a long-form
    /// type ("OMP - Original Misc. Petition"), the type folded into the number ("Suit For Recovery/14/2021"), or a
    /// tribunal's joined petition-and-applications reference repeated as its type
    /// ("C.P.(IB)593/KB/2017C.A.(IB)506/KB/2018"), which already carries its own years. Null when there is no number.</summary>
    public static string? CaseTitle(string? caseNumber, string? caseType, string? caseYear)
    {
        var number = caseNumber?.Trim();
        if (string.IsNullOrEmpty(number)) return null;
        var type = ShortCaseType(caseType);

        if (EmbeddedTypePattern().Match(number) is { Success: true } embedded)
            return $"{embedded.Groups["type"].Value.Trim()} {embedded.Groups["rest"].Value}";
        if (number.Any(char.IsLetter))
            return JoinedReferencePattern().Replace(number, " · ");

        if (!number.Contains('/') && caseYear?.Trim() is { Length: 4 } year && int.TryParse(year, out var y) && y is >= 1900 and <= 2100)
            number = $"{number}/{year}";
        return type is null ? number : $"{type} {number}";
    }

    /// <summary>The short form of a case type: "OMP - Original Misc. Petition" → "OMP", "Original Application-" →
    /// "Original Application". Null when empty or when it is a full reference rather than a type.</summary>
    private static string? ShortCaseType(string? caseType)
    {
        var type = caseType?.Trim();
        if (string.IsNullOrEmpty(type) || type.Any(char.IsDigit)) return null;
        var separator = type.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0) type = type[..separator];
        type = type.TrimEnd('-', ' ');
        return type.Length == 0 ? null : type;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<type>[A-Za-z][A-Za-z .()&-]*?)\s*/\s*(?<rest>\d+/\d{4})$")]
    private static partial System.Text.RegularExpressions.Regex EmbeddedTypePattern();

    /// <summary>Where one reference ends with its year and the next begins straight after it.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=/\d{4})(?=[A-Za-z])")]
    private static partial System.Text.RegularExpressions.Regex JoinedReferencePattern();

    public static string SideLabel(LitigationCompanySide side) => side switch
    {
        LitigationCompanySide.ByCompany => "By the company",
        LitigationCompanySide.AgainstCompany => "Against the company",
        LitigationCompanySide.Both => "Company on both sides",
        _ => "Not determined"
    };

    /// <summary>The order cases are listed in, by the report and by the portal register alike, so the two can never disagree: pending
    /// first, then disposed, then status not reported; within a section by forum seniority, cases against the company before ones it
    /// brought, newest filing first (no filing date last), and the case id so equal cases keep a stable order.</summary>
    public static (int Section, int Forum, int Side, int Filing, long CaseId) SortKey(
        LitigationCaseStatusBucket status, LitigationInstitution forum, LitigationCompanySide side, string? filingDate, long caseId) =>
        (SectionOrder(status), (int)forum, SideOrder(side),
            LitigationCaseAges.ParseDate(filingDate) is { } filed ? -filed.DayNumber : int.MaxValue, caseId);

    private static int SectionOrder(LitigationCaseStatusBucket bucket) => bucket switch
    {
        LitigationCaseStatusBucket.Pending => 0,
        LitigationCaseStatusBucket.Disposed => 1,
        _ => 2
    };

    /// <summary>Against the company first (the cases a lender cares about), then by the company, then the undetermined.</summary>
    private static int SideOrder(LitigationCompanySide side) => side switch
    {
        LitigationCompanySide.AgainstCompany => 0,
        LitigationCompanySide.ByCompany => 1,
        LitigationCompanySide.Both => 2,
        _ => 3
    };
}
