using System.Globalization;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.LitigationModule.Normalisation;

/// <param name="Bucket">Pending, Disposed or Unknown. Unknown is treated as pending for refresh (plan section 4.4).</param>
/// <param name="Outcome">The outcome word when the status field carries one (<c>ALLOWED</c>, <c>DISMISSED</c> ...); kept as filed.</param>
/// <param name="StageWord">A stage written into the status field (<c>Arguments</c>, <c>Prosecution Evidence</c>); the caller moves it to the stage field when that is empty.</param>
public sealed record CaseStatusParts(LitigationCaseStatusBucket Bucket, string? Outcome, string? StageWord);

/// <summary>The one status vocabulary map (plan section 3.6). Reuses <see cref="LitigationCaseStatusClassifier"/> for the bucket so every surface agrees.</summary>
public static class CaseStatusVocabulary
{
    private static readonly string[] OutcomeTokens = ["allow", "dismis", "reject", "withdr", "settl", "quash", "decree", "decid"];

    public static CaseStatusParts Map(string? status, string? stage = null)
    {
        var s = CaseValue.IsMissing(status) ? null : status!.Trim();
        var st = CaseValue.IsMissing(stage) ? null : stage!.Trim();
        var bucket = LitigationCaseStatusClassifier.Classify(s, st);

        string? outcome = null, stageWord = null;
        if (s is not null)
        {
            var lower = s.ToLowerInvariant();
            if (bucket == LitigationCaseStatusBucket.Disposed && OutcomeTokens.Any(lower.Contains)) outcome = s;
            // A pending-looking status that is not the word "pending" is really a stage ("Appearance", "Arguments").
            else if (bucket == LitigationCaseStatusBucket.Pending && !lower.Contains("pend") && st is null) stageWord = s;
        }
        return new CaseStatusParts(bucket, outcome, stageWord);
    }
}

/// <summary>Date parsing for the court feeds. Portal and tracker dates are <b>day-first</b>; e-Jagriti timestamps are UTC and the portal date is the IST date.</summary>
public static class CaseDates
{
    private static readonly string[] DayFirst = ["d-M-yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd/MM/yyyy", "d.M.yyyy", "d MMM yyyy", "d-MMM-yyyy", "yyyy-MM-dd"];

    /// <summary>A calendar date from text, day-first; null for a placeholder or anything unreadable (never guessed month-first).</summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (CaseValue.IsMissing(text)) return null;
        var t = text!.Trim();
        if (DateOnly.TryParseExact(t, DayFirst, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        // A full timestamp (ISO with time) is an instant: take its IST calendar date.
        if (t.Contains('T') && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            return DateOnly.FromDateTime(Ist.FromUtc(instant));
        return null;
    }
}

/// <param name="Last">Last hearing / last listing date.</param>
/// <param name="Next">Next hearing date: only ever a date that is today or later on a case that is not disposed.</param>
/// <param name="DerivedDecision">A candidate decision date for a disposed case (the last hearing), to be marked derived, never written as an observed decision date.</param>
public sealed record CombinedHearing(DateOnly? Last, DateOnly? Next, DateOnly? DerivedDecision);

/// <summary>The combined <c>LDOH/NDOH</c> column holds one date with no label (plan section 3.6).</summary>
public static class CombinedHearingDate
{
    public static CombinedHearing Split(string? value, LitigationCaseStatusBucket bucket, DateOnly today)
    {
        var date = CaseDates.ParseDate(value);
        if (date is null) return new CombinedHearing(null, null, null);
        if (bucket == LitigationCaseStatusBucket.Disposed)
            return date.Value <= today ? new CombinedHearing(date, null, date) : new CombinedHearing(null, null, null);   // a future date on a disposed case is not trusted
        return date.Value >= today ? new CombinedHearing(null, date, null) : new CombinedHearing(date, null, null);
    }
}

/// <param name="Canonical">The canonical court name, or the supplied one with the suffix removed when there is no alias.</param>
/// <param name="State">State from the suffix, else from the alias table.</param>
/// <param name="ListType">The cause-list type from the suffix (<c>Civil Causelist</c>), or null.</param>
public sealed record CourtNameParts(string Canonical, string? State, string? ListType);

/// <summary>Maps the several spellings of one court to a canonical name and splits the <c>, State, ... Causelist</c> suffix off (plan section 3.6).</summary>
public static partial class CourtNames
{
    private static readonly Dictionary<string, (string Canonical, string? State)> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["punjabhighcourt"] = ("High Court of Punjab and Haryana", null),
        ["high court of punjab and haryana"] = ("High Court of Punjab and Haryana", null),
        ["punjab and haryana high court"] = ("High Court of Punjab and Haryana", null),
        ["supreme court of india"] = ("Supreme Court of India", null),
        ["supremecourt"] = ("Supreme Court of India", null),
    };

    public static CourtNameParts Normalise(string? name)
    {
        if (CaseValue.IsMissing(name)) return new CourtNameParts(string.Empty, null, null);
        var t = Whitespace().Replace(name!.Trim(), " ");
        string? state = null, listType = null;
        if (Suffix().Match(t) is { Success: true } m)
        {
            t = t[..m.Index].TrimEnd(' ', ',');
            state = m.Groups["state"].Value.Trim();
            listType = m.Groups["list"].Value.Trim();
        }
        var key = t.Replace(" ", "").Equals("punjabhighcourt", StringComparison.OrdinalIgnoreCase) ? "punjabhighcourt" : t;
        if (Aliases.TryGetValue(key, out var alias)) return new CourtNameParts(alias.Canonical, state ?? alias.State, listType);
        return new CourtNameParts(t, state, listType);
    }

    [GeneratedRegex(@",\s*(?<state>[A-Za-z][A-Za-z &]+?)\s*,\s*(?<list>[A-Za-z ]*?Cause\s?list)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Suffix();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
}
