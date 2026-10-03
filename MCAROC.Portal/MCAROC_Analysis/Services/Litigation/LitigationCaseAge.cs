using System.Globalization;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>What the age of a case is measured from — and so how far it can be trusted.</summary>
public enum CaseAgeBasis
{
    /// <summary>The filing date the court record states: an exact start.</summary>
    Filed,
    /// <summary>No filing date; the earliest order on file. A true lower bound — the case is at least this old.</summary>
    FirstOrderOnFile,
    /// <summary>Only the year the case was registered. No age is computed from a year.</summary>
    YearOnly,
    Unknown
}

/// <summary>Whether the clock is still running: a pending case is measured to the as-of date, a disposed one to its decision date
/// (or, when the record has none, its last order on file); a case of unknown status is simply the time since the start.</summary>
public enum CaseAgeKind { Pending, Disposed, Elapsed }

/// <summary>A pending case's age band, for the ageing summary.</summary>
public enum CaseAgeBand { UnderOneYear, OneToThreeYears, ThreeToFiveYears, OverFiveYears, NotKnown }

/// <summary>How old a litigation case is, with the basis shown so an estimate is never mistaken for a filed date.</summary>
public sealed record LitigationCaseAge(
    CaseAgeBasis Basis, CaseAgeKind Kind, DateOnly? Start, DateOnly? End, int? Months, bool EndIsLastOrderOnFile, int? Year)
{
    public bool IsLowerBound => Basis == CaseAgeBasis.FirstOrderOnFile;
    public int? Years => Months is { } m ? m / 12 : null;

    /// <summary>The duration alone: "5 years 5 months", "at least 7 years", or "registered in 2017" when only the year is known.</summary>
    public string Duration => Basis switch
    {
        CaseAgeBasis.YearOnly => $"registered in {Year}",
        _ when Months is null => "not known",
        _ => (IsLowerBound ? "at least " : "") + LitigationCaseAges.FormatMonths(Months.Value)
    };

    /// <summary>A short line for a card: "pending for 5 years 5 months", "ran for 2 years 3 months", "at least 7 years pending".</summary>
    public string Headline => Basis switch
    {
        CaseAgeBasis.YearOnly => $"Registered in {Year}",
        CaseAgeBasis.Unknown => "Age not known",
        _ when Months is null => "Age not known",
        _ => Kind switch
        {
            CaseAgeKind.Pending => $"Pending for {Duration}",
            CaseAgeKind.Disposed => $"Ran for {Duration}",
            _ => $"{Duration} since {(Basis == CaseAgeBasis.Filed ? "filing" : "its first order on file")}"
        }
    };

    /// <summary>Where the number comes from, in words, for the line under it.</summary>
    public string BasisNote
    {
        get
        {
            string D(DateOnly? d) => d is { } v ? v.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "—";
            return Basis switch
            {
                CaseAgeBasis.Filed => $"Filed {D(Start)}" + (Kind == CaseAgeKind.Disposed && End is not null ? $" · {(EndIsLastOrderOnFile ? "last order on file" : "decided")} {D(End)}" : ""),
                CaseAgeBasis.FirstOrderOnFile => $"No filing date on record · first order on file {D(Start)}" + (Kind == CaseAgeKind.Disposed && End is not null ? $" · last order on file {D(End)}" : ""),
                CaseAgeBasis.YearOnly => "Only the year is on record",
                _ => "No filing date, order or year on record"
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

    /// <summary>The age of a case at <paramref name="asOf"/> — the date its data was retrieved (in IST), so a page and a report
    /// built from the same data always agree. Nothing is guessed: a missing filing date falls back to the earliest order on file
    /// (a lower bound, labelled so), then to the registration year (shown as a year, no age computed).</summary>
    public static LitigationCaseAge Compute(
        string? filingDate, string? decisionDate, string? caseYear, LitigationCaseStatusBucket status,
        IEnumerable<string?> orderDates, DateOnly asOf)
    {
        var orders = orderDates.Select(ParseDate).OfType<DateOnly>().Order().ToList();
        var kind = status switch
        {
            LitigationCaseStatusBucket.Pending => CaseAgeKind.Pending,
            LitigationCaseStatusBucket.Disposed => CaseAgeKind.Disposed,
            _ => CaseAgeKind.Elapsed
        };

        DateOnly? start;
        CaseAgeBasis basis;
        if (ParseDate(filingDate) is { } filed) { start = filed; basis = CaseAgeBasis.Filed; }
        else if (orders.Count > 0) { start = orders[0]; basis = CaseAgeBasis.FirstOrderOnFile; }
        else if (int.TryParse(caseYear?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year >= 1900 && year <= asOf.Year)
            return new LitigationCaseAge(CaseAgeBasis.YearOnly, kind, null, null, null, false, year);
        else return new LitigationCaseAge(CaseAgeBasis.Unknown, kind, null, null, null, false, null);

        DateOnly? end;
        var endIsLastOrder = false;
        if (kind == CaseAgeKind.Disposed)
        {
            if (ParseDate(decisionDate) is { } decided) end = decided;
            else if (orders.Count > 0) { end = orders[^1]; endIsLastOrder = true; }
            else end = null; // disposed, but nothing says when: no duration
        }
        else end = asOf;

        int? months = start is { } s && end is { } e && e >= s ? MonthsBetween(s, e) : null;
        return new LitigationCaseAge(basis, kind, start, end, months, endIsLastOrder, null);
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
