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
public static class LitigationCasePresentation
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
        var institution = LitigationBaselineRisk.ForumOf(type, court, courtCategory) switch
        {
            LitigationForum.SupremeCourt => LitigationInstitution.SupremeCourt,
            LitigationForum.HighCourt => LitigationInstitution.HighCourt,
            LitigationForum.DistrictCourt => LitigationInstitution.DistrictCourt,
            LitigationForum.Consumer => LitigationInstitution.ConsumerCourt,
            LitigationForum.Cestat => LitigationInstitution.Cestat,
            LitigationForum.Nclt => LitigationInstitution.Nclt,
            LitigationForum.Nclat => LitigationInstitution.Nclat,
            LitigationForum.Itat => LitigationInstitution.Itat,
            LitigationForum.Drt => LitigationInstitution.Drt,
            LitigationForum.Drat => LitigationInstitution.Drat,
            _ => LitigationInstitution.Others
        };
        if (institution != LitigationInstitution.Others) return institution;
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

    public static string SideLabel(LitigationCompanySide side) => side switch
    {
        LitigationCompanySide.ByCompany => "By the company",
        LitigationCompanySide.AgainstCompany => "Against the company",
        LitigationCompanySide.Both => "Company on both sides",
        _ => "Not determined"
    };

    /// <summary>List order: section (pending, disposed, status unknown), then forum by seniority, then cases against the
    /// company before cases it brought, then newest filing first. Stable for equal keys.</summary>
    public static int SectionOrder(LitigationCaseStatusBucket bucket) => bucket switch
    {
        LitigationCaseStatusBucket.Pending => 0,
        LitigationCaseStatusBucket.Disposed => 1,
        _ => 2
    };

    /// <summary>Against the company first (the cases a lender cares about), then by the company, then the undetermined.</summary>
    public static int SideOrder(LitigationCompanySide side) => side switch
    {
        LitigationCompanySide.AgainstCompany => 0,
        LitigationCompanySide.ByCompany => 1,
        LitigationCompanySide.Both => 2,
        _ => 3
    };

    /// <summary>Filing date as a sortable number, so cases without one sort last.</summary>
    public static int FilingSortKey(string? filingDate) => LitigationCaseAges.ParseDate(filingDate)?.DayNumber ?? int.MinValue;
}
