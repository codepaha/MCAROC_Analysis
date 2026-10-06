using System.Globalization;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.CompanyMaster;

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

/// <summary>Whether the company brought the case or the case was brought against it.</summary>
public enum LitigationDirection { By, Against, Unspecified }

/// <summary>Presentation rules shared by the litigation report and the portal: which forum a case belongs to, whose
/// side the company is on, how long the case has been open, and the order cases are listed in. Pure functions of
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

    /// <summary>The vendor's category is trusted for the three court tiers. Anything else (its "high_risk_court",
    /// "tribunal_cases", a blank) is a tribunal, which the court name identifies — the vendor files DRT, NCLT and
    /// NCLAT under a "high risk court" category that is not a forum.</summary>
    public static LitigationInstitution Resolve(string? courtCategory, string? court)
    {
        switch (courtCategory?.Trim().ToLowerInvariant())
        {
            case "supreme_court": return LitigationInstitution.SupremeCourt;
            case "high_court": return LitigationInstitution.HighCourt;
            case "district_court": return LitigationInstitution.DistrictCourt;
        }

        var name = new string((court ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();
        if (name.Contains("nclat") || name.Contains("nationalcompanylawappellate")) return LitigationInstitution.Nclat;
        if (name.Contains("nclt") || name.Contains("nationalcompanylawtribunal")) return LitigationInstitution.Nclt;
        if (name.Contains("drat") || name.Contains("debtsrecoveryappellate")) return LitigationInstitution.Drat;
        if (name.Contains("drt") || name.Contains("debtsrecoverytribunal") || name.Contains("debtrecoverytribunal")) return LitigationInstitution.Drt;
        if (name.Contains("itat") || name.Contains("incometaxappellate")) return LitigationInstitution.Itat;
        if (name.Contains("cestat") || name.Contains("customsexcise")) return LitigationInstitution.Cestat;
        if (name.Contains("consumer")) return LitigationInstitution.ConsumerCourt;
        return LitigationInstitution.Others;
    }

    /// <summary>The vendor's direction when it gives one. A few categories (tribunal cases) carry none, so otherwise the
    /// side is read from the party lists: the company among the petitioners means it brought the case, among the
    /// respondents means it is defending. In both lists or neither the side cannot be told, and is not guessed.</summary>
    public static LitigationDirection ResolveDirection(
        string? vendorDirection, string? petitionersJson, string? respondentsJson, IEnumerable<string> targetNames)
    {
        switch (vendorDirection?.Trim().ToLowerInvariant())
        {
            case "by": return LitigationDirection.By;
            case "against": return LitigationDirection.Against;
        }

        var targets = targetNames.Select(NameCore).Where(t => t.Length > 0).Distinct().ToList();
        if (targets.Count == 0) return LitigationDirection.Unspecified;

        var asPetitioner = Names(petitionersJson).Any(p => MentionsAny(p, targets));
        var asRespondent = Names(respondentsJson).Any(p => MentionsAny(p, targets));
        return (asPetitioner, asRespondent) switch
        {
            (true, false) => LitigationDirection.By,
            (false, true) => LitigationDirection.Against,
            _ => LitigationDirection.Unspecified
        };
    }

    private static IEnumerable<string> Names(string? json) =>
        LitigationReportArtifacts.PartyText(json).Split("; ", StringSplitOptions.RemoveEmptyEntries).Where(n => n != "-");

    private static string NameCore(string name) => CompanyNameNormalizer.Normalize(name).NameCore;

    /// <summary>Whole-word match, so "COASTAL PROJECTS" matches "COASTAL PROJECTS AND ORS" but never "COASTALPROJECTSX".</summary>
    private static bool MentionsAny(string partyName, List<string> targetCores)
    {
        var party = " " + CompanyNameNormalizer.Normalize(partyName).NameNormalized + " ";
        return targetCores.Any(core => party.Contains(" " + core + " ", StringComparison.Ordinal));
    }

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

    public static int DirectionOrder(LitigationDirection direction) => direction switch
    {
        LitigationDirection.Against => 0,
        LitigationDirection.By => 1,
        _ => 2
    };

    /// <summary>Filing date as a sortable number, so cases without one sort last.</summary>
    public static int FilingSortKey(string? filingDate) => ParseDate(filingDate)?.DayNumber ?? int.MinValue;
}
