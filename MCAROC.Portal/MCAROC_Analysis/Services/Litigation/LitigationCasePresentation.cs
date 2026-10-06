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

    public static readonly string[] DateFormats = ["dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"];

    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>How long the case has been open (pending), took to decide (disposed) or has existed (status unknown).
    /// "-" whenever the dates needed are missing or contradict each other.</summary>
    public static string Age(string? filingDate, string? decisionDate, LitigationCaseStatusBucket bucket, DateOnly today)
    {
        if (ParseDate(filingDate) is not { } filed) return "-";
        switch (bucket)
        {
            case LitigationCaseStatusBucket.Disposed:
                if (ParseDate(decisionDate) is not { } decided || decided < filed) return "-";
                return $"Disposed in {Span(filed, decided)}";
            case LitigationCaseStatusBucket.Pending:
                return filed > today ? "-" : $"Pending {Span(filed, today)}";
            default:
                return filed > today ? "-" : $"Filed {Span(filed, today)} ago";
        }
    }

    /// <summary>Whole years and months, or days when under a month ("3 yrs 2 mths", "1 yr", "5 mths", "12 days").</summary>
    public static string Span(DateOnly from, DateOnly to)
    {
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month - (to.Day < from.Day ? 1 : 0);
        if (months < 1)
        {
            var days = Math.Max(0, to.DayNumber - from.DayNumber);
            return days == 1 ? "1 day" : $"{days} days";
        }
        var years = months / 12;
        var rest = months % 12;
        var y = years == 1 ? "1 yr" : $"{years} yrs";
        var m = rest == 1 ? "1 mth" : $"{rest} mths";
        return years == 0 ? m : rest == 0 ? y : $"{y} {m}";
    }

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
    public static int FilingSortKey(string? filingDate) => ParseDate(filingDate)?.DayNumber ?? int.MinValue;
}
