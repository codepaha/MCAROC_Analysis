using System.Globalization;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;

namespace MCAROC_Analysis.Services.LitigationData.Attribution;

/// <summary>Another company with a name the company has (or had): it has the name today, or had it before the company took it.</summary>
public sealed record NameHolder(string Identifier, string Name, string? State);

/// <summary>The name a party matched, and what tells whether the company had it when the case was filed. For a former name, <see cref="UsedUntil"/> is
/// the last day the company used it (the certificate's date where one was read, else the name history's), and <see cref="Others"/> the companies that
/// have it today. For the current name, <see cref="CurrentSince"/> is the day the company took it (its last change of name, else its incorporation),
/// and <see cref="Others"/> the companies that had it before. <see cref="OthersKnown"/> is false when the search for them was made but not read to its end
/// (or failed): then an empty <see cref="Others"/> is not evidence that nobody else has the name (review of #536).</summary>
public sealed record NameAtFiling(string MatchedName, bool IsFormer, DateOnly? UsedUntil, DateOnly? CurrentSince, IReadOnlyList<NameHolder> Others,
    bool OthersKnown = true);

public enum FormerNameCaseDecision
{
    /// <summary>The whole-name rule stands: the company had the name when the case was filed, or nothing says another company did.</summary>
    Keep,
    /// <summary>Filed after the company left the name, which no other company has: the old cause title carried on. Still the company's.</summary>
    CarriedOn,
    /// <summary>Another company has (or had) the name, and the filing date does not show the name was the company's then: judged, not counted until.</summary>
    NeedsJudgement,
}

public sealed record FormerNameCaseCheck(FormerNameCaseDecision Decision, string? Rule, string Reason);

/// <summary>
/// #497 PR C: a case found through a name the company has used is the company's only for the time it used it. The whole-name rule
/// (<see cref="CaseAttributionRules"/>) says a party IS one of the company's names; this says whether the company HAD that name when the case was filed.
/// <list type="bullet">
/// <item>A former name, filed on or before the company left it: the company's (the rule stands, with the dates in its reason).</item>
/// <item>A former name, filed after the company left it, and no other company has it today: still the company's — proceedings keep the old cause title.</item>
/// <item>A former name another company has today, filed after the change or on a date not known: not counted until judged
/// (<see cref="FormerNameCaseJudge"/>). Owner, 2026-10-25: held out, not counted and suggested.</item>
/// <item>The current name, filed before the company took it, when another company had it before: judged the same way.</item>
/// </list>
/// A name used twice (renamed and renamed back) is the company's up to the last day it used it.
/// </summary>
public static class FormerNameCaseRules
{
    public static bool IsFormerNameRule(string? rule) => rule is "exact-former-name" or "exact-former-name-representative";
    public static bool IsCurrentNameRule(string? rule) => rule is "exact" or "exact-representative";

    /// <summary>The last day the company used <paramref name="name"/>: the latest change away from it. Null when a change away from it has no date.</summary>
    public static DateOnly? UsedUntil(IReadOnlyList<NameChange> changes, string name)
    {
        var away = changes.Where(c => NameChanges.Key(c.FromName) == NameChanges.Key(name)).ToList();
        return away.Count == 0 || away.Any(c => c.Date is null) ? null : away.Max(c => c.Date);
    }

    /// <summary>The day the company took its current name: its latest change of name, else the day it was incorporated. Null when that is not known.</summary>
    public static DateOnly? CurrentSince(IReadOnlyList<NameChange> changes, DateOnly? incorporated)
    {
        if (changes.Count == 0) return incorporated;
        return changes.Any(c => c.Date is null) ? null : changes.Max(c => c.Date);
    }

    /// <summary>Whether another company having the name could change the outcome: a former name not shown to be the company's when the case was filed, or the
    /// current name before the company took it. Only then are other holders looked up (a query, and the reference tool's search where configured).</summary>
    public static bool MayBeAnothersName(NameAtFiling name, DateOnly? filed) => name.IsFormer
        ? !(filed is { } f && name.UsedUntil is { } until && f <= until)
        : filed is { } early && name.CurrentSince is { } since && early < since;

    public static FormerNameCaseCheck Check(NameAtFiling name, DateOnly? filed)
    {
        // Nobody else has the name only when the search for others was complete and found none.
        var nobodyElse = name.Others.Count == 0 && name.OthersKnown;
        var others = name.Others.Count > 0 ? string.Join("; ", name.Others.Select(o => $"{o.Name} ({o.Identifier})"))
            : "whether another company has it could not be fully checked";
        if (name.IsFormer)
        {
            if (filed is { } f && name.UsedUntil is { } until && f <= until)
                return new(FormerNameCaseDecision.Keep, null,
                    $"A party is the company's former name ({name.MatchedName}), which it used until {D(until)}; the case was filed on {D(f)}.");
            if (nobodyElse)
                return filed is { } after && name.UsedUntil is { } left
                    ? new(FormerNameCaseDecision.CarriedOn, "exact-former-name-carried-on",
                        $"A party is the company's former name ({name.MatchedName}), which it used until {D(left)}; the case was filed on {D(after)}, after the change, and no other company has that name.")
                    : new(FormerNameCaseDecision.Keep, null, $"A party is the company's former name ({name.MatchedName}).");
            var when = filed is { } fd && name.UsedUntil is { } u ? $"was filed on {D(fd)}, after the company left the name on {D(u)}" : "has no filing date that shows the company still had the name";
            var holder = name.Others.Count > 0 ? $"which another company has today ({others})" : $"of which {others}";
            return new(FormerNameCaseDecision.NeedsJudgement, null,
                $"A party is the company's former name ({name.MatchedName}), {holder}, and the case {when}; waiting for the judgement of whose case it is.");
        }

        if (!nobodyElse && filed is { } early && name.CurrentSince is { } since && early < since)
            return new(FormerNameCaseDecision.NeedsJudgement, null,
                $"A party is the company's name ({name.MatchedName}), but the case was filed on {D(early)}, before the company took it on {D(since)}, and "
                + (name.Others.Count > 0 ? $"another company had it before ({others})" : $"{others} before") + "; waiting for the judgement of whose case it is.");
        return new(FormerNameCaseDecision.Keep, null, "");
    }

    internal static string D(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>One case the rules hold out (<see cref="FormerNameCaseDecision.NeedsJudgement"/>), as the judgement is asked about it.</summary>
public sealed record FormerNameCaseQuestion(
    long CaseId, string Party, NameAtFiling Name, string? Court, string? CaseNumber, string? FilingDate, string? Place,
    IReadOnlyList<string> Petitioners, IReadOnlyList<string> Respondents);

/// <summary>
/// The judgement for a case whose party is a name the company has used and another company has (or had) too (#497 PR C): whose case is it, given when each
/// company had the name, where the case was filed and who the other parties are. One call per batch of cases; each answer counts only for a case id that
/// was asked, and a case left out or with an unknown verdict is <see cref="LitigationPartyVerdictKind.Unsure"/>, never the company's.
/// </summary>
public static class FormerNameCaseJudge
{
    public const string PromptMarker = "MCAROC FORMER NAME CASE";
    public const string PromptVersion = "former-name-case-v1";
    public const int BatchSize = 20;

    /// <summary>The verdict store's key for a case's judgement: per case, since the answer depends on the case, not only the party name.</summary>
    public static string VerdictKey(long caseId) => $"CASE {caseId}";

    public static string BuildPrompt(AttributionSubject subject, IReadOnlyList<NameChange> changes, IReadOnlyList<FormerNameCaseQuestion> cases)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{PromptMarker} ({PromptVersion})");
        sb.AppendLine("You decide whose case each Indian court case below is. In each, a party has a name this company has used, and another company has used it too.");
        sb.AppendLine();
        sb.AppendLine($"Company: {subject.LegalName}");
        if (!string.IsNullOrWhiteSpace(subject.Identifier)) sb.AppendLine($"Corporate identity number: {subject.Identifier}");
        if (!string.IsNullOrWhiteSpace(subject.RegisteredOffice)) sb.AppendLine($"Registered office: {subject.RegisteredOffice}");
        foreach (var change in changes.Where(c => c.Date is not null))
            sb.AppendLine($"Renamed from {change.FromName} to {change.ToName} on {FormerNameCaseRules.D(change.Date!.Value)}");
        sb.AppendLine();
        foreach (var (q, i) in cases.Select((q, i) => (q, i + 1)))
        {
            sb.AppendLine($"C{i}. {q.Court ?? "Court not given"}, case {q.CaseNumber ?? "number not given"}, filed {q.FilingDate ?? "on a date not given"}{(string.IsNullOrWhiteSpace(q.Place) ? "" : $", {q.Place}")}.");
            sb.AppendLine($"    Petitioners: {string.Join("; ", q.Petitioners)}");
            sb.AppendLine($"    Respondents: {string.Join("; ", q.Respondents)}");
            sb.AppendLine($"    Party in question: {q.Party}");
            sb.AppendLine(q.Name.IsFormer
                ? $"    This company used the name {q.Name.MatchedName} until {(q.Name.UsedUntil is { } u ? FormerNameCaseRules.D(u) : "a date not known")}. The name now belongs to: {Holders(q.Name)}."
                : $"    This company has had the name {q.Name.MatchedName} since {(q.Name.CurrentSince is { } s ? FormerNameCaseRules.D(s) : "a date not known")}. Before that it belonged to: {Holders(q.Name)}.");
        }
        sb.AppendLine();
        sb.AppendLine("""
            For each case answer:
            - "company": the party is this company (the case is about it under that name, for example filed while it had the name, or carried on under its old title).
            - "other": the party is the other company named, or someone else.
            - "unsure": it cannot be told from what is given.
            Weigh the filing date against when each company had the name, the court's place against the companies' offices, and the other parties.
            Give the case id exactly as given. Reason in at most 25 words.
            Answer with JSON only: {"cases": [{"id": "C1", "verdict": "company|other|unsure", "reason": "..."}]}
            """);
        return sb.ToString();

        static string Holders(NameAtFiling name) =>
            string.Join("; ", name.Others.Select(o => $"{o.Name} ({o.Identifier}{(string.IsNullOrWhiteSpace(o.State) ? "" : $", {o.State}")})")
                .Append(name.OthersKnown ? null : "possibly a company not found, as the search for companies with this name could not be completed")
                .Where(x => x is not null));
    }

    /// <summary>One judgement per case asked about, in the order asked.</summary>
    public static IReadOnlyList<(long CaseId, LitigationPartyVerdictKind Verdict, string Reason)> Read(IReadOnlyList<FormerNameCaseQuestion> asked, string raw)
    {
        var answers = new Dictionary<string, (LitigationPartyVerdictKind, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cases", out var c) ? c : root;
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var verdict = Str(e, "verdict").Trim().ToLowerInvariant() switch
                    {
                        "company" => LitigationPartyVerdictKind.Same,
                        "other" => LitigationPartyVerdictKind.Different,
                        _ => LitigationPartyVerdictKind.Unsure
                    };
                    var reason = Str(e, "reason").Trim();
                    answers.TryAdd(Str(e, "id").Trim(), (verdict, reason.Length > 300 ? reason[..300] : reason));
                }
        }
        catch (JsonException) { }

        return asked.Select((q, i) => answers.TryGetValue($"C{i + 1}", out var a)
                ? (q.CaseId, a.Item1, a.Item2.Length > 0 ? a.Item2 : "No reason given.")
                : (q.CaseId, LitigationPartyVerdictKind.Unsure, "The judgement did not answer for this case."))
            .ToList();

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    }
}
