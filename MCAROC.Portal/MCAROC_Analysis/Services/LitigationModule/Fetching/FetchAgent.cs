using System.Net;
using System.Security.Cryptography;
using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.LitigationModule.Fetching;

/// <summary>What one turn of an agent did.</summary>
public enum AgentTurn { Idle, Done, Deferred, Retrying, Failed, LeaseLost }

/// <summary>One fetch agent's turn (plan 4.4 flow 1 and 4.9): claim one item of a host from the queue, pass the host's shared traffic gate, call the portal, record what the portal said
/// as an observation, and settle the item. Many agents, in one process or several, run this side by side; the queue's lease token keeps them off each other's items and the gate keeps
/// their combined load on a portal within its cap.
/// <para>Only case status refreshes are handled here. Orders and party searches are separate work kinds and are put back untouched until their handlers exist.</para>
/// <para>Publication into requests is not done per case: the projection changes here, and the live stage publishes changed cases in batches (one version bump per batch, plan 4.7).</para></summary>
public sealed class FetchAgent(
    AppDbContext db, FetchWorkQueue queue, HostTrafficGate gate, IDrtClient drt, IConsumerClient consumer, ILogger<FetchAgent> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly LiveProjectionStore _projection = new(db, time);

    /// <summary>How long an unconfirmed not-found waits before it is asked again (by whichever agent, through whichever exit, claims it next).</summary>
    public static readonly TimeSpan NotFoundRecheckDelay = TimeSpan.FromMinutes(2);
    /// <summary>How long work of a kind with no handler yet is put aside.</summary>
    public static readonly TimeSpan UnhandledKindDelay = TimeSpan.FromHours(1);

    public const string NotFoundCandidate = "NotFoundCandidate";

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<AgentTurn> RunOnceAsync(FetchHost host, string agentId, CancellationToken ct)
    {
        var work = await queue.ClaimNextAsync(host, agentId, ct);
        if (work is null) return AgentTurn.Idle;
        await queue.TouchRunsAsync(work.WorkItemId, ct);   // the banner's heartbeat: an agent is working on these runs
        try
        {
            if (work.Kind != FetchWorkKind.StatusFetch)
                return await queue.DeferAsync(work, Now + UnhandledKindDelay, $"no handler for {work.Kind} yet", ct) ? AgentTurn.Deferred : AgentTurn.LeaseLost;
            return await RefreshStatusAsync(work, agentId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the item returns to the queue when its lease runs out (the first expiry is free).
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fetch agent {Agent} failed on work item {Item}", agentId, work.WorkItemId);
            var r = await queue.FailAttemptAsync(work, "AgentError", ex.GetType().Name + ": " + ex.Message, CancellationToken.None);
            return !r.Recorded ? AgentTurn.LeaseLost : r.Terminal ? AgentTurn.Failed : AgentTurn.Retrying;
        }
    }

    private async Task<AgentTurn> RefreshStatusAsync(ClaimedWork work, string agentId, CancellationToken ct)
    {
        if (work.CaseId is not { } caseId)
            return Settle(await queue.FailPermanentlyAsync(work, "NoCase", "a status refresh needs a case", ct), AgentTurn.Failed);

        var moduleCase = await db.Set<ModuleCase>().AsNoTracking().FirstOrDefaultAsync(c => c.CaseId == caseId, ct);
        if (moduleCase is null)
            return Settle(await queue.FailPermanentlyAsync(work, "NoCase", $"case {caseId} does not exist", ct), AgentTurn.Failed);
        if (moduleCase.LegacyLitigationCaseId is { } legacyId
            && await db.LitigationCases.AsNoTracking().AnyAsync(c => c.LitigationCaseId == legacyId && c.MergedIntoCaseId != null, ct))
            return Settle(await queue.CompleteAsync(work, "Merged", ct), AgentTurn.Done);   // merged away: its survivor is the case to refresh

        var payload = RefreshPayload.FromJson(work.PayloadJson);
        var lookup = PortalLookup.FromCaseKey(moduleCase.CaseKey, out var why) ?? (moduleCase.CaseKey is null ? payload?.ToLookup() : null);
        if (lookup is null)
            return Settle(await queue.FailPermanentlyAsync(work, "NoLookup", why, ct), AgentTurn.Failed);
        var party = await PartyContextAsync(payload?.RequestId, moduleCase.LegacyLitigationCaseId, ct);
        if (lookup.Host != work.Host)
            return Settle(await queue.FailPermanentlyAsync(work, "WrongHost", $"the case is looked up on {lookup.Host}, not {work.Host}", ct), AgentTurn.Failed);

        if (lookup.Host == FetchHost.Drt && DrtCaseTypeRegistry.GetCaseTypeId(lookup.Parts.TypeCode, lookup.IsDrat) is null)
            return Settle(await queue.FailPermanentlyAsync(work, "NoLookup", $"case type {lookup.Parts.TypeCode} has no portal id", ct), AgentTurn.Failed);

        var entry = await gate.TryEnterAsync(work.Host, agentId, ct);
        if (!entry.Admitted)
        {
            // Breaker open or host full: put the item back for later without using an attempt (plan 4.9 §5).
            var reason = entry.Refusal == HostEntryRefusal.BreakerOpen ? "portal not responding: will retry" : "portal at its agreed concurrency";
            return Settle(await queue.DeferAsync(work, entry.RetryAtUtc ?? Now + TimeSpan.FromSeconds(30), reason, ct), AgentTurn.Deferred);
        }

        PortalAnswer? answer;
        CallStop stop;
        try
        {
            (answer, stop) = await CallWithHeartbeatAsync(work, entry.Permit!, lookup, party, ct);
            if (answer is not null) await gate.ReportAsync(entry.Permit!, answer.HostOutcome, answer.Message, answer.RetryAfter, ct);
        }
        finally
        {
            await gate.ReleaseAsync(entry.Permit!, CancellationToken.None);
        }
        if (stop == CallStop.LeaseLost) return AgentTurn.LeaseLost;   // the item was taken over while the call ran: its result belongs to the new holder
        if (stop == CallStop.PermitLost)
        {
            // The host permit lapsed mid-call, so this call no longer counted toward the host's cap: it was stopped. The item is still ours; put it back without using an attempt.
            return Settle(await queue.DeferAsync(work, Now + TimeSpan.FromSeconds(15), "the host permit lapsed during the call", ct), AgentTurn.Deferred);
        }

        var now = Now;
        return await FencedAsync(work, async () =>
        {
            switch (answer!.Mapped.Outcome)
            {
                case ObservationOutcome.Found when answer.Mapped.Verified:
                {
                    var published = await _projection.PublishAsync(new ObservationInput(caseId, answer!.Source, ObservationOutcome.Found, true, now, answer.Mapped.Snapshot), ct);
                    // The request's own question (plan 4.3): is its company really a party? Answered from this same response, recorded on the link; only a Verified link publishes.
                    if (party.RequestId is { } requestId && answer.Party is { } match)
                        await new RequestLinkStore(db, _time).UpsertAsync(requestId, caseId, match, answer.MatchedSide, answer.RoleConflict, answer.RoleNote, ct);
                    if (moduleCase.CaseKey is null) await MintKeyAsync(caseId, lookup, answer.ReturnedParts, ct);
                    var outcome = published.ChangedFields.Count > 0 ? "Updated" : "Unchanged";
                    return Settle(await queue.CompleteAsync(work, outcome, ct), AgentTurn.Done);
                }
                case ObservationOutcome.IdentityRejected:
                {
                    // The portal returned some other case for this number: on record, never applied, never retried (plan 4.3, 4.9).
                    await _projection.PublishAsync(new ObservationInput(caseId, answer.Source, ObservationOutcome.IdentityRejected, false, now, answer.Mapped.Snapshot), ct);
                    return Settle(await queue.CompleteAsync(work, "IdentityRejected", ct), AgentTurn.Done);
                }
                case ObservationOutcome.NotFound:
                    return await NotFoundAsync(work, caseId, answer, now, ct);
                default:
                {
                    await _projection.PublishAsync(new ObservationInput(caseId, answer.Source, ObservationOutcome.Error, false, now, null), ct);
                    if (!answer.Retryable)
                        return Settle(await queue.FailPermanentlyAsync(work, "PortalError", answer.Message ?? "the portal refused the lookup", ct), AgentTurn.Failed);
                    var r = await queue.FailAttemptAsync(work, "PortalUnavailable", answer.Message ?? "the portal did not answer", ct);
                    return !r.Recorded ? AgentTurn.LeaseLost : r.Terminal ? AgentTurn.Failed : AgentTurn.Retrying;
                }
            }
        }, ct);
    }

    /// <summary>Writes a result only while this agent still holds the item: one transaction that first fences on the lease (row lock + token check), then writes the observation,
    /// the projection and the item's outcome. Another agent that took the item over cannot interleave, and a stale result is never written (plan 4.9 §7: retries are idempotent).</summary>
    private async Task<AgentTurn> FencedAsync(ClaimedWork work, Func<Task<AgentTurn>> write, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await queue.FenceAsync(work, ct))
        {
            await tx.RollbackAsync(ct);
            return AgentTurn.LeaseLost;
        }
        var turn = await write();
        if (turn == AgentTurn.LeaseLost) { await tx.RollbackAsync(ct); return turn; }
        await tx.CommitAsync(ct);
        return turn;
    }

    private enum CallStop { None, LeaseLost, PermitLost }

    /// <summary>Calls the portal while renewing the item's lease and the host permit at a third of the lease. If either cannot be renewed the call is cancelled and nothing it learnt
    /// is used: a lost lease means another agent has the item; a lost permit means the call no longer counts toward the host's cap and must not go on outside it.</summary>
    private async Task<(PortalAnswer? Answer, CallStop Stop)> CallWithHeartbeatAsync(ClaimedWork work, HostPermit permit, PortalLookup lookup, PartyContext party, CancellationToken ct)
    {
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stop = CallStop.None;
        var every = queue.LeaseDuration / 3;
        async Task HeartbeatAsync()
        {
            while (true)
            {
                try { await Task.Delay(every, _time, callCts.Token); }
                catch (OperationCanceledException) { return; }
                if (!await queue.RenewAsync(work, CancellationToken.None)) stop = CallStop.LeaseLost;
                else if (!await gate.RenewAsync(permit, CancellationToken.None)) stop = CallStop.PermitLost;
                if (stop != CallStop.None)
                {
                    callCts.Cancel();
                    return;
                }
            }
        }
        var heartbeat = HeartbeatAsync();
        PortalAnswer? answer = null;
        try
        {
            answer = await CallAsync(lookup, party, callCts.Token);
        }
        catch (OperationCanceledException) when (stop != CallStop.None && !ct.IsCancellationRequested)
        {
            // stopped by the heartbeat: decided below
        }
        finally
        {
            // Stop the heartbeat and wait for it (including a renewal in flight), so nothing else uses the database context once the call is over.
            callCts.Cancel();
            await heartbeat;
        }
        // Decided only now, after the heartbeat has finished: a lease or permit lost while the answer was arriving discards the answer just the same.
        return stop != CallStop.None ? (null, stop) : (answer, CallStop.None);
    }

    /// <summary>An empty answer is final only after a second empty answer through a <b>different</b> exit (plan 4.9 §3): a bad proxy must never mark a real case "not found" and so take
    /// it out of refresh. The first answer's exit is kept on the item as a hash, never as an address.</summary>
    private async Task<AgentTurn> NotFoundAsync(ClaimedWork work, long caseId, PortalAnswer answer, DateTime now, CancellationToken ct)
    {
        var thisExit = ExitHash(answer.ExitAddress);
        var firstExit = work.PreviousOutcome == NotFoundCandidate ? work.PreviousNote : null;
        if (firstExit is null)
            return Settle(await queue.RequeueWithNoteAsync(work, now + NotFoundRecheckDelay, NotFoundCandidate, thisExit ?? "unknown", ct), AgentTurn.Deferred);
        if (thisExit is not null && firstExit != "unknown" && firstExit != thisExit)
        {
            await _projection.PublishAsync(new ObservationInput(caseId, answer.Source, ObservationOutcome.NotFound, false, now, null), ct);
            return Settle(await queue.CompleteAsync(work, "NotFound", ct), AgentTurn.Done);
        }
        // Empty again through the same exit (or an unknown one): not confirmation. It costs an attempt, so a case that can only be reached one way ends Failed, not looping.
        var r = await queue.FailAttemptAsync(work, NotFoundCandidate, firstExit, ct);
        return !r.Recorded ? AgentTurn.LeaseLost : r.Terminal ? AgentTurn.Failed : AgentTurn.Retrying;
    }

    private sealed record PortalAnswer(ObservationSource Source, ObservationOutcomeView Mapped, HostCallOutcome HostOutcome, bool Retryable, string? Message, string? ExitAddress, TimeSpan? RetryAfter = null)
    {
        public PartyMatchResult? Party { get; init; }
        public string? MatchedSide { get; init; }
        public bool RoleConflict { get; init; }
        public string? RoleNote { get; init; }
        /// <summary>The case number as the portal itself returned it; a key is minted only from this (plan 3.3 (e)).</summary>
        public CaseNumberParts? ReturnedParts { get; init; }
    }

    /// <summary>The request the answer is checked for: its company's current and former names, and the direction the vendor stated.</summary>
    private sealed record PartyContext(long? RequestId, IReadOnlyList<string> Names, IReadOnlyList<string> FormerNames, string? Direction)
    {
        public static readonly PartyContext None = new(null, [], [], null);
    }

    private async Task<PartyContext> PartyContextAsync(long? requestId, long? legacyCaseId, CancellationToken ct)
    {
        if (requestId is not { } id) return PartyContext.None;
        var request = await db.Requests.AsNoTracking().Where(r => r.RequestId == id).Select(r => new { r.CompanyName, r.LatestCompletedIngestionRunId }).FirstOrDefaultAsync(ct);
        if (request is null || string.IsNullOrWhiteSpace(request.CompanyName)) return PartyContext.None;
        var former = request.LatestCompletedIngestionRunId is { } runId
            ? await db.CompanyNameHistories.AsNoTracking().Where(x => x.IngestionRunId == runId && x.PreviousName != "").Select(x => x.PreviousName).ToListAsync(ct)
            : [];
        var direction = legacyCaseId is { } legacy
            ? await db.LitigationCases.AsNoTracking().Where(c => c.LitigationCaseId == legacy).Select(c => c.Direction).FirstOrDefaultAsync(ct)
            : null;
        return new PartyContext(id, [request.CompanyName], former, direction);
    }

    /// <summary>A keyless case gets its shared key once the portal has verified it, minted by the gate from the identity the portal returned (plan 3.3 (e)). If another case
    /// already holds that key the two are duplicates: nothing is keyed here, and the merge rules decide (plan 4.6).</summary>
    private async Task MintKeyAsync(long caseId, PortalLookup lookup, CaseNumberParts? returned, CancellationToken ct)
    {
        if (returned is null) return;
        var key = CaseKeyGate.Evaluate(new CaseKeyInput(lookup.Forum, lookup.VenueId.ToString(System.Globalization.CultureInfo.InvariantCulture), lookup.Parts, returned, FromPortal: true)).Key;
        if (key is null) return;
        if (await db.Set<ModuleCase>().AsNoTracking().AnyAsync(c => c.CaseKey == key && c.CaseId != caseId, ct)) return;
        await db.Set<ModuleCase>().Where(c => c.CaseId == caseId && c.CaseKey == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.CaseKey, key), ct);
    }

    /// <summary>The part of an adapter's mapped observation the agent needs, the same for both portals.</summary>
    private sealed record ObservationOutcomeView(ObservationOutcome Outcome, bool Verified, ParsedCaseSnapshot? Snapshot);

    private async Task<PortalAnswer> CallAsync(PortalLookup lookup, PartyContext party, CancellationToken ct)
    {
        var todayIst = DateOnly.FromDateTime(Ist.Now(_time));
        // Identity is decided from the lookup alone; the party check (names) is for the request's link and never decides whether the observation is verified (plan 4.3).
        if (lookup.Host == FetchHost.Drt)
        {
            var caseTypeId = DrtCaseTypeRegistry.GetCaseTypeId(lookup.Parts.TypeCode, lookup.IsDrat);
            if (caseTypeId is null)
                return new(ObservationSource.DrtPortal, new(ObservationOutcome.Error, false, null), HostCallOutcome.NotSent, false, $"case type {lookup.Parts.TypeCode} has no portal id", null);
            var result = DrtCaseTypeRegistry.IsRcTrc(lookup.Parts.TypeCode)
                ? await drt.GetRcTrcCaseAsync(lookup.VenueId, caseTypeId.Value, lookup.Parts.No!, lookup.Parts.Year!.Value, ct)
                : await drt.GetCaseByNumberAsync(lookup.VenueId, caseTypeId.Value, lookup.Parts.No!, lookup.Parts.Year!.Value, ct);
            var mapped = DrtObservationMapper.Map(result, new DrtLookup(lookup.VenueId, lookup.Parts), DrtTribunalRegistry.TryGetById(lookup.VenueId), party.Names, party.FormerNames, party.Direction, todayIst);
            return new(ObservationSource.DrtPortal, new(mapped.Outcome, mapped.Verified, mapped.Snapshot), Classify(result.Status == DrtLookupStatus.Unavailable, result.StatusCode),
                Retryable(result.Status == DrtLookupStatus.Unavailable, result.StatusCode), mapped.Message ?? result.Message, result.ExitAddress, result.RetryAfter)
            {
                Party = party.Names.Count > 0 ? mapped.Verification?.Party : null, MatchedSide = mapped.Verification?.MatchedSide,
                RoleConflict = mapped.Verification?.RoleConflict ?? false, RoleNote = mapped.Verification?.RoleNote, ReturnedParts = result.Case?.CaseNumber
            };
        }
        else
        {
            var number = ConsumerCommissionRegistry.ToFullCaseNumber(lookup.Parts.Display, lookup.VenueId) ?? lookup.Parts.Display;
            var result = await consumer.GetCaseStatusAsync(number, ct);
            var mapped = ConsumerObservationMapper.Map(result, new ConsumerLookup(lookup.VenueId, lookup.Parts), party.Names, party.FormerNames, party.Direction, todayIst);
            return new(ObservationSource.ConsumerPortal, new(mapped.Outcome, mapped.Verified, mapped.Snapshot), Classify(result.Status == ConsumerLookupStatus.Unavailable, result.StatusCode),
                Retryable(result.Status == ConsumerLookupStatus.Unavailable, result.StatusCode), mapped.Message ?? result.Message, result.ExitAddress, result.RetryAfter)
            {
                Party = party.Names.Count > 0 ? mapped.Verification?.Party : null, MatchedSide = mapped.Verification?.MatchedSide,
                RoleConflict = mapped.Verification?.RoleConflict ?? false, RoleNote = mapped.Verification?.RoleNote, ReturnedParts = result.Case?.NormalizedCaseNumber
            };
        }
    }

    /// <summary>The host's health from the response (plan 4.9 table): 429 slows the host down; 5xx, timeouts and refusals count toward its breaker; anything the portal answered,
    /// even "not found" or a 400, shows it is up.</summary>
    internal static HostCallOutcome Classify(bool unavailable, HttpStatusCode? code) =>
        code == HttpStatusCode.TooManyRequests ? HostCallOutcome.Throttled
        : unavailable || (int?)code >= 500 ? HostCallOutcome.Unavailable
        : HostCallOutcome.Answered;

    /// <summary>Unavailable and 5xx/429 are worth another attempt after a cool-down; a permanent 4xx or a body that does not parse is not.</summary>
    internal static bool Retryable(bool unavailable, HttpStatusCode? code) =>
        unavailable || code == HttpStatusCode.TooManyRequests || (int?)code >= 500;

    /// <summary>A short hash of an exit address: enough to tell two exits apart, and never the address itself (proxy URLs carry credentials).</summary>
    internal static string? ExitHash(string? exit) =>
        string.IsNullOrWhiteSpace(exit) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exit.Trim().ToLowerInvariant())))[..16];

    private static AgentTurn Settle(bool recorded, AgentTurn turn) => recorded ? turn : AgentTurn.LeaseLost;
}
