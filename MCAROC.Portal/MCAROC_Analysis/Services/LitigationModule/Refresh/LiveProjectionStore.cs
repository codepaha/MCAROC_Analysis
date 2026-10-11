using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

/// <summary>What an adapter hands to the store after a portal (or vendor) call.</summary>
/// <param name="Snapshot">The compact parsed snapshot; null for a not-found or error outcome.</param>
public sealed record ObservationInput(
    long CaseId, ObservationSource Source, ObservationOutcome Outcome, bool Verified, DateTime RetrievedUtc, ParsedCaseSnapshot? Snapshot,
    int ParserVersion = 1, long? EventWatermark = null, DateTime? SourceUpdatedUtc = null);

public sealed record PublishResult(long ObservationId, ProjectionOutcome Outcome, IReadOnlyList<string> ChangedFields, LiveProjectionState State);

/// <summary>Appends observations and publishes the verified projection of a case (plan section 4.1). Every call writes exactly one observation, whatever its outcome, so what the
/// portal said on a given day is on record; the row is inserted once and never updated. The projection row is changed through <see cref="LiveProjectionEngine"/> and a compare-and-set on <c>Version</c>: a writer
/// that lost a race re-reads the row and applies its observation again, so two agents cannot silently overwrite one another.</summary>
public sealed class LiveProjectionStore(AppDbContext db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private const int Retries = 6;

    public async Task<PublishResult> PublishAsync(ObservationInput input, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        // The id is taken from a sequence first, so the engine can name the observation in the field provenance and the observation row can be written ONCE, with its
        // outcome already known. Observations are append-only: nothing ever updates one after it is inserted.
        var observationId = await NextObservationIdAsync(ct);
        var engineInput = new ProjectionObservation(observationId, input.Source, input.Outcome, input.Verified, input.RetrievedUtc,
            input.Snapshot ?? new ParsedCaseSnapshot(null, null, null, null, null, null, null, null, null, 0));
        var snapshotJson = input.Snapshot is null ? null : JsonSerializer.Serialize(input.Snapshot);

        for (var attempt = 0; attempt < Retries; attempt++)
        {
            var live = await db.Set<ModuleCaseLive>().FirstOrDefaultAsync(l => l.CaseId == input.CaseId, ct);
            var current = live is null ? LiveProjectionState.Empty : ToState(live);
            var result = LiveProjectionEngine.Apply(current, engineInput);

            var observation = new ModuleCaseObservation
            {
                ObservationId = observationId, CaseId = input.CaseId, Source = (int)input.Source, Outcome = (int)input.Outcome, Verified = input.Verified,
                ParserVersion = input.ParserVersion, RetrievedUtc = input.RetrievedUtc, EventWatermark = input.EventWatermark, SourceUpdatedUtc = input.SourceUpdatedUtc,
                SnapshotJson = snapshotJson, AppliedAs = result.Outcome.ToString(), CreatedUtc = now
            };
            db.Set<ModuleCaseObservation>().Add(observation);

            var changesProjection = result.Outcome is not (ProjectionOutcome.IgnoredNotVerified or ProjectionOutcome.IgnoredLate);
            if (changesProjection)
            {
                if (live is null)
                {
                    live = new ModuleCaseLive { CaseId = input.CaseId, Version = 1 };
                    db.Set<ModuleCaseLive>().Add(live);
                }
                else live.Version++;
                Copy(result.State, live, now);
            }

            try
            {
                await db.SaveChangesAsync(ct);   // the observation and the projection change commit together, or neither does
                return new PublishResult(observationId, result.Outcome, result.ChangedFields, result.State);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
            {
                // Another agent published first (a changed Version, or a second first-insert): forget what this attempt tracked and apply again to what is stored now.
                db.ChangeTracker.Clear();
                if (attempt == Retries - 1) throw;
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The next id from the observation sequence. A raw command, because EF wraps a query in a sub-select and NEXT VALUE FOR is not allowed there.</summary>
    private async Task<long> NextObservationIdAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT NEXT VALUE FOR [litigation].[CaseObservationIds]";
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    public async Task<LiveProjectionState?> GetStateAsync(long caseId, CancellationToken ct)
    {
        var live = await db.Set<ModuleCaseLive>().AsNoTracking().FirstOrDefaultAsync(l => l.CaseId == caseId, ct);
        return live is null ? null : ToState(live);
    }

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
