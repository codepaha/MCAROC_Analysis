using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

public sealed record LitigationStartResult(bool Started, long? ReferenceId, AdmissionDenial? Denial, string? Message, long? AdmissionId = null)
{
    public static LitigationStartResult Denied(AdmissionDenial denial, string message) => new(false, null, denial, message);
}

/// <summary>Every litigation search/analysis start — the reviewer's buttons today, the pipeline's
/// auto-triggers later — goes through <see cref="IPaidCallAdmission"/> here, so the ledger is the complete
/// spend history (docs/pipeline-automation-plan.md §4.0). The job/run services themselves are unchanged.</summary>
public sealed class LitigationStartService(
    AppDbContext db,
    IPaidCallAdmission admission,
    LitigationSearchJobService searchJobs,
    LitigationSearchQueue searchQueue,
    LitigationAiAnalysisOrchestrator analysis)
{
    public async Task<LitigationStartResult> StartSearchAsync(
        McaRequest request, IReadOnlyList<LitigationKeyword> keywords, string entityType, string applicationCustomerId,
        PaidCallTrigger trigger, CancellationToken ct, string? correlationId = null)
    {
        // The scope key includes the keyword set, so two starts with different keywords claim different
        // scopes and would both pass admission — but there is only one job row per request. Serialise the
        // whole check → admit → reset → link sequence per request, across instances.
        await using var startLock = await SqlSessionLock.TryAcquireAsync(db, $"MCAROC:LitigationSearchStart:{request.RequestId}", ct);
        if (startLock is null)
            return LitigationStartResult.Denied(AdmissionDenial.InFlight, "A litigation search for this request is already being started.");

        // Settle the previous admission first: a rerun resets the job and clears RegistrationAttemptedUtc,
        // which is the only evidence the previous search may have been bought.
        await admission.ResolveOutstandingAsync(PaidCallKind.LitigationSearch, request.RequestId, ct);
        // A still-Reserved admission under *any* keyword set means the request's one job is queued or
        // running; resetting it now would let one purchase satisfy two admissions.
        if (await HasReservedAsync(PaidCallKind.LitigationSearch, request.RequestId, ct))
            return LitigationStartResult.Denied(AdmissionDenial.InFlight, "A litigation search for this request is already queued or in progress.");

        var scopeKey = PaidCallScopeKeys.LitigationSearch(PaidCallScopeKeys.CanonicalIdentifier(request), keywords.Select(k => k.Value));
        var admitted = await admission.TryAdmitAsync(
            new PaidCallAdmissionRequest(PaidCallKind.LitigationSearch, scopeKey, trigger, request.RequestId, request.ClientId, correlationId), ct);
        if (!admitted.Admitted)
            return LitigationStartResult.Denied(admitted.Denial!.Value, DenialMessage(admitted.Denial.Value, "litigation search"));

        LitigationSearchJob job;
        try
        {
            job = await searchJobs.CreateOrResetJobAsync(request.RequestId, keywords, entityType, applicationCustomerId, ct);
        }
        catch
        {
            await admission.ReleaseAsync(admitted.AdmissionId!.Value, CancellationToken.None); // nothing was sent to BPR
            throw;
        }

        await admission.SetReferenceAsync(admitted.AdmissionId!.Value, job.LitigationSearchJobId, ct);
        searchQueue.Enqueue(job.LitigationSearchJobId);
        return new LitigationStartResult(true, job.LitigationSearchJobId, null, null, admitted.AdmissionId);
    }

    public async Task<LitigationStartResult> StartAnalysisAsync(long requestId, long? clientId, PaidCallTrigger trigger, CancellationToken ct, string? correlationId = null)
    {
        // Joining a run that's already active costs nothing new — no admission.
        var active = await ActiveRunAsync(requestId, ct);
        if (active is not null) return new LitigationStartResult(true, active.Value, null, null);

        await admission.ResolveOutstandingAsync(PaidCallKind.LitigationAnalysis, requestId, ct);
        if (await HasReservedAsync(PaidCallKind.LitigationAnalysis, requestId, ct))
            return LitigationStartResult.Denied(AdmissionDenial.InFlight, "A litigation analysis for this request is already being started.");

        var snapshotId = await db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => s.SearchJob!.RequestId == requestId && s.Status == LitigationReportSnapshotStatus.Completed)
            .OrderByDescending(s => s.LitigationReportSnapshotId)
            .Select(s => (long?)s.LitigationReportSnapshotId)
            .FirstOrDefaultAsync(ct);

        var admitted = await admission.TryAdmitAsync(new PaidCallAdmissionRequest(
            PaidCallKind.LitigationAnalysis, PaidCallScopeKeys.LitigationAnalysis(snapshotId, requestId), trigger, requestId, clientId, correlationId), ct);
        if (!admitted.Admitted)
            return LitigationStartResult.Denied(admitted.Denial!.Value, DenialMessage(admitted.Denial.Value, "litigation analysis"));

        LitigationAiAnalysisRun run;
        try
        {
            run = await analysis.CreateOrJoinAsync(requestId, ct);
        }
        catch
        {
            await admission.ReleaseAsync(admitted.AdmissionId!.Value, CancellationToken.None);
            throw;
        }

        // Lost a race to a concurrent start and joined its run instead — this admission bought nothing.
        if (run.CreatedUtc < admitted.ReservedUtc)
        {
            await admission.ReleaseAsync(admitted.AdmissionId!.Value, ct);
            return new LitigationStartResult(true, run.LitigationAiAnalysisRunId, null, null);
        }

        await admission.SetReferenceAsync(admitted.AdmissionId!.Value, run.LitigationAiAnalysisRunId, ct);
        return new LitigationStartResult(true, run.LitigationAiAnalysisRunId, null, null, admitted.AdmissionId);
    }

    private Task<bool> HasReservedAsync(PaidCallKind kind, long requestId, CancellationToken ct) =>
        db.PaidCallAdmissions.AnyAsync(a => a.Kind == kind && a.RequestId == requestId && a.State == PaidCallAdmissionState.Reserved, ct);

    private Task<long?> ActiveRunAsync(long requestId, CancellationToken ct) =>
        db.LitigationAiAnalysisRuns.AsNoTracking()
            .Where(x => x.RequestId == requestId && (x.Status == LitigationAiAnalysisRunStatus.Pending || x.Status == LitigationAiAnalysisRunStatus.InProgress))
            .OrderByDescending(x => x.LitigationAiAnalysisRunId)
            .Select(x => (long?)x.LitigationAiAnalysisRunId)
            .FirstOrDefaultAsync(ct);

    private static string DenialMessage(AdmissionDenial denial, string what) => denial switch
    {
        AdmissionDenial.InFlight => $"A {what} for this company is already in progress.",
        AdmissionDenial.Fresh => $"A {what} for this company was already run recently.",
        _ => $"Today's automatic {what} limit has been reached."
    };
}
