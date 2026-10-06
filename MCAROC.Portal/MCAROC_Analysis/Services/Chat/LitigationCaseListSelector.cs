using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>A question that describes litigation cases by forum, status or the company's side rather than naming one ("the Supreme Court
/// cases against the company", "pending DRT cases"). Empty parts do not filter.</summary>
public sealed record CaseListFilter(
    IReadOnlyList<LitigationInstitution> Forums, LitigationCaseStatusBucket? Status, LitigationCompanySide? Side)
{
    public bool IsEmpty => Forums.Count == 0 && Status is null && Side is null;

    /// <summary>The filter in words, for the note that tells the model what the list is a list of.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Forums.Count > 0) parts.Add(string.Join(" or ", Forums.Select(LitigationCasePresentation.Label)));
        if (Status is { } status) parts.Add(status.ToString().ToLowerInvariant());
        if (Side is { } side) parts.Add(LitigationCasePresentation.SideLabel(side).ToLowerInvariant());
        return string.Join(", ", parts);
    }
}

/// <summary>Reads a case-list filter out of a question, and applies one to a report's cases exactly as the register and the report do (the same
/// forum, side and status rules, the same listing order), so the assistant's list and the page agree.</summary>
public static partial class LitigationCaseListSelector
{
    /// <summary>The most case records one answer carries. The note that accompanies them always states the true number of matches.</summary>
    public const int MaxListedCases = 20;

    public static CaseListFilter? Extract(string question)
    {
        var text = question.ToLowerInvariant();
        if (!CaseCueRegex().IsMatch(text)) return null;

        var forums = new List<LitigationInstitution>();
        void Forum(string pattern, LitigationInstitution institution)
        {
            if (Regex.IsMatch(text, pattern) && !forums.Contains(institution)) forums.Add(institution);
        }
        Forum(@"\bsupreme court\b|\bsc cases?\b|\bslps?\b", LitigationInstitution.SupremeCourt);
        Forum(@"\bhigh courts?\b", LitigationInstitution.HighCourt);
        Forum(@"\bdistrict (and sessions )?courts?\b|\bsessions courts?\b|\bmagistrates?\b", LitigationInstitution.DistrictCourt);
        Forum(@"\bconsumer (courts?|forum|commission)\b", LitigationInstitution.ConsumerCourt);
        Forum(@"\bcestat\b", LitigationInstitution.Cestat);
        Forum(@"\bnclat\b", LitigationInstitution.Nclat);
        Forum(@"\bnclt\b", LitigationInstitution.Nclt);
        Forum(@"\bitat\b", LitigationInstitution.Itat);
        Forum(@"\bdrat\b|\bdebts? recovery appellate\b", LitigationInstitution.Drat);
        Forum(@"\bdrt\b|\bdebts? recovery tribunal\b", LitigationInstitution.Drt);

        LitigationCaseStatusBucket? status =
            Regex.IsMatch(text, @"\b(pending|ongoing|open|live|undecided|currently)\b") ? LitigationCaseStatusBucket.Pending
            : Regex.IsMatch(text, @"\b(disposed|closed|concluded|decided)\b") ? LitigationCaseStatusBucket.Disposed
            : null;

        LitigationCompanySide? side =
            Regex.IsMatch(text, @"\bagainst (the )?(company|borrower|it\b|coastal\b)") ? LitigationCompanySide.AgainstCompany
            : Regex.IsMatch(text, @"\b(filed|brought|initiated|instituted|raised) by (the )?(company|borrower|it)\b|\bby the company\b|\bcompany (has )?(filed|initiated|instituted)\b")
                ? LitigationCompanySide.ByCompany
            : null;

        var filter = new CaseListFilter(forums, status, side);
        return filter.IsEmpty ? null : filter;
    }

    /// <summary>The cases that match, in the report's listing order, and how many matched in all.</summary>
    public static (IReadOnlyList<LitigationCase> Cases, int Total) Select(
        IEnumerable<LitigationCase> cases, CaseListFilter filter, IReadOnlyCollection<string> companyNames)
    {
        var matches = new List<(LitigationCase Case, (int, int, int, int, long) Key)>();
        foreach (var c in cases)
        {
            var bucket = LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage);
            var forum = LitigationCasePresentation.Resolve(c.Type, c.Court, c.CourtCategory);
            if (filter.Forums.Count > 0 && !filter.Forums.Contains(forum)) continue;
            if (filter.Status is { } status && bucket != status) continue;
            var side = LitigationCasePresentation.Side(c.Direction, c.PetitionersJson, c.RespondentsJson, companyNames);
            if (filter.Side is { } wanted && side != wanted) continue;
            matches.Add((c, LitigationCasePresentation.SortKey(bucket, forum, side, c.FilingDate, c.LitigationCaseId)));
        }
        var ordered = matches.OrderBy(m => m.Key).Select(m => m.Case).ToList();
        return (ordered.Take(MaxListedCases).ToList(), ordered.Count);
    }

    [GeneratedRegex(@"\b(cases?|litigations?|proceedings?|suits?|petitions?|appeals?|slps?|matters?)\b")]
    private static partial Regex CaseCueRegex();
}
