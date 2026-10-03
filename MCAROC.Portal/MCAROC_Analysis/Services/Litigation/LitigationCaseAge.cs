using System.Globalization;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>What the age of a case is measured from — and so how far it can be trusted.</summary>
public enum CaseAgeBasis
{
    /// <summary>The filing date the court record states: an exact start.</summary>
    Filed,
    /// <summary>No filing date; the year in the case number. Only the year is known, so the age is approximate.</summary>
    CaseYear,
    /// <summary>No filing date or case year; the year the CNR carries (its last four digits). Approximate, like the case year.</summary>
    CnrYear,
    /// <summary>No year at all; the earliest order on file. A true lower bound — the case is at least this old.</summary>
    FirstOrderOnFile,
    Unknown
}

/// <summary>Whether the clock is still running: a pending case is measured to the as-of date, a disposed one to its decision date
/// (or, when the record has none, its last order on file); a case of unknown status is simply the time since the start.</summary>
public enum CaseAgeKind { Pending, Disposed, Elapsed }

/// <summary>A pending case's age band, for the ageing summary.</summary>
public enum CaseAgeBand { UnderOneYear, OneToThreeYears, ThreeToFiveYears, OverFiveYears, NotKnown }

/// <summary>How old a litigation case is, with the basis shown so an estimate is never mistaken for a filed date. A filing date gives an
/// exact age; a case year or CNR year gives an approximate one (whole years, to the end year); the earliest order on file gives a lower bound.</summary>
public sealed record LitigationCaseAge(
    CaseAgeBasis Basis, CaseAgeKind Kind, DateOnly? Start, DateOnly? End, int? Months, bool EndIsLastOrderOnFile, int? Year)
{
    public bool IsLowerBound => Basis == CaseAgeBasis.FirstOrderOnFile;
    /// <summary>True when the age comes from a year alone, so it is "about" that many years.</summary>
    public bool IsApproximate => Basis is CaseAgeBasis.CaseYear or CaseAgeBasis.CnrYear;
    public int? Years => Months is { } m ? m / 12 : null;

    /// <summary>The duration alone: "5 years 5 months", "about 9 years", "at least 7 years".</summary>
    public string Duration => Months switch
    {
        null => "not known",
        { } m when IsApproximate => m < 12 ? "under a year" : "about " + LitigationCaseAges.FormatMonths(m),
        { } m => (IsLowerBound ? "at least " : "") + LitigationCaseAges.FormatMonths(m)
    };

    /// <summary>A short line for a card: "Pending for 5 years 5 months", "Ran for about 3 years", "About 9 years since registration".</summary>
    public string Headline => Months is null
        ? "Age not known"
        : Kind switch
        {
            CaseAgeKind.Pending => $"Pending for {Duration}",
            CaseAgeKind.Disposed => $"Ran for {Duration}",
            _ => IsApproximate ? $"{char.ToUpperInvariant(Duration[0])}{Duration[1..]} since registration"
                : $"{Duration} since {(Basis == CaseAgeBasis.Filed ? "filing" : "its first order on file")}"
        };

    /// <summary>Where the number comes from, in words, for the line under it.</summary>
    public string BasisNote
    {
        get
        {
            string D(DateOnly? d) => d is { } v ? v.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "—";
            var disposedEnd = Kind == CaseAgeKind.Disposed && End is not null ? $" · {(EndIsLastOrderOnFile ? "last order on file" : "decided")} {D(End)}" : "";
            return Basis switch
            {
                CaseAgeBasis.Filed => $"Filed {D(Start)}{disposedEnd}",
                CaseAgeBasis.CaseYear => $"No filing date on record · case year {Year} (whole years){disposedEnd}",
                CaseAgeBasis.CnrYear => $"No filing date or case year on record · year in the CNR {Year} (whole years){disposedEnd}",
                CaseAgeBasis.FirstOrderOnFile => $"No filing date or year on record · first order on file {D(Start)}{disposedEnd}",
                _ => "No filing date, year or order on record"
            };
        }
    }
}

public static class LitigationCaseAges
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "d MMM yyyy", "dd MMM yyyy", "yyyy/MM/dd"];

    /// <summary>A date as the court record writes it (ISO, or day-first as in India). Null when it isn't a date.</summary>
    public static DateOnly? ParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>A plausible registration year: four digits, not before 1900 and not after the as-of year.</summary>
    public static int? ParseYear(string? value, int asOfYear) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y >= 1900 && y <= asOfYear ? y : null;

    /// <summary>The year a CNR carries: its last four digits, when it is a valid CNR (16 characters, not a placeholder).</summary>
    public static int? YearInCnr(string? cnr, int asOfYear)
    {
        var normalised = LitigationCaseIdentity.NormaliseCnr(cnr);
        return normalised is null ? null : ParseYear(normalised[^4..], asOfYear);
    }

    /// <summary>The age of a case at <paramref name="asOf"/> — the date its data was retrieved (in IST), so a page and a report
    /// built from the same data always agree. Nothing is guessed. The start is, in order: the filing date (exact); the case year
    /// (approximate, whole years); the year in the CNR (approximate); the earliest order on file (a lower bound). When a year is known
    /// but an order on file is older than that year allows, the order wins as the lower bound. The basis is always reported.</summary>
    public static LitigationCaseAge Compute(
        string? filingDate, string? decisionDate, string? caseYear, LitigationCaseStatusBucket status,
        IEnumerable<string?> orderDates, DateOnly asOf, string? cnr = null)
    {
        var orders = orderDates.Select(ParseDate).OfType<DateOnly>().Order().ToList();
        var kind = status switch
        {
            LitigationCaseStatusBucket.Pending => CaseAgeKind.Pending,
            LitigationCaseStatusBucket.Disposed => CaseAgeKind.Disposed,
            _ => CaseAgeKind.Elapsed
        };

        // Where the clock stops.
        DateOnly? end;
        var endIsLastOrder = false;
        if (kind == CaseAgeKind.Disposed)
        {
            if (ParseDate(decisionDate) is { } decided) end = decided;
            else if (orders.Count > 0) { end = orders[^1]; endIsLastOrder = true; }
            else end = null; // disposed, but nothing says when: no duration
        }
        else end = asOf;

        if (ParseDate(filingDate) is { } filed)
            return new LitigationCaseAge(CaseAgeBasis.Filed, kind, filed, end, end is { } e && e >= filed ? MonthsBetween(filed, e) : null, endIsLastOrder, null);

        var (yearBasis, year) = ParseYear(caseYear, asOf.Year) is { } cy ? (CaseAgeBasis.CaseYear, (int?)cy)
            : YearInCnr(cnr, asOf.Year) is { } ny ? (CaseAgeBasis.CnrYear, ny)
            : (CaseAgeBasis.Unknown, null);
        int? yearMonths = year is { } y && end is { } ye && ye.Year >= y ? (ye.Year - y) * 12 : null;

        int? orderMonths = orders.Count > 0 && end is { } oe && oe >= orders[0] ? MonthsBetween(orders[0], oe) : null;
        // A year gives about N whole years; an order older than that year allows proves the case is at least as old as the order says.
        if (year is not null && (orderMonths is null || yearMonths is null || orderMonths <= yearMonths))
            return new LitigationCaseAge(yearBasis, kind, null, end, yearMonths, endIsLastOrder, year);
        if (orders.Count > 0)
            return new LitigationCaseAge(CaseAgeBasis.FirstOrderOnFile, kind, orders[0], end, orderMonths, endIsLastOrder, null);
        return new LitigationCaseAge(CaseAgeBasis.Unknown, kind, null, null, null, false, null);
    }

    /// <summary>Whole calendar months from <paramref name="start"/> to <paramref name="end"/> (a month is complete on the same day number).</summary>
    public static int MonthsBetween(DateOnly start, DateOnly end)
    {
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month;
        if (end.Day < start.Day) months--;
        return Math.Max(0, months);
    }

    public static string FormatMonths(int months)
    {
        if (months < 1) return "less than a month";
        var years = months / 12;
        var rest = months % 12;
        var parts = new List<string>();
        if (years > 0) parts.Add(years == 1 ? "1 year" : $"{years} years");
        if (rest > 0) parts.Add(rest == 1 ? "1 month" : $"{rest} months");
        return string.Join(" ", parts);
    }

    /// <summary>The band of a pending case. A lower bound lands in the band it already reaches (the case is at least that old); a
    /// case with no computable age is "not known".</summary>
    public static CaseAgeBand BandOf(LitigationCaseAge age) => age.Months switch
    {
        null => CaseAgeBand.NotKnown,
        < 12 => CaseAgeBand.UnderOneYear,
        < 36 => CaseAgeBand.OneToThreeYears,
        < 60 => CaseAgeBand.ThreeToFiveYears,
        _ => CaseAgeBand.OverFiveYears
    };

    public static string Label(CaseAgeBand band) => band switch
    {
        CaseAgeBand.UnderOneYear => "Under 1 year",
        CaseAgeBand.OneToThreeYears => "1–3 years",
        CaseAgeBand.ThreeToFiveYears => "3–5 years",
        CaseAgeBand.OverFiveYears => "Over 5 years",
        _ => "Age not known"
    };
}

/// <summary>The ageing of a portfolio's pending cases: how many fall in each band, and the oldest with a known age.</summary>
public sealed class LitigationAgeProfile
{
    public Dictionary<CaseAgeBand, int> PendingByBand { get; } = Enum.GetValues<CaseAgeBand>().ToDictionary(b => b, _ => 0);
    public int PendingCases => PendingByBand.Values.Sum();
    public long? OldestPendingCaseId { get; private set; }
    public LitigationCaseAge? OldestPending { get; private set; }

    /// <summary>Counts every case's age; only pending cases are banded.</summary>
    public void Add(long caseId, LitigationCaseAge age)
    {
        if (age.Kind != CaseAgeKind.Pending) return;
        PendingByBand[LitigationCaseAges.BandOf(age)]++;
        if (age.Months is { } m && (OldestPending?.Months is not { } best || m > best))
        {
            OldestPending = age;
            OldestPendingCaseId = caseId;
        }
    }
}
