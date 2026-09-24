using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <param name="NotEligible">The request can't be searched as it stands (integration not configured, no company
/// name/identifier, unsupported entity type); <see cref="Message"/> says why. Nothing was admitted.</param>
public sealed record LitigationStartResult(bool Started, long? ReferenceId, AdmissionDenial? Denial, string? Message, long? AdmissionId = null, bool NotEligible = false)
{
    public static LitigationStartResult Denied(AdmissionDenial denial, string message) => new(false, null, denial, message);
    public static LitigationStartResult Ineligible(string message) => new(false, null, null, message, NotEligible: true);
}

public sealed record LitigationSearchPlan(IReadOnlyList<LitigationKeyword> Keywords, string EntityType, string ApplicationCustomerId);

/// <summary>Every litigation search/analysis start — the reviewer's buttons today, the pipeline's
/// auto-triggers later — goes through <see cref="IPaidCallAdmission"/> here, so the ledger is the complete
/// spend history (docs/pipeline-automation-plan.md §4.0). The job/run services themselves are unchanged.</summary>
public sealed class LitigationStartService(
    AppDbContext db,
    IPaidCallAdmission admission,
    LitigationSearchJobService searchJobs,
    LitigationSearchQueue searchQueue,
    LitigationAiAnalysisOrchestrator analysis,
    IOptions<BprLitigationOptions>? bprOptions = null)
{
    /// <summary>Eligibility and keyword planning, shared by the reviewer's button and the pipeline's automatic
    /// start so the two can't drift. Returns the problem as text when the request can't be searched.</summary>
    public static async Task<(LitigationSearchPlan? Plan, string? Problem)> PlanSearchAsync(
        AppDbContext db, McaRequest request, BprLitigationOptions opts, CancellationToken ct)
    {
        if (!opts.IsConfigured)
            return (null, "BPR Litigation API is not configured on this instance.");
        if (string.IsNullOrWhiteSpace(request.CompanyName))
            return (null, "Company name is required for litigation keyword planning.");
        if (request.EntityType is not (EntityType.Company or EntityType.LLP))
            return (null, "Unsupported entity type for litigation search.");
        if (string.IsNullOrWhiteSpace(opts.DefaultEntityType))
            return (null, "Default entity type is not configured.");

        var historicalNames = request.LatestCompletedIngestionRunId is { } runId
            ? await db.CompanyNameHistories.Where(x => x.IngestionRunId == runId && !string.IsNullOrWhiteSpace(x.PreviousName))
                .Select(x => x.PreviousName).ToListAsync(ct)
            : [];
        var keywords = LitigationKeywordPlanner.Build(request.CompanyName, historicalNames);
        var appCustomerId = !string.IsNullOrWhiteSpace(request.RequestNumber)
            ? request.RequestNumber.Trim()
            : $"REQ-{request.RequestId}";
        return (new LitigationSearchPlan(keywords, opts.DefaultEntityType, appCustomerId), null);
    }

    /// <summary>The pipeline's automatic start (plan §4.1). Unlike the reviewer's button it never re-runs: if the
    /// request already has a search job — started by hand, or by a racing reconciler — nothing is admitted and the
    /// result carries that job's id with <c>Started=false</c>. A company name alone is not a safe identity to buy
    /// on, so a request without a CIN/LLPIN is not eligible.</summary>
    public async Task<LitigationStartResult> StartAutoSearchAsync(long requestId, string? correlationId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return LitigationStartResult.Ineligible("The request no longer exists.");
        if (string.IsNullOrWhiteSpace(request.Cin ?? request.Llpin))
            return LitigationStartResult.Ineligible("The request has no CIN/LLPIN; start the litigation search by hand.");
        var (plan, problem) = await PlanSearchAsync(db, request, bprOptions?.Value ?? new BprLitigationOptions(), ct);
        if (plan is null) return LitigationStartResult.Ineligible(problem!);
        return await StartSearchAsync(request, plan.Keywords, plan.EntityType, plan.ApplicationCustomerId, PaidCallTrigger.Auto, ct,
            correlationId, onlyIfNoJob: true);
    }

    public async Task<LitigationStartResult> StartSearchAsync(
        McaRequest request, IReadOnlyList<LitigationKeyword> keywords, string entityType, string applicationCustomerId,
        PaidCallTrigger trigger, CancellationToken ct, string? correlationId = null, bool onlyIfNoJob = false)
    {
        // The scope key includes the keyword set, so two starts with different keywords claim different
        // scopes and would both pass admission — but there is only one job row per request. Serialise the
        // whole check → admit → reset → link sequence per request, across instances.
        await using var startLock = await SqlSessionLock.TryAcquireAsync(db, $"MCAROC:LitigationSearchStart:{request.RequestId}", ct);
        if (startLock is null)
            return LitigationStartResult.Denied(AdmissionDenial.InFlight, "A litigation search for this request is already being started.");

        if (onlyIfNoJob && await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == request.RequestId)
                .Select(j => (long?)j.LitigationSearchJobId).FirstOrDefaultAsync(ct) is { } existingJobId)
            return new LitigationStartResult(false, existingJobId, null, "A litigation search already exists for this request.");

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
