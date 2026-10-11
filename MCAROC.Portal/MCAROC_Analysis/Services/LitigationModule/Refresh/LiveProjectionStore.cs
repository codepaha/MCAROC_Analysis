using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

/// <summary>What an adapter hands to the store after a portal (or vendor) call.</summary>
/// <param name="Snapshot">The compact parsed snapshot; null for a not-found or error outcome.</param>
public sealed record ObservationInput(
    long CaseId, ObservationSource Source, ObservationOutcome Outcome, bool Verified, DateTime RetrievedUtc, ParsedCaseSnapshot? Snapshot,
    int ParserVersion = 1, long? EventWatermark = null, DateTime? SourceUpdatedUtc = null);

public sealed record PublishResult(long ObservationId, ProjectionOutcome Outcome, IReadOnlyList<string> ChangedFields, LiveProjectionState State);

/// <summary>Appends observations and publishes the verified projection of a case (plan section 4.1). Every call writes an observation, whatever its outcome, so what the
/// portal said on a given day is on record. The projection row is changed through <see cref="LiveProjectionEngine"/> and a compare-and-set on <c>Version</c>: a writer
/// that lost a race re-reads the row and applies its observation again, so two agents cannot silently overwrite one another.</summary>
public sealed class LiveProjectionStore(AppDbContext db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private const int Retries = 6;

    public async Task<PublishResult> PublishAsync(ObservationInput input, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var observation = new ModuleCaseObservation
        {
            CaseId = input.CaseId, Source = (int)input.Source, Outcome = (int)input.Outcome, Verified = input.Verified, ParserVersion = input.ParserVersion,
            RetrievedUtc = input.RetrievedUtc, EventWatermark = input.EventWatermark, SourceUpdatedUtc = input.SourceUpdatedUtc,
            SnapshotJson = input.Snapshot is null ? null : JsonSerializer.Serialize(input.Snapshot), CreatedUtc = now
        };
        db.Set<ModuleCaseObservation>().Add(observation);
        await db.SaveChangesAsync(ct);   // the observation is on record before anything is decided about it

        var engineInput = new ProjectionObservation(observation.ObservationId, input.Source, input.Outcome, input.Verified, input.RetrievedUtc,
            input.Snapshot ?? new ParsedCaseSnapshot(null, null, null, null, null, null, null, null, null, 0));

        for (var attempt = 0; attempt < Retries; attempt++)
        {
            var live = await db.Set<ModuleCaseLive>().FirstOrDefaultAsync(l => l.CaseId == input.CaseId, ct);
            var current = live is null ? LiveProjectionState.Empty : ToState(live);
            var result = LiveProjectionEngine.Apply(current, engineInput);

            if (result.Outcome == ProjectionOutcome.IgnoredNotVerified || result.Outcome == ProjectionOutcome.IgnoredLate)
            {
                await Record(observation.ObservationId, result.Outcome, ct);
                return new PublishResult(observation.ObservationId, result.Outcome, result.ChangedFields, result.State);
            }

            if (live is null)
            {
                live = new ModuleCaseLive { CaseId = input.CaseId, Version = 1 };
                db.Set<ModuleCaseLive>().Add(live);
            }
            else live.Version++;
            Copy(result.State, live, now);
            observation.AppliedAs = result.Outcome.ToString();
            db.Entry(observation).Property(o => o.AppliedAs).IsModified = true;

            try
            {
                await db.SaveChangesAsync(ct);
                return new PublishResult(observation.ObservationId, result.Outcome, result.ChangedFields, result.State);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
            {
                // Another agent published first (a changed Version, or a second first-insert): forget our tracked rows, keep the observation, and apply again to what is stored now.
                db.Entry(live).State = EntityState.Detached;
                var tracked = db.Entry(observation);
                tracked.Reload();
                if (attempt == Retries - 1) throw;
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    public async Task<LiveProjectionState?> GetStateAsync(long caseId, CancellationToken ct)
    {
        var live = await db.Set<ModuleCaseLive>().AsNoTracking().FirstOrDefaultAsync(l => l.CaseId == caseId, ct);
        return live is null ? null : ToState(live);
    }

    private async Task Record(long observationId, ProjectionOutcome outcome, CancellationToken ct) =>
        await db.Set<ModuleCaseObservation>().Where(o => o.ObservationId == observationId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.AppliedAs, outcome.ToString()), ct);

    private static LiveProjectionState ToState(ModuleCaseLive l) => new(
        l.Status, l.Stage, l.Court, l.FilingDate, l.LastHearingDate, l.NextHearingDate, l.DecisionDate, l.DisposalNature, l.SuitAmount, l.HearingCount,
        l.LastRetrievedUtc, l.LastObservationId, LiveProjectionEngine.DeserializeSources(l.FieldSourcesJson), l.HeldChange);

    private static void Copy(LiveProjectionState s, ModuleCaseLive l, DateTime now)
    {
        l.Status = s.Status; l.Stage = s.Stage; l.Court = s.Court; l.FilingDate = s.FilingDate; l.LastHearingDate = s.LastHearingDate; l.NextHearingDate = s.NextHearingDate;
        l.DecisionDate = s.DecisionDate; l.DisposalNature = s.DisposalNature; l.SuitAmount = s.SuitAmount; l.HearingCount = s.HearingCount;
        l.LastRetrievedUtc = s.LastRetrievedUtc; l.LastObservationId = s.LastObservationId; l.HeldChange = s.HeldChange;
        l.FieldSourcesJson = LiveProjectionEngine.SerializeSources(s.Sources);
        l.UpdatedUtc = now;
    }
}
