using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.LitigationModule.Discovery;

/// <summary>Discovers missing cases by party-name search across DRT/DRAT and consumer commissions (plan section 4.4).
/// Strictly read-only: applies WholeNamePartyMatcher to every candidate, separates accepted and rejected candidates,
/// logs rejected candidates for audit, and does NOT add anything to the case store.</summary>
public sealed class PartyDiscoverySearchService : IPartyDiscoverySearchService
{
    private readonly IDrtClient _drtClient;
    private readonly IConsumerClient _consumerClient;
    private readonly ILogger<PartyDiscoverySearchService> _logger;

    public PartyDiscoverySearchService(
        IDrtClient drtClient,
        IConsumerClient consumerClient,
        ILogger<PartyDiscoverySearchService> logger)
    {
        _drtClient = drtClient;
        _consumerClient = consumerClient;
        _logger = logger;
    }

    public async Task<PartyDiscoveryResult> DiscoverDrtCandidatesAsync(
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames = null,
        IReadOnlyList<int>? tribunalIds = null,
        CancellationToken ct = default)
    {
        var namesList = companyNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var formerList = (formerNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var primaryName = namesList.FirstOrDefault() ?? formerList.FirstOrDefault() ?? "Unknown";

        var queryNames = namesList.Concat(formerList).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (queryNames.Count == 0)
        {
            return new PartyDiscoveryResult(primaryName, [], 0, 0, 0, 0, ["No company names provided for discovery"]);
        }

        var targets = tribunalIds?.ToList();
        if (targets is null || targets.Count == 0)
        {
            try
            {
                var tribunals = await _drtClient.GetTribunalsAsync(ct);
                targets = tribunals.Select(t => t.Id).Distinct().ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch DRT tribunal list; falling back to default tribunal range");
                targets = DrtTribunalRegistry.GetAll().Select(t => t.Id).ToList();
            }
        }

        var candidates = new List<DiscoveredCaseCandidate>();
        var errors = new List<string>();
        var venuesSearched = 0;

        foreach (var tribId in targets)
        {
            ct.ThrowIfCancellationRequested();
            venuesSearched++;
            var venueName = DrtTribunalRegistry.TryGetById(tribId)?.Name ?? $"Tribunal {tribId}";
            var isDrat = tribId >= 100;
            var source = isDrat ? DiscoveredCaseSource.Drat : DiscoveredCaseSource.Drt;

            foreach (var queryName in queryNames)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var results = await _drtClient.SearchByPartyNameAsync(tribId, queryName, ct);
                    foreach (var row in results)
                    {
                        var caseNumberStr = !string.IsNullOrWhiteSpace(row.CaseNumber.Display)
                            ? row.CaseNumber.Display
                            : (row.RawCaseNo ?? string.Empty);

                        // Evaluate applicant and respondent under the whole-name rule
                        var applicantMatch = WholeNamePartyMatcher.Match(row.Applicant, namesList, formerList);
                        var respondentMatch = WholeNamePartyMatcher.Match(row.Respondent, namesList, formerList);

                        // Best match wins (Verified beats Ambiguous beats Rejected)
                        var bestMatch = PickBestMatch(applicantMatch, respondentMatch);
                        var isAccepted = bestMatch.Outcome == PartyMatchOutcome.Verified;

                        if (!isAccepted)
                        {
                            _logger.LogInformation(
                                "Audit: Discovered DRT candidate '{CaseNumber}' at tribunal {TribunalId} rejected: Rule={Rule}, Applicant='{Applicant}', Respondent='{Respondent}'",
                                caseNumberStr, tribId, bestMatch.Rule, row.Applicant, row.Respondent);
                        }

                        candidates.Add(new DiscoveredCaseCandidate(
                            Source: source,
                            SourceVenueId: tribId.ToString(),
                            SourceVenueName: venueName,
                            CaseNumber: caseNumberStr,
                            NormalizedCaseNumber: row.CaseNumber,
                            FilingDate: row.DateOfFiling,
                            ApplicantOrComplainant: row.Applicant,
                            Respondent: row.Respondent,
                            MatchResult: bestMatch,
                            QueriedPartyName: queryName,
                            IsAccepted: isAccepted,
                            DiaryNo: row.DiaryNo,
                            StableSourceId: row.FilingNo));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error searching DRT tribunal {TribunalId} for query '{QueryName}'", tribId, queryName);
                    errors.Add($"DRT tribunal {tribId} error: {ex.Message}");
                }
            }
        }

        // Deduplicate candidate instances only on decisive identity (case number, diary number, or stable source id).
        // Observations lacking decisive identity are preserved individually.
        var distinctCandidates = DeduplicateCandidates(candidates);

        var accepted = distinctCandidates.Count(c => c.IsAccepted);
        var rejected = distinctCandidates.Count - accepted;

        return new PartyDiscoveryResult(
            primaryName,
            distinctCandidates,
            distinctCandidates.Count,
            accepted,
            rejected,
            venuesSearched,
            errors);
    }

    public async Task<PartyDiscoveryResult> DiscoverConsumerCandidatesAsync(
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames = null,
        IReadOnlyList<(int CommissionTypeId, int CommissionId)>? commissions = null,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        CancellationToken ct = default)
    {
        var namesList = companyNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var formerList = (formerNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var primaryName = namesList.FirstOrDefault() ?? formerList.FirstOrDefault() ?? "Unknown";

        var queryNames = namesList.Concat(formerList).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (queryNames.Count == 0)
        {
            return new PartyDiscoveryResult(primaryName, [], 0, 0, 0, 0, ["No company names provided for discovery"]);
        }

        var targets = commissions?.ToList();
        if (targets is null || targets.Count == 0)
        {
            // Default: search all commissions across all tiers (NCDRC, SCDRC, and DCDRC district commissions)
            targets = ConsumerCommissionRegistry.GetAll()
                .Select(c => (c.CommissionTypeId, c.CommissionId))
                .Distinct()
                .ToList();
        }

        var from = fromDate ?? new DateOnly(2010, 1, 1);
        var to = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var candidates = new List<DiscoveredCaseCandidate>();
        var errors = new List<string>();
        var venuesSearched = 0;

        foreach (var (typeId, commId) in targets)
        {
            ct.ThrowIfCancellationRequested();
            venuesSearched++;
            var commInfo = ConsumerCommissionRegistry.GetById(commId);
            var venueName = commInfo?.CommissionName ?? $"Commission {commId}";

            foreach (var queryName in queryNames)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await _consumerClient.SearchByCompanyAsync(typeId, commId, queryName, from, to, ct);
                    if (!result.IsSuccess)
                    {
                        errors.Add($"Commission {commId} returned error: {result.ErrorMessage}");
                        continue;
                    }

                    foreach (var c in result.Cases)
                    {
                        var complainantMatch = WholeNamePartyMatcher.Match(c.ComplainantName, namesList, formerList);
                        var respondentMatch = WholeNamePartyMatcher.Match(c.RespondentName, namesList, formerList);

                        var bestMatch = PickBestMatch(complainantMatch, respondentMatch);
                        var isAccepted = bestMatch.Outcome == PartyMatchOutcome.Verified;

                        if (!isAccepted)
                        {
                            _logger.LogInformation(
                                "Audit: Discovered Consumer candidate '{CaseNumber}' at commission {CommissionId} rejected: Rule={Rule}, Complainant='{Complainant}', Respondent='{Respondent}'",
                                c.CaseNumber, commId, bestMatch.Rule, c.ComplainantName, c.RespondentName);
                        }

                        candidates.Add(new DiscoveredCaseCandidate(
                            Source: DiscoveredCaseSource.Consumer,
                            SourceVenueId: commId.ToString(),
                            SourceVenueName: c.CommissionName ?? venueName,
                            CaseNumber: c.CaseNumber,
                            NormalizedCaseNumber: c.NormalizedCaseNumber,
                            FilingDate: c.CaseFilingDate,
                            ApplicantOrComplainant: c.ComplainantName,
                            Respondent: c.RespondentName,
                            MatchResult: bestMatch,
                            QueriedPartyName: queryName,
                            IsAccepted: isAccepted,
                            Stage: c.CaseStageName,
                            HearingDate: c.EffectiveHearingDate,
                            DiaryNo: c.FileApplicationNumber,
                            RawPayload: c.RawJson));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error searching Consumer commission {CommissionId} for query '{QueryName}'", commId, queryName);
                    errors.Add($"Commission {commId} error: {ex.Message}");
                }
            }
        }

        var distinctCandidates = DeduplicateCandidates(candidates);

        var accepted = distinctCandidates.Count(c => c.IsAccepted);
        var rejected = distinctCandidates.Count - accepted;

        return new PartyDiscoveryResult(
            primaryName,
            distinctCandidates,
            distinctCandidates.Count,
            accepted,
            rejected,
            venuesSearched,
            errors);
    }

    public async Task<PartyDiscoveryResult> DiscoverAllCandidatesAsync(
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames = null,
        CancellationToken ct = default)
    {
        var drtTask = DiscoverDrtCandidatesAsync(companyNames, formerNames, null, ct);
        var consumerTask = DiscoverConsumerCandidatesAsync(companyNames, formerNames, null, null, null, ct);

        await Task.WhenAll(drtTask, consumerTask);

        var drtRes = await drtTask;
        var consumerRes = await consumerTask;

        var allCandidates = drtRes.Candidates.Concat(consumerRes.Candidates).ToList();
        var allErrors = drtRes.Errors.Concat(consumerRes.Errors).ToList();

        return new PartyDiscoveryResult(
            drtRes.SubjectName,
            allCandidates,
            allCandidates.Count,
            allCandidates.Count(c => c.IsAccepted),
            allCandidates.Count(c => !c.IsAccepted),
            drtRes.VenuesSearchedCount + consumerRes.VenuesSearchedCount,
            allErrors);
    }

    private static PartyMatchResult PickBestMatch(PartyMatchResult first, PartyMatchResult second)
    {
        if (first.Outcome == PartyMatchOutcome.Verified) return first;
        if (second.Outcome == PartyMatchOutcome.Verified) return second;
        if (first.Outcome == PartyMatchOutcome.Ambiguous) return first;
        if (second.Outcome == PartyMatchOutcome.Ambiguous) return second;
        if (first.Rule != "no-match") return first;
        if (second.Rule != "no-match") return second;
        return first;
    }

    /// <summary>Deduplicates candidates on decisive identity (case number, diary number, or stable source id).
    /// Preserves observations without decisive identity so distinct keyless cases are never falsely collapsed.</summary>
    private static IReadOnlyList<DiscoveredCaseCandidate> DeduplicateCandidates(IEnumerable<DiscoveredCaseCandidate> candidates)
    {
        var result = new List<DiscoveredCaseCandidate>();
        var groupedByKey = new Dictionary<string, List<DiscoveredCaseCandidate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            var key = GetDeduplicationKey(c);
            if (key is null)
            {
                // Keyless observation: preserve individually
                result.Add(c);
            }
            else
            {
                if (!groupedByKey.TryGetValue(key, out var list))
                {
                    list = [];
                    groupedByKey[key] = list;
                }
                list.Add(c);
            }
        }

        foreach (var group in groupedByKey.Values)
        {
            // Pick accepted match if any, else first
            result.Add(group.OrderByDescending(c => c.IsAccepted).First());
        }

        return result;
    }

    private static string? GetDeduplicationKey(DiscoveredCaseCandidate c)
    {
        // 1. Decisive complete case number (type, number, and explicit year established)
        if (c.NormalizedCaseNumber?.Completeness == CaseNumberCompleteness.Complete)
        {
            var caseText = !string.IsNullOrWhiteSpace(c.NormalizedCaseNumber.Display)
                ? c.NormalizedCaseNumber.Display.Trim()
                : c.CaseNumber.Trim();

            if (!string.IsNullOrWhiteSpace(caseText))
            {
                return $"{c.Source}:{c.SourceVenueId}:case:{caseText}";
            }
        }

        // 2. Stable source filing number if available
        if (!string.IsNullOrWhiteSpace(c.StableSourceId))
        {
            return $"{c.Source}:{c.SourceVenueId}:filing:{c.StableSourceId.Trim()}";
        }

        // 3. Decisive diary number
        if (!string.IsNullOrWhiteSpace(c.DiaryNo))
        {
            return $"{c.Source}:{c.SourceVenueId}:diary:{c.DiaryNo.Trim()}";
        }

        // Incomplete case numbers without stable filing or diary identity cannot establish
        // decisive equivalence; retain observations individually to prevent false collapse.
        return null;
    }
}

