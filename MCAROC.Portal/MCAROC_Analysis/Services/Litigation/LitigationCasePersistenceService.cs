using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Parses a completed <see cref="LitigationSearchJob"/>'s raw report and persists its cases, orders
/// and source-report provenance. Two distinct idempotency/de-dup mechanisms, for two distinct problems:
/// <list type="bullet">
/// <item><b>Report-level idempotency</b> (re-processing the exact same completed report must be a no-op):
/// short-circuits immediately if any <see cref="LitigationCaseSourceReport"/> already matches this job's
/// current (job id, raw-response hash) pair — not job id alone, since a request's job row is reused across
/// reruns (see <see cref="LitigationCaseSourceReport"/>'s remarks).</item>
/// <item><b>Case-level de-dup</b> (the same real-world case, found by a different search run, must not
/// become a second row): conservative and CNR-first via <see cref="LitigationCaseIdentity.CanAutoDedupe"/> —
/// never fuzzy-matched on court/parties/dates, and never merged just because CSP ID matches (CSP is retained
/// provider identity, not a merge key).</item>
/// </list>
/// Only <see cref="BprReportFormat.Json"/> reports can be parsed today — <see cref="BprLitigationReportParser"/>
/// has no XLSX reader. A job that completed with a different format is logged and left unpersisted rather
/// than guessed at; building that parser is a follow-up, not silently skipped forever.</summary>
public sealed class LitigationCasePersistenceService(AppDbContext db, ILogger<LitigationCasePersistenceService> logger)
{
    public async Task PersistCasesForJobAsync(long jobId, CancellationToken ct)
    {
        var job = await db.LitigationSearchJobs.FirstOrDefaultAsync(j => j.LitigationSearchJobId == jobId, ct);
        if (job is null || job.Status != LitigationSearchJobStatus.Completed ||
            job.RawReportBytes is null || job.RawResponseHash is null)
            return;

        // Keyed on (job id, report hash), not job id alone — a request's LitigationSearchJob row is reused in
        // place across reruns (CreateOrResetJobAsync), so the same job id recurs across genuinely different
        // search runs. See LitigationCaseSourceReport's remarks for why job id alone would be wrong here.
        var alreadyPersisted = await db.LitigationCaseSourceReports
            .AnyAsync(s => s.LitigationSearchJobId == jobId && s.ReportHash == job.RawResponseHash, ct);
        if (alreadyPersisted)
            return; // idempotent — a retried/duplicate call for this exact report is a no-op

        if (job.ReportFormat != BprReportFormat.Json)
        {
            logger.LogWarning(
                "Litigation search job {JobId} completed with report format {Format} — no parser exists for " +
                "that format yet (only Json), so its cases were not extracted. Needs a follow-up XLSX/Unknown " +
                "report parser; the raw bytes remain retained on the job for when one exists.",
                jobId, job.ReportFormat);
            return;
        }

        BprLitigationReport report;
        try
        {
            report = BprLitigationReportParser.Parse(Encoding.UTF8.GetString(job.RawReportBytes));
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "Litigation search job {JobId}'s raw report failed to parse as JSON despite ReportFormat=Json.", jobId);
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var item in report.Cases)
            await PersistOneCaseAsync(job, item, now, ct);
    }

    private async Task PersistOneCaseAsync(LitigationSearchJob job, BprLitigationCase item, DateTime now, CancellationToken ct)
    {
        var identity = LitigationCaseIdentity.Normalise(new LitigationCaseIdentityInput(
            CnrNumber: item.CnrNumber, CaseNumber: item.CaseNumber, CaseYear: ParseYear(item.CaseYear),
            CaseType: item.CaseType, CspId: item.CspId));

        var litigationCase = await FindAutoDedupeMatchAsync(job.RequestId, identity, ct);
        if (litigationCase is null)
        {
            litigationCase = new LitigationCase { RequestId = job.RequestId, FirstSeenUtc = now };
            db.LitigationCases.Add(litigationCase);
        }

        ApplyFields(litigationCase, identity, item, now);
        await UpsertOrdersAsync(litigationCase, item.Orders, now, ct);

        // Persist now so LitigationCaseId is real before the source-report link below — case volume per
        // report is small (tens, not thousands), so a save per case is simple and safe over batching.
        await db.SaveChangesAsync(ct);

        // Two cases within the SAME report can resolve to the same LitigationCase (an in-batch CNR match) —
        // without this check, the second one would violate the (case, job, hash) unique index.
        var linkAlreadyExists = await db.LitigationCaseSourceReports.AnyAsync(s =>
            s.LitigationCaseId == litigationCase.LitigationCaseId && s.LitigationSearchJobId == job.LitigationSearchJobId &&
            s.ReportHash == job.RawResponseHash, ct);
        if (!linkAlreadyExists)
        {
            db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
            {
                LitigationCaseId = litigationCase.LitigationCaseId, LitigationSearchJobId = job.LitigationSearchJobId,
                ReportHash = job.RawResponseHash!, FirstSeenUtc = now
            });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Looks for an existing case in the same request whose identity auto-dedupes against
    /// <paramref name="identity"/>. Scoped to <paramref name="requestId"/> — de-duplication happens within one
    /// company's search results, never across different companies/requests.</summary>
    private async Task<LitigationCase?> FindAutoDedupeMatchAsync(long requestId, LitigationCaseIdentityEvidence identity, CancellationToken ct)
    {
        if (identity.Cnr is null) return null; // CanAutoDedupe always requires a CNR on both sides

        var candidates = await db.LitigationCases
            .Where(c => c.RequestId == requestId && c.Cnr == identity.Cnr)
            .ToListAsync(ct);
        return candidates.FirstOrDefault(c =>
            LitigationCaseIdentity.CanAutoDedupe(identity, new LitigationCaseIdentityEvidence(c.Cnr, c.ProceedingType, null, c.CspId)));
    }

    private static void ApplyFields(LitigationCase litigationCase, LitigationCaseIdentityEvidence identity, BprLitigationCase item, DateTime now)
    {
        litigationCase.ProviderCaseId = item.ProviderCaseId;
        litigationCase.CspId = item.CspId;
        litigationCase.Cnr = identity.Cnr;
        litigationCase.ProceedingType = identity.ProceedingType;
        litigationCase.CourtCategory = item.CourtCategory;
        litigationCase.Direction = item.Direction;
        litigationCase.CaseClassification = item.CaseClassification;
        litigationCase.Type = item.Type;
        litigationCase.Court = item.Court;
        litigationCase.Bench = item.Bench;
        litigationCase.CaseNumber = item.CaseNumber;
        litigationCase.CaseType = item.CaseType;
        litigationCase.CaseYear = item.CaseYear;
        litigationCase.CaseStage = item.CaseStage;
        litigationCase.CaseStatus = item.CaseStatus;
        litigationCase.Act = item.Act;
        litigationCase.FilingDate = item.FilingDate;
        litigationCase.LastHearingDate = item.LastHearingDate;
        litigationCase.NextHearingDate = item.NextHearingDate;
        litigationCase.DecisionDate = item.DecisionDate;
        litigationCase.State = item.State;
        litigationCase.District = item.District;
        litigationCase.PetitionersJson = item.PetitionersJson;
        litigationCase.RespondentsJson = item.RespondentsJson;
        litigationCase.PetitionerAdvocatesJson = item.PetitionerAdvocatesJson;
        litigationCase.RespondentAdvocatesJson = item.RespondentAdvocatesJson;
        litigationCase.LastSeenUtc = now;
    }

    /// <summary>Adds only orders not already recorded for this case — de-duplicated on (PdfUrl, OrderDate,
    /// OrderType), the closest available approximation of identity BPR's order records offer (they carry no
    /// order-level id of their own).</summary>
    private async Task UpsertOrdersAsync(LitigationCase litigationCase, IReadOnlyList<BprLitigationOrder> orders, DateTime now, CancellationToken ct)
    {
        if (orders.Count == 0) return;

        // A brand-new case has LitigationCaseId == 0 at this point (not yet saved) — the query below then
        // correctly finds zero existing rows rather than needing a separate "is this new" branch.
        var existing = await db.LitigationCaseOrders
            .Where(o => o.LitigationCaseId == litigationCase.LitigationCaseId)
            .Select(o => new { o.PdfUrl, o.OrderDate, o.OrderType })
            .ToListAsync(ct);

        foreach (var order in orders)
        {
            var isDuplicate = existing.Any(e => e.PdfUrl == order.PdfUrl && e.OrderDate == order.OrderDate && e.OrderType == order.OrderType);
            if (isDuplicate) continue;

            db.LitigationCaseOrders.Add(new LitigationCaseOrder
            {
                Case = litigationCase, PdfUrl = order.PdfUrl, OrderDate = order.OrderDate, OrderType = order.OrderType, CreatedUtc = now
            });
        }
    }

    private static int? ParseYear(string? value) => int.TryParse(value, out var year) ? year : null;

    /// <summary>Returns every completed job's id, for the worker's startup recovery sweep to re-enqueue.
    /// Deliberately does not try to pre-filter to "unprocessed" ones here — since a job's (job id, report
    /// hash) pair, not job id alone, is what identifies one report/run (see
    /// <see cref="LitigationCaseSourceReport"/>'s remarks), replicating that filter in this query would just
    /// duplicate <see cref="PersistCasesForJobAsync"/>'s own idempotency check and risk drifting out of sync
    /// with it. Re-enqueuing every completed job is cheap: an already-processed one's re-run is a single
    /// indexed lookup that immediately no-ops.</summary>
    public async Task<IReadOnlyList<long>> FindUnprocessedCompletedJobIdsAsync(CancellationToken ct) =>
        await db.LitigationSearchJobs
            .Where(j => j.Status == LitigationSearchJobStatus.Completed)
            .Select(j => j.LitigationSearchJobId)
            .ToListAsync(ct);
}
