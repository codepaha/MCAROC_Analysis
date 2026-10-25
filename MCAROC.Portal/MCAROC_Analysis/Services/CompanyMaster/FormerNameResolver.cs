using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>A company that was once registered under the requested name, as the reference tool's search reports it (a hit whose context is
/// <c>oldName</c>), or as a former name stored from an earlier search.</summary>
/// <param name="CurrentName">The company's legal name as the tool reported it; the master's current name is preferred when the master has the row.</param>
public sealed record FormerNameHit(string Identifier, string FormerName, string? CurrentName, string? Status, string? State, string? PinCode, DateOnly? IncorporationDate);

/// <summary>A company the search reports as holding the requested name today (context <c>legalName</c>); it may be missing from a stale master.</summary>
public sealed record CompanyHolder(string Identifier, string Name, string? Status, string? State, string? PinCode, DateOnly? IncorporationDate);

/// <summary>What a former-name lookup found for a requested name.
/// <paramref name="Searched"/> is false when nothing could be looked up (search not configured or failed): the master's decision then stands, as before.
/// <paramref name="Complete"/> is true only when the search was read to its end (or a stored observation records that it was): only then does
/// "exactly one company had the name, and none has it today" hold, and only then may a company be chosen automatically.</summary>
public sealed record FormerNameLookup(bool Searched, bool Complete, IReadOnlyList<FormerNameHit> FormerNames, IReadOnlyList<CompanyHolder> CurrentHolders)
{
    public static readonly FormerNameLookup NotSearched = new(false, false, [], []);
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
        if (former.Count == 0) return Unconfirmed(master, lookup);

        var masterHolders = master.Candidates.Where(c => c.Features.GetValueOrDefault("ExactNormalized") == 1)
            .Select(c => c.Candidate.Identifier.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var currentHolders = masterHolders.Concat(lookup.CurrentHolders.Select(h => h.Identifier.Trim().ToUpperInvariant())).ToHashSet(StringComparer.Ordinal);
        var formerOthers = former.Where(f => !currentHolders.Contains(f.Identifier)).ToList();
        if (formerOthers.Count == 0) return Unconfirmed(master, lookup); // the company that holds the name today also held it before: nothing new

        var formerCandidates = formerOthers.Select(f => Candidate(f, inputName, hints, masterRows.GetValueOrDefault(f.Identifier), lookup.Complete)).ToList();
        // A company holding the name today that only the search knows about is a choice too: the client may mean it.
        var searchOnlyHolders = lookup.CurrentHolders
            .Where(h => !masterHolders.Contains(h.Identifier.Trim().ToUpperInvariant()))
            .DistinctBy(h => h.Identifier.Trim().ToUpperInvariant())
            .Select(h => Holder(h, hints, masterRows.GetValueOrDefault(h.Identifier.Trim().ToUpperInvariant()))).ToList();
        var shown = formerCandidates.Concat(searchOnlyHolders)
            .Concat(master.Candidates.Where(c => !formerOthers.Any(f => f.Identifier == c.Candidate.Identifier)))
            .Take(Math.Max(options.MaxCandidates, formerCandidates.Count + searchOnlyHolders.Count)).ToList();

        if (currentHolders.Count > 0)
            return new(ResolutionStatus.Ambiguous, null, null, null, false, master.TopScore, null, ResolutionReasonCodes.FormerNameConflict, master.NormalizedInput, shown);
        if (formerCandidates.Count > 1)
            return new(ResolutionStatus.Ambiguous, null, null, null, false, 1.0, 0, ResolutionReasonCodes.FormerNameAmbiguous, master.NormalizedInput, shown);

        var only = formerCandidates[0];
        // Only a search read to its end shows that no other company had the name, and none has it today.
        var trustworthy = lookup.Complete && !only.Candidate.IsToolOnly && CompanyNameResolver.IsActive(only.Candidate.Status)
            && only.Features.GetValueOrDefault("HintsDisagreed") == 0;
        if (trustworthy && options.AutoSelectEnabled && options.AutoSelectFormerName)
            return new(ResolutionStatus.Resolved, ResolutionMethod.AutoSelected, only.Candidate.Identifier, only.Candidate.Identifier, true,
                1.0, 1.0, ResolutionReasonCodes.FormerName, master.NormalizedInput, shown);
        return new(ResolutionStatus.NeedsConfirmation, null, null, only.Candidate.Identifier, trustworthy, 1.0, 1.0,
            ResolutionReasonCodes.FormerNameNeedsConfirmation, master.NormalizedInput, shown);
    }

    /// <summary>The master's own decision, except that an automatic pick needs the search to have been read to its end: a company that had the name
    /// before may be on a page that was not read, and the guard above can only see what was read. Then the pick is offered, filled in, instead.</summary>
    private static ResolutionDecision Unconfirmed(ResolutionDecision master, FormerNameLookup lookup) =>
        lookup.Complete || master is not { Status: ResolutionStatus.Resolved, Method: ResolutionMethod.AutoSelected }
            ? master
            : master with
            {
                Status = ResolutionStatus.NeedsConfirmation, Method = null, ChosenIdentifier = null,
                RecommendedIdentifier = master.ChosenIdentifier, AutoSelectEligible = false, ReasonCode = ResolutionReasonCodes.FormerNameCheckIncomplete
            };

    /// <summary>The company as a candidate: the master's row when it has one (current name, status, address), else what the search reported, marked
    /// tool-only. Scored for hints against the master's data, so a requester's state or year that disagrees still stops an automatic choice.</summary>
    private static ScoredCandidate Candidate(FormerNameHit hit, string inputName, ResolutionHints hints, MasterCandidate? row, bool searchComplete)
    {
        var candidate = row ?? new MasterCandidate(hit.Identifier, hit.Identifier.Contains('-') ? CompanyMasterRecordType.Llp : CompanyMasterRecordType.Company,
            hit.CurrentName ?? hit.Identifier, Status: hit.Status, State: hit.State, PinCode: hit.PinCode, RegistrationDate: hit.IncorporationDate, IsToolOnly: true);
        var scored = CompanyNameResolver.Rank(candidate.Name, hints, [candidate])[0];
        var features = new Dictionary<string, double>(scored.Features, StringComparer.Ordinal) { ["FormerNameExact"] = 1, ["ExactNormalized"] = 0 };
        var reasons = new List<string> { $"registered as \"{hit.FormerName.Trim()}\" before; registered name now \"{candidate.Name}\"" };
        if (!searchComplete)
            reasons.Add("the reference tool's search could not be read to its end, so another company with this name cannot be ruled out");
        reasons.AddRange(scored.Reasons.Where(r => r != "exact normalized name"));
        return new ScoredCandidate(candidate, 1.0, features, reasons);
    }

    /// <summary>A company holding the requested name today that the master does not list as such (a stale master), shown so it can be chosen.</summary>
    private static ScoredCandidate Holder(CompanyHolder holder, ResolutionHints hints, MasterCandidate? row)
    {
        var candidate = row ?? new MasterCandidate(holder.Identifier.Trim().ToUpperInvariant(),
            holder.Identifier.Contains('-') ? CompanyMasterRecordType.Llp : CompanyMasterRecordType.Company, holder.Name,
            Status: holder.Status, State: holder.State, PinCode: holder.PinCode, RegistrationDate: holder.IncorporationDate, IsToolOnly: true);
        var scored = CompanyNameResolver.Rank(candidate.Name, hints, [candidate])[0];
        var features = new Dictionary<string, double>(scored.Features, StringComparer.Ordinal) { ["CurrentHolderBySearch"] = 1 };
        var reasons = new List<string> { "has this name today, by the reference tool's search" };
        reasons.AddRange(scored.Reasons.Where(r => r != "exact normalized name"));
        return new ScoredCandidate(candidate, 1.0, features, reasons);
    }
}
