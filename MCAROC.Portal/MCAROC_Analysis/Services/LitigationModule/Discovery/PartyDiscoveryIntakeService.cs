using System.Globalization;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.LitigationModule.Discovery;

/// <summary>Turns discovered candidate rows from party search into request cases (plan section 4.4 flow 2).
/// Implements candidate acceptance, party verification, identity gate evaluation, duplicate detection,
/// single detail fetching, per-case locking, disposed case routing, and batched live-data versioning.</summary>
public sealed class PartyDiscoveryIntakeService : IPartyDiscoveryIntakeService
{
    private readonly AppDbContext _db;
    private readonly IDrtClient _drtClient;
    private readonly IConsumerClient _consumerClient;
    private readonly IPartyDiscoverySearchService? _searchService;
    private readonly ILogger<PartyDiscoveryIntakeService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly LitigationModuleStore _moduleStore;
    private readonly LiveProjectionStore _liveProjectionStore;
    private readonly RequestLinkStore _requestLinkStore;

    public PartyDiscoveryIntakeService(
        AppDbContext db,
        IDrtClient drtClient,
        IConsumerClient consumerClient,
        ILogger<PartyDiscoveryIntakeService> logger,
        TimeProvider? timeProvider = null,
        IPartyDiscoverySearchService? searchService = null)
    {
        _db = db;
        _drtClient = drtClient;
        _consumerClient = consumerClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _searchService = searchService;
        _moduleStore = new LitigationModuleStore(_db, _timeProvider);
        _liveProjectionStore = new LiveProjectionStore(_db, _timeProvider);
        _requestLinkStore = new RequestLinkStore(_db, _timeProvider);
    }

    public async Task<PartyDiscoveryIntakeBatchResult> DiscoverAndIntakeAsync(
        long requestId,
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames = null,
        CancellationToken ct = default)
    {
        if (_searchService is null)
        {
            throw new InvalidOperationException("IPartyDiscoverySearchService was not provided to PartyDiscoveryIntakeService.");
        }

        var searchResult = await _searchService.DiscoverAllCandidatesAsync(companyNames, formerNames, ct);
        return await IntakeCandidatesAsync(requestId, companyNames, formerNames, searchResult.Candidates, ct);
    }

    public async Task<PartyDiscoveryIntakeBatchResult> IntakeCandidatesAsync(
        long requestId,
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames,
        IEnumerable<DiscoveredCaseCandidate> candidates,
        CancellationToken ct = default)
    {
        var subjectNamesList = companyNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var formerNamesList = (formerNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var candidateList = candidates.ToList();
        if (candidateList.Count == 0 || subjectNamesList.Count == 0)
        {
            return new PartyDiscoveryIntakeBatchResult(
                requestId,
                TotalCandidatesProcessed: 0,
                CreatedCount: 0,
                AlreadyExistingCount: 0,
                RejectedCount: 0,
                PossibleDuplicatesCount: 0,
                NewLiveDataVersion: null,
                Items: []);
        }

        var items = new List<DiscoveredCaseIntakeItemResult>();
        var seenKeysInBatch = new Dictionary<string, DiscoveredCaseIntakeItemResult>(StringComparer.OrdinalIgnoreCase);
        var createdCount = 0;
        var alreadyExistingCount = 0;
        var rejectedCount = 0;
        var possibleDuplicatesCount = 0;

        var todayIst = DateOnly.FromDateTime(Ist.Now(_timeProvider));
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var candidate in candidateList)
        {
            ct.ThrowIfCancellationRequested();

            // 1. Whole-name party matching against current and former names
            var applicantMatch = WholeNamePartyMatcher.Match(candidate.ApplicantOrComplainant, subjectNamesList, formerNamesList);
            var respondentMatch = WholeNamePartyMatcher.Match(candidate.Respondent, subjectNamesList, formerNamesList);
            var bestMatch = PickBestMatch(applicantMatch, respondentMatch);

            if (bestMatch.Outcome != PartyMatchOutcome.Verified)
            {
                _logger.LogInformation(
                    "Audit: Discovered candidate '{CaseNumber}' ({Source}) at venue {Venue} rejected for request {RequestId}: Rule={Rule}, Outcome={Outcome}, MatchedParty='{Party}', Applicant='{Applicant}', Respondent='{Respondent}'",
                    candidate.CaseNumber, candidate.Source, candidate.SourceVenueId, requestId, bestMatch.Rule, bestMatch.Outcome, bestMatch.MatchedParty, candidate.ApplicantOrComplainant, candidate.Respondent);

                var status = bestMatch.Outcome == PartyMatchOutcome.Ambiguous
                    ? IntakeCandidateStatus.PartyAmbiguous
                    : IntakeCandidateStatus.PartyRejected;

                items.Add(new DiscoveredCaseIntakeItemResult(
                    candidate,
                    status,
                    CaseKey: null,
                    LegacyLitigationCaseId: null,
                    ModuleCaseId: null,
                    IsDisposed: false,
                    RefreshAction: LiveRefreshAction.Skip,
                    Note: $"Rejected under whole-name rule: {bestMatch.Outcome} ({bestMatch.Rule})"));
                rejectedCount++;
                continue;
            }

            var matchedSide = applicantMatch.Outcome == PartyMatchOutcome.Verified
                ? "petitioner"
                : respondentMatch.Outcome == PartyMatchOutcome.Verified
                    ? "respondent"
                    : null;

            // 2. Identity gate (CaseKeyGate)
            var parts = candidate.NormalizedCaseNumber ?? CaseNumberNormaliser.Parse(new CaseNumberInput(candidate.CaseNumber, FilingDate: candidate.FilingDate?.ToString("dd-MM-yyyy")));
            var forum = candidate.Source switch
            {
                DiscoveredCaseSource.Drt => "DRT",
                DiscoveredCaseSource.Drat => "DRAT",
                DiscoveredCaseSource.Consumer => "CONSUMER",
                _ => candidate.Source.ToString().ToUpperInvariant()
            };

            var gateInput = new CaseKeyInput(forum, candidate.SourceVenueId, parts);
            var gateResult = CaseKeyGate.Evaluate(gateInput);

            if (gateResult.Key is null)
            {
                _logger.LogInformation(
                    "Audit: Discovered candidate '{CaseNumber}' for request {RequestId} failed CaseKeyGate ({Reason}); logged as possible duplicate, not keyed or merged",
                    candidate.CaseNumber, requestId, gateResult.Reason);

                items.Add(new DiscoveredCaseIntakeItemResult(
                    candidate,
                    IntakeCandidateStatus.FailedGatePartial,
                    CaseKey: null,
                    LegacyLitigationCaseId: null,
                    ModuleCaseId: null,
                    IsDisposed: false,
                    RefreshAction: LiveRefreshAction.Skip,
                    Note: $"Failed CaseKeyGate: {gateResult.Reason}"));
                possibleDuplicatesCount++;
                continue;
            }

            var caseKey = gateResult.Key;

            // 3. De-duplication check:
            // 3a. In-batch check
            if (seenKeysInBatch.TryGetValue(caseKey, out var earlierInBatch))
            {
                _logger.LogInformation(
                    "Discovered candidate '{CaseNumber}' (key: {CaseKey}) already processed in batch as legacy case {ExistingId} in request {RequestId}; skipping addition",
                    candidate.CaseNumber, caseKey, earlierInBatch.LegacyLitigationCaseId, requestId);

                items.Add(new DiscoveredCaseIntakeItemResult(
                    candidate,
                    IntakeCandidateStatus.AlreadyExists,
                    caseKey,
                    earlierInBatch.LegacyLitigationCaseId,
                    earlierInBatch.ModuleCaseId,
                    earlierInBatch.IsDisposed,
                    earlierInBatch.RefreshAction,
                    Note: "Already exists (duplicate in batch)"));
                alreadyExistingCount++;
                continue;
            }

            // 3b. Database check against existing request cases
            var existingCase = await (from lc in _db.LitigationCases.AsNoTracking()
                                      where lc.RequestId == requestId && lc.MergedIntoCaseId == null
                                      join mc in _db.Set<ModuleCase>().AsNoTracking() on lc.LitigationCaseId equals mc.LegacyLitigationCaseId
                                      where mc.CaseKey == caseKey
                                      select new { lc.LitigationCaseId, mc.CaseId, lc.CaseStatus, lc.CaseStage }).FirstOrDefaultAsync(ct);

            if (existingCase is not null)
            {
                _logger.LogInformation(
                    "Discovered candidate '{CaseNumber}' (key: {CaseKey}) already exists as legacy case {ExistingId} in request {RequestId}; skipping addition",
                    candidate.CaseNumber, caseKey, existingCase.LitigationCaseId, requestId);

                var isExistingDisposed = LitigationCaseStatusClassifier.Classify(existingCase.CaseStatus, existingCase.CaseStage) == LitigationCaseStatusBucket.Disposed;
                var existingRefreshAction = isExistingDisposed ? LiveRefreshAction.FetchOrdersOnly : LiveRefreshAction.Skip;

                var existingItem = new DiscoveredCaseIntakeItemResult(
                    candidate,
                    IntakeCandidateStatus.AlreadyExists,
                    caseKey,
                    existingCase.LitigationCaseId,
                    existingCase.CaseId,
                    isExistingDisposed,
                    existingRefreshAction,
                    Note: "Already exists in request case store");

                items.Add(existingItem);
                seenKeysInBatch[caseKey] = existingItem;
                alreadyExistingCount++;
                continue;
            }

            // 4. Concurrency lock: take lock for the case key to prevent concurrent creation races
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await CaseLock.AcquireAsync(_db, $"discovery:{requestId}:{caseKey}", ct);

            // Re-check existence under the lock
            var existingUnderLock = await (from lc in _db.LitigationCases
                                           where lc.RequestId == requestId && lc.MergedIntoCaseId == null
                                           join mc in _db.Set<ModuleCase>() on lc.LitigationCaseId equals mc.LegacyLitigationCaseId
                                           where mc.CaseKey == caseKey
                                           select new { lc.LitigationCaseId, mc.CaseId, lc.CaseStatus, lc.CaseStage }).FirstOrDefaultAsync(ct);

            if (existingUnderLock is not null)
            {
                await tx.RollbackAsync(ct);
                _logger.LogInformation(
                    "Discovered candidate '{CaseNumber}' (key: {CaseKey}) was created concurrently as case {ExistingId}; skipping addition",
                    candidate.CaseNumber, caseKey, existingUnderLock.LitigationCaseId);

                var isExistingDisposed = LitigationCaseStatusClassifier.Classify(existingUnderLock.CaseStatus, existingUnderLock.CaseStage) == LitigationCaseStatusBucket.Disposed;
                var existingRefreshAction = isExistingDisposed ? LiveRefreshAction.FetchOrdersOnly : LiveRefreshAction.Skip;

                var existingItem = new DiscoveredCaseIntakeItemResult(
                    candidate,
                    IntakeCandidateStatus.AlreadyExists,
                    caseKey,
                    existingUnderLock.LitigationCaseId,
                    existingUnderLock.CaseId,
                    isExistingDisposed,
                    existingRefreshAction,
                    Note: "Already exists (created concurrently)");

                items.Add(existingItem);
                seenKeysInBatch[caseKey] = existingItem;
                alreadyExistingCount++;
                continue;
            }

            // 5. Make one detail call per genuinely new case and map
            ParsedCaseSnapshot? snapshot = null;
            PartyMatchResult? detailMatch = null;
            string? detailMatchedSide = null;
            ObservationSource obsSource;
            DrtTribunal? drtTribunal = null;
            ConsumerCommission? consumerCommission = null;

            try
            {
                if (candidate.Source is DiscoveredCaseSource.Drt or DiscoveredCaseSource.Drat)
                {
                    obsSource = ObservationSource.DrtPortal;
                    var isDrat = candidate.Source == DiscoveredCaseSource.Drat;
                    var tribId = int.TryParse(candidate.SourceVenueId, out var parsedTribId) ? parsedTribId : 0;
                    drtTribunal = DrtTribunalRegistry.TryGetById(tribId);

                    DrtCaseLookupResult detailResult = null!;
                    var caseTypeId = parts.TypeCode is not null ? DrtCaseTypeRegistry.GetCaseTypeId(parts.TypeCode, isDrat) : null;
                    if (tribId > 0 && caseTypeId.HasValue && parts.No is not null && parts.Year.HasValue)
                    {
                        detailResult = await _drtClient.GetCaseByNumberAsync(tribId, caseTypeId.Value, parts.No, parts.Year.Value, ct);
                    }

                    if ((detailResult is null || !detailResult.IsFound) && !string.IsNullOrWhiteSpace(candidate.DiaryNo))
                    {
                        var diaryParts = candidate.DiaryNo.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (diaryParts.Length == 2 && int.TryParse(diaryParts[1], out var diaryYear))
                        {
                            detailResult = await _drtClient.GetCaseByDiaryAsync(tribId, diaryParts[0], diaryYear, ct);
                        }
                    }

                    if (detailResult is null || !detailResult.IsFound || detailResult.Case is null)
                    {
                        _logger.LogWarning("Detail call for DRT candidate '{CaseNumber}' at tribunal {TribunalId} failed or returned not found: {Msg}", candidate.CaseNumber, tribId, detailResult?.Message);
                        await tx.RollbackAsync(ct);
                        items.Add(new DiscoveredCaseIntakeItemResult(candidate, IntakeCandidateStatus.DetailFetchFailed, caseKey, null, null, false, LiveRefreshAction.Skip, detailResult?.Message ?? "Detail lookup failed"));
                        continue;
                    }

                    var lookup = new DrtLookup(tribId, parts);
                    var mapped = DrtObservationMapper.Map(detailResult, lookup, drtTribunal, subjectNamesList, formerNamesList, storedDirection: null, todayIst);
                    if (!mapped.Verified || mapped.Outcome != ObservationOutcome.Found)
                    {
                        _logger.LogWarning("DRT candidate '{CaseNumber}' identity or party verification failed: {Reason}", candidate.CaseNumber, mapped.Message ?? mapped.Verification?.IdentityReason);
                        await tx.RollbackAsync(ct);
                        items.Add(new DiscoveredCaseIntakeItemResult(candidate, IntakeCandidateStatus.IdentityMismatch, caseKey, null, null, false, LiveRefreshAction.Skip, mapped.Message ?? mapped.Verification?.IdentityReason));
                        continue;
                    }

                    snapshot = mapped.Snapshot;
                    detailMatch = mapped.Verification?.Party;
                    detailMatchedSide = mapped.Verification?.MatchedSide;
                }
                else if (candidate.Source == DiscoveredCaseSource.Consumer)
                {
                    obsSource = ObservationSource.ConsumerPortal;
                    var commId = int.TryParse(candidate.SourceVenueId, out var parsedCommId) ? parsedCommId : 0;
                    consumerCommission = ConsumerCommissionRegistry.GetById(commId);

                    var detailResult = await _consumerClient.GetCaseStatusAsync(candidate.CaseNumber, ct);
                    if (!detailResult.IsFound || detailResult.Case is null)
                    {
                        _logger.LogWarning("Detail call for Consumer candidate '{CaseNumber}' failed or returned not found: {Msg}", candidate.CaseNumber, detailResult.Message);
                        await tx.RollbackAsync(ct);
                        items.Add(new DiscoveredCaseIntakeItemResult(candidate, IntakeCandidateStatus.DetailFetchFailed, caseKey, null, null, false, LiveRefreshAction.Skip, detailResult.Message ?? "Detail lookup failed"));
                        continue;
                    }

                    var lookup = new ConsumerLookup(commId, parts);
                    var mapped = ConsumerObservationMapper.Map(detailResult, lookup, subjectNamesList, formerNamesList, storedDirection: null, todayIst);
                    if (!mapped.Verified || mapped.Outcome != ObservationOutcome.Found)
                    {
                        _logger.LogWarning("Consumer candidate '{CaseNumber}' identity or party verification failed: {Reason}", candidate.CaseNumber, mapped.Message ?? mapped.Verification?.IdentityReason);
                        await tx.RollbackAsync(ct);
                        items.Add(new DiscoveredCaseIntakeItemResult(candidate, IntakeCandidateStatus.IdentityMismatch, caseKey, null, null, false, LiveRefreshAction.Skip, mapped.Message ?? mapped.Verification?.IdentityReason));
                        continue;
                    }

                    snapshot = mapped.Snapshot;
                    detailMatch = mapped.Verification?.Party;
                    detailMatchedSide = mapped.Verification?.MatchedSide;
                }
                else
                {
                    await tx.RollbackAsync(ct);
                    items.Add(new DiscoveredCaseIntakeItemResult(candidate, IntakeCandidateStatus.DetailFetchFailed, caseKey, null, null, false, LiveRefreshAction.Skip, "Unsupported source"));
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                await tx.RollbackAsync(ct);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error fetching details for discovered candidate '{CaseNumber}'", candidate.CaseNumber);
                await tx.RollbackAsync(ct);
                items.Add(new DiscoveredCaseIntakeItemResult(candidate, IntakeCandidateStatus.DetailFetchFailed, caseKey, null, null, false, LiveRefreshAction.Skip, ex.Message));
                continue;
            }

            // 6. Create case row, module case, link, observation and publish
            var statusBucket = LitigationCaseStatusClassifier.Classify(snapshot?.Status, snapshot?.Stage);
            var isDisposed = statusBucket == LitigationCaseStatusBucket.Disposed;

            var courtName = snapshot?.Court ?? candidate.SourceVenueName;
            var state = drtTribunal?.State ?? consumerCommission?.StateName;
            var district = drtTribunal?.District ?? consumerCommission?.DistrictName;
            var bench = drtTribunal?.Bench;

            var legacyCase = new LitigationCase
            {
                RequestId = requestId,
                CaseNumber = candidate.CaseNumber,
                CaseType = parts.TypeCode,
                CaseYear = parts.Year?.ToString(CultureInfo.InvariantCulture),
                Court = courtName,
                CaseStatus = snapshot?.Status ?? (isDisposed ? "DISPOSED" : "PENDING"),
                CaseStage = snapshot?.Stage,
                FilingDate = snapshot?.FilingDate,
                LastHearingDate = snapshot?.LastHearingDate,
                NextHearingDate = snapshot?.NextHearingDate,
                DecisionDate = snapshot?.DecisionDate,
                FirstSeenUtc = nowUtc,
                LastSeenUtc = nowUtc,
                State = state,
                District = district,
                Bench = bench,
                PetitionersJson = JsonSerializer.Serialize(new[] { candidate.ApplicantOrComplainant }.Where(p => !string.IsNullOrWhiteSpace(p))),
                RespondentsJson = JsonSerializer.Serialize(new[] { candidate.Respondent }.Where(p => !string.IsNullOrWhiteSpace(p)))
            };

            _db.LitigationCases.Add(legacyCase);
            await _db.SaveChangesAsync(ct);

            // Take CaseLock for the newly created legacy case
            await CaseLock.AcquireAsync(_db, legacyCase.LitigationCaseId, ct);

            // Ensure ModuleCase
            var moduleCase = await _moduleStore.EnsureCaseForLegacyAsync(legacyCase.LitigationCaseId, ct);
            moduleCase.CaseKey = caseKey;
            await _db.SaveChangesAsync(ct);

            // Record RequestLinkStore verdict
            var finalMatch = detailMatch ?? bestMatch;
            var finalSide = detailMatchedSide ?? matchedSide;
            await _requestLinkStore.UpsertAsync(requestId, moduleCase.CaseId, finalMatch, finalSide, roleConflict: false, roleNote: null, ct);

            // Publish first observation into LiveProjectionStore
            if (snapshot is not null)
            {
                await _liveProjectionStore.PublishAsync(
                    new ObservationInput(moduleCase.CaseId, obsSource, ObservationOutcome.Found, true, nowUtc, snapshot), ct);
            }

            // Publish to request columns (bumpLiveDataVersion: false so batch bumps once at the end)
            await LiveCasePublisher.PublishAsync(_db, requestId, legacyCase.LitigationCaseId, ct, bumpLiveDataVersion: false);

            await tx.CommitAsync(ct);

            createdCount++;

            // Routing for disposed cases: orders path only, never scheduled for hearing refresh
            var refreshAction = isDisposed
                ? LiveRefreshAction.FetchOrdersOnly
                : LiveRefreshEligibility.Decide(
                    new LiveRefreshCaseState(
                        legacyCase.CaseStatus, legacyCase.CaseStage, legacyCase.Court, legacyCase.State, legacyCase.District,
                        legacyCase.FilingDate, legacyCase.LastHearingDate, legacyCase.NextHearingDate,
                        parts.Completeness, EverLiveVerified: true, NewlyDiscovered: true),
                    todayIst, nowUtc).Action;

            var createdItem = new DiscoveredCaseIntakeItemResult(
                candidate,
                IntakeCandidateStatus.Created,
                caseKey,
                legacyCase.LitigationCaseId,
                moduleCase.CaseId,
                isDisposed,
                refreshAction,
                Note: null);

            items.Add(createdItem);
            seenKeysInBatch[caseKey] = createdItem;
        }

        // 7. Bump LiveDataVersion once per batch of additions, not once per case
        long? newVersion = null;
        if (createdCount > 0)
        {
            newVersion = await RequestLiveData.BumpAsync(_db, requestId, ct);
        }

        return new PartyDiscoveryIntakeBatchResult(
            requestId,
            TotalCandidatesProcessed: items.Count,
            CreatedCount: createdCount,
            AlreadyExistingCount: alreadyExistingCount,
            RejectedCount: rejectedCount,
            PossibleDuplicatesCount: possibleDuplicatesCount,
            NewLiveDataVersion: newVersion,
            Items: items);
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
}

