using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>A company that was once registered under the requested name, as the reference tool's search reports it (a hit whose context is
/// <c>oldName</c>), or as a former name stored from an earlier search.</summary>
/// <param name="CurrentName">The company's legal name as the tool reported it; the master's current name is preferred when the master has the row.</param>
public sealed record FormerNameHit(string Identifier, string FormerName, string? CurrentName, string? Status, string? State, string? PinCode, DateOnly? IncorporationDate);

/// <summary>What a former-name lookup found for a requested name. <paramref name="CurrentHolders"/> are companies the search reports as holding the
/// name today (context <c>legalName</c>); they may be missing from a stale master. <paramref name="Searched"/> is false when nothing could be looked up
/// (search not configured or failed): the master's decision then stands, as before.</summary>
public sealed record FormerNameLookup(bool Searched, IReadOnlyList<FormerNameHit> FormerNames, IReadOnlyList<string> CurrentHolders)
{
    public static readonly FormerNameLookup NotSearched = new(false, [], []);
}

/// <summary>Resolution by former name (#497), the pure part: given the master's decision for a requested name and what a former-name lookup found,
/// decide again. Clients sometimes ask for a company by a name it no longer has; our master holds current names only.
/// <list type="bullet">
/// <item><b>Found by its former name:</b> no company holds the requested name today (in the master or the search), and exactly one company held it
/// before. It is selected automatically only with <see cref="ResolverOptions.AutoSelectFormerName"/> on, and when it is Active and no hint
/// disagrees; otherwise it is offered for confirmation, filled in.</item>
/// <item><b>Guard on the current-name rules (#495):</b> when a company holds the name today <i>and</i> a different company held it before, nothing is
/// chosen automatically: the client may mean either, and both are offered.</item>
/// <item>Several companies held the name before: offered together, nothing chosen.</item>
/// </list>
/// Nothing found, or the lookup unavailable: the master's decision stands unchanged.</summary>
public static class FormerNameResolver
{
    public static ResolutionDecision Decide(string inputName, ResolutionHints hints, ResolutionDecision master, FormerNameLookup lookup,
        IReadOnlyDictionary<string, MasterCandidate> masterRows, ResolverOptions options)
    {
        if (!lookup.Searched || master.Status == ResolutionStatus.InvalidInput) return master;
        var input = CompanyNameNormalizer.Normalize(inputName);
        if (input.NameNormalized.Length == 0) return master;

        var former = lookup.FormerNames
            .Where(h => CompanyNameNormalizer.Normalize(h.FormerName).NameNormalized == input.NameNormalized)
            .GroupBy(h => h.Identifier.Trim().ToUpperInvariant()).Select(g => g.First() with { Identifier = g.Key })
            .ToList();
        if (former.Count == 0) return master;

        var currentHolders = master.Candidates.Where(c => c.Features.GetValueOrDefault("ExactNormalized") == 1)
            .Select(c => c.Candidate.Identifier.Trim().ToUpperInvariant())
            .Concat(lookup.CurrentHolders.Select(i => i.Trim().ToUpperInvariant()))
            .ToHashSet(StringComparer.Ordinal);
        var formerOthers = former.Where(f => !currentHolders.Contains(f.Identifier)).ToList();
        if (formerOthers.Count == 0) return master; // the company that holds the name today also held it before: nothing new

        var formerCandidates = formerOthers.Select(f => Candidate(f, inputName, hints, masterRows.GetValueOrDefault(f.Identifier))).ToList();
        var shown = formerCandidates.Concat(master.Candidates.Where(c => !formerOthers.Any(f => f.Identifier == c.Candidate.Identifier)))
            .Take(Math.Max(options.MaxCandidates, formerCandidates.Count)).ToList();

        if (currentHolders.Count > 0)
            return new(ResolutionStatus.Ambiguous, null, null, null, false, master.TopScore, null, ResolutionReasonCodes.FormerNameConflict, master.NormalizedInput, shown);
        if (formerCandidates.Count > 1)
            return new(ResolutionStatus.Ambiguous, null, null, null, false, 1.0, 0, ResolutionReasonCodes.FormerNameAmbiguous, master.NormalizedInput, shown);

        var only = formerCandidates[0];
        var trustworthy = !only.Candidate.IsToolOnly && CompanyNameResolver.IsActive(only.Candidate.Status)
            && only.Features.GetValueOrDefault("HintsDisagreed") == 0;
        if (trustworthy && options.AutoSelectEnabled && options.AutoSelectFormerName)
            return new(ResolutionStatus.Resolved, ResolutionMethod.AutoSelected, only.Candidate.Identifier, only.Candidate.Identifier, true,
                1.0, 1.0, ResolutionReasonCodes.FormerName, master.NormalizedInput, shown);
        return new(ResolutionStatus.NeedsConfirmation, null, null, only.Candidate.Identifier, trustworthy, 1.0, 1.0,
            ResolutionReasonCodes.FormerNameNeedsConfirmation, master.NormalizedInput, shown);
    }

    /// <summary>The company as a candidate: the master's row when it has one (current name, status, address), else what the search reported, marked
    /// tool-only. Scored for hints against the master's data, so a requester's state or year that disagrees still stops an automatic choice.</summary>
    private static ScoredCandidate Candidate(FormerNameHit hit, string inputName, ResolutionHints hints, MasterCandidate? row)
    {
        var candidate = row ?? new MasterCandidate(hit.Identifier, hit.Identifier.Contains('-') ? CompanyMasterRecordType.Llp : CompanyMasterRecordType.Company,
            hit.CurrentName ?? hit.Identifier, Status: hit.Status, State: hit.State, PinCode: hit.PinCode, RegistrationDate: hit.IncorporationDate, IsToolOnly: true);
        var scored = CompanyNameResolver.Rank(candidate.Name, hints, [candidate])[0];
        var features = new Dictionary<string, double>(scored.Features, StringComparer.Ordinal) { ["FormerNameExact"] = 1, ["ExactNormalized"] = 0 };
        var reasons = new List<string> { $"registered as \"{hit.FormerName.Trim()}\" before; registered name now \"{candidate.Name}\"" };
        reasons.AddRange(scored.Reasons.Where(r => r != "exact normalized name"));
        return new ScoredCandidate(candidate, 1.0, features, reasons);
    }
}
