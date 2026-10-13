using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

namespace MCAROC_Analysis.Services.LitigationModule.Discovery;

/// <summary>Contract for searching missing cases across court portals by party name (plan section 4.4, phase 4).
/// Returns candidates evaluated under the whole-name acceptance rule without adding anything to the case store.</summary>
public interface IPartyDiscoverySearchService
{
    /// <summary>Discovers candidates across DRT and DRAT tribunals for given company names/aliases.</summary>
    /// <param name="searchTerms">The names to search with (#489: <c>LitigationKeywordSuggester.ForPortals</c>); null searches the company and former names.
    /// Candidates are still matched against the company and former names only.</param>
    Task<PartyDiscoveryResult> DiscoverDrtCandidatesAsync(
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames = null,
        IReadOnlyList<int>? tribunalIds = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? searchTerms = null);

    /// <summary>Discovers candidates across Consumer commissions for given company names/aliases.</summary>
    /// <param name="searchTerms">The names to search with (#489: <c>LitigationKeywordSuggester.ForPortals</c>); null searches the company and former names.
    /// Candidates are still matched against the company and former names only.</param>
    Task<PartyDiscoveryResult> DiscoverConsumerCandidatesAsync(
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames = null,
        IReadOnlyList<(int CommissionTypeId, int CommissionId)>? commissions = null,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? searchTerms = null);

    /// <summary>Discovers candidates across all portals (DRT, DRAT, Consumer commissions).</summary>
    /// <param name="searchTerms">The names to search with (#489: <c>LitigationKeywordSuggester.ForPortals</c>); null searches the company and former names.
    /// Candidates are still matched against the company and former names only.</param>
    Task<PartyDiscoveryResult> DiscoverAllCandidatesAsync(
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? searchTerms = null);
}

