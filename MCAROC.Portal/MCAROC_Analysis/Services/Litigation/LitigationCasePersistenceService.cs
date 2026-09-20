using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Parses a completed <see cref="LitigationSearchJob"/>'s raw report and persists its cases, orders
/// and source-report provenance. Built around an immutable <see cref="LitigationReportSnapshot"/> per report
/// (see its own remarks for why a copy separate from the mutable job row is required), which is also the
/// crash-safe, concurrency-safe unit of import work:
/// <list type="bullet">
/// <item><b>Crash safety.</b> The snapshot's <c>CasesPersistedCount</c> only advances once a case (plus its
/// orders and source-report link) is durably committed. A worker that dies mid-report leaves the snapshot
/// <c>InProgress</c> with an expired lease; the next attempt reclaims it and resumes from that count —
/// positionally, not by identity, so it correctly resumes even for a case with no CNR.</item>
/// <item><b>Concurrency safety.</b> Every mutation to a snapshot — the initial claim and every per-case
/// progress update — is an ordinary EF change tracked against a row protected by a SQL Server
/// <c>rowversion</c> concurrency token. Two workers racing to claim or advance the same snapshot can never
/// both succeed: the loser's <c>SaveChangesAsync</c> throws <see cref="DbUpdateConcurrencyException"/> and
/// stops immediately. Unique indexes on <c>LitigationCase</c> and <c>LitigationCaseOrder</c> are a second,
/// independent backstop against the same class of race at the case level.</item>
/// </list>
/// Only <see cref="BprReportFormat.Json"/> reports can be parsed today — <see cref="BprLitigationReportParser"/>
/// has no XLSX reader. A snapshot whose report is a different format is marked <c>Failed</c> (terminal, never
/// silently retried forever) rather than guessed at.</summary>
public sealed class LitigationCasePersistenceService(AppDbContext db, ILogger<LitigationCasePersistenceService> logger)
{
    private const int LeaseMinutes = 15; // generous: pure CPU/DB work, no external calls, "tens not thousands" of cases

    public async Task PersistCasesForJobAsync(long jobId, CancellationToken ct)
    {
        var job = await db.LitigationSearchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.LitigationSearchJobId == jobId, ct);
        if (job is null || job.Status != LitigationSearchJobStatus.Completed ||
            job.RawReportBytes is null || job.RawResponseHash is null)
            return;

        var snapshot = await GetOrCreateSnapshotAsync(job, ct);
        if (snapshot.IsTerminal) return; // already fully processed, or permanently unparseable

        if (!await TryClaimAsync(snapshot, ct)) return; // already owned by a still-live attempt

        try
        {
            if (snapshot.ReportFormat != BprReportFormat.Json)
            {
                await MarkFailedAsync(snapshot,
                    $"No parser exists yet for report format {snapshot.ReportFormat} (only Json) — cases were not extracted.", ct);
                logger.LogWarning(
                    "Litigation report snapshot {SnapshotId} (job {JobId}) has format {Format} — no parser exists, marked Failed.",
                    snapshot.LitigationReportSnapshotId, jobId, snapshot.ReportFormat);
                return;
            }

            BprLitigationReport report;
            try
            {
                report = BprLitigationReportParser.Parse(Encoding.UTF8.GetString(snapshot.RawReportBytes));
            }
            catch (JsonException ex)
            {
                await MarkFailedAsync(snapshot, $"Raw report failed to parse as JSON despite ReportFormat=Json: {ex.Message}", ct);
                logger.LogError(ex, "Litigation report snapshot {SnapshotId} failed to parse.", snapshot.LitigationReportSnapshotId);
                return;
            }

            for (var i = snapshot.CasesPersistedCount; i < report.Cases.Count; i++)
                await PersistOneCaseAsync(job.RequestId, snapshot, report.Cases[i], ct);

            snapshot.Status = LitigationReportSnapshotStatus.Completed;
            snapshot.CompletedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Litigation report snapshot {SnapshotId} (job {JobId}) completed: {CaseCount} case(s) persisted.",
                snapshot.LitigationReportSnapshotId, jobId, report.Cases.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the lease mid-processing — another attempt has since claimed and possibly advanced this
            // snapshot. Stop touching it; whatever it committed stands, and a future attempt (if still needed)
            // will resume from its own CasesPersistedCount.
            logger.LogWarning(
                "Litigation report snapshot {SnapshotId} (job {JobId}) lost its claim mid-processing — another attempt has taken over.",
                snapshot.LitigationReportSnapshotId, jobId);
        }
    }

    /// <summary>Creates the snapshot row for (job id, report hash) if one doesn't already exist — race-safe
    /// against a concurrent creator via the unique index, mirroring
    /// <c>CalculationAiAuditOrchestrator.EnqueueForSnapshotAsync</c>'s exact pattern for the same problem.</summary>
    private async Task<LitigationReportSnapshot> GetOrCreateSnapshotAsync(LitigationSearchJob job, CancellationToken ct)
    {
        var existing = await db.LitigationReportSnapshots
            .FirstOrDefaultAsync(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.ReportHash == job.RawResponseHash, ct);
        if (existing is not null) return existing;

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = job.RawResponseHash!,
            ReportFormat = job.ReportFormat,
            RawReportBytes = job.RawReportBytes!,
            RawReportByteLength = job.RawReportBytes!.LongLength,
            RetrievedUtc = job.CompletedUtc ?? DateTime.UtcNow,
            Status = LitigationReportSnapshotStatus.Pending,
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        try
        {
            await db.SaveChangesAsync(ct);
            return snapshot;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            db.Entry(snapshot).State = EntityState.Detached;
            logger.LogInformation(
                "Lost a race creating the litigation report snapshot for job {JobId} — another attempt already created it.",
                job.LitigationSearchJobId);
            return await db.LitigationReportSnapshots
                .FirstAsync(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.ReportHash == job.RawResponseHash, ct);
        }
    }

    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Atomic claim: Pending, or InProgress with an expired lease, becomes InProgress under this
    /// attempt. Protected by <see cref="LitigationReportSnapshot.RowVersion"/> — if another attempt claims the
    /// same row first, this SaveChanges throws <see cref="DbUpdateConcurrencyException"/>, caught here and
    /// treated as "not claimable right now," not an error.</summary>
    private async Task<bool> TryClaimAsync(LitigationReportSnapshot snapshot, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var claimable = snapshot.Status == LitigationReportSnapshotStatus.Pending ||
            (snapshot.Status == LitigationReportSnapshotStatus.InProgress && (snapshot.LeaseExpiresUtc is null || snapshot.LeaseExpiresUtc < now));
        if (!claimable) return false;

        snapshot.Status = LitigationReportSnapshotStatus.InProgress;
        snapshot.LeaseOwner = LeaseOwnerId;
        snapshot.LeaseExpiresUtc = now.AddMinutes(LeaseMinutes);
        snapshot.AttemptCount++;
        snapshot.StartedUtc ??= now;

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation(
                "Lost a race claiming litigation report snapshot {SnapshotId} — another attempt claimed it first.",
                snapshot.LitigationReportSnapshotId);
            return false;
        }
    }

    private async Task MarkFailedAsync(LitigationReportSnapshot snapshot, string reason, CancellationToken ct)
    {
        snapshot.Status = LitigationReportSnapshotStatus.Failed;
        snapshot.FailureReason = reason;
        snapshot.CompletedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* someone else already advanced this snapshot — leave it */ }
    }

    private async Task PersistOneCaseAsync(long requestId, LitigationReportSnapshot snapshot, BprLitigationCase item, CancellationToken ct)
    {
        var identity = LitigationCaseIdentity.Normalise(new LitigationCaseIdentityInput(
            CnrNumber: item.CnrNumber, CaseNumber: item.CaseNumber, CaseYear: ParseYear(item.CaseYear),
            CaseType: item.CaseType, CspId: item.CspId));
        var now = DateTime.UtcNow;
        // Captured once, assigned absolutely (not "++") on every attempt below — a retry must not double-count.
        var targetCasesPersistedCount = snapshot.CasesPersistedCount + 1;

        // One retry: if a concurrent import inserts the same (RequestId, Cnr, ProceedingType) between our
        // lookup and our insert, the unique index rejects us — re-query for the winner's row and merge onto
        // it instead of failing the whole snapshot. Only entities THIS attempt itself added are detached on
        // retry — snapshot's own tracking is never touched here, so its RowVersion-based concurrency check
        // (a separate, unrelated mechanism from this per-case retry) stays exactly as EF set it up.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var addedThisAttempt = new List<object>();

            var litigationCase = await FindAutoDedupeMatchAsync(requestId, identity, ct);
            var isNewCase = litigationCase is null;
            litigationCase ??= new LitigationCase { RequestId = requestId, FirstSeenUtc = now };
            if (isNewCase)
            {
                db.LitigationCases.Add(litigationCase);
                addedThisAttempt.Add(litigationCase);
            }

            ApplyFields(litigationCase, identity, item, now);
            addedThisAttempt.AddRange(await UpsertOrdersAsync(litigationCase, item.Orders, now, ct));

            var linkAlreadyExists = !isNewCase && litigationCase.LitigationCaseId != 0 &&
                await db.LitigationCaseSourceReports.AnyAsync(s =>
                    s.LitigationCaseId == litigationCase.LitigationCaseId && s.LitigationReportSnapshotId == snapshot.LitigationReportSnapshotId, ct);
            if (!linkAlreadyExists)
            {
                var link = new LitigationCaseSourceReport
                {
                    Case = litigationCase, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId,
                    ProviderCaseId = item.ProviderCaseId, CspId = item.CspId, FirstSeenUtc = now
                };
                db.LitigationCaseSourceReports.Add(link);
                addedThisAttempt.Add(link);
            }

            snapshot.CasesPersistedCount = targetCasesPersistedCount;

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex) && attempt == 0)
            {
                foreach (var added in addedThisAttempt)
                    db.Entry(added).State = EntityState.Detached;
                logger.LogInformation(
                    "Lost a race persisting a litigation case for request {RequestId} — retrying against the concurrent winner's row.",
                    requestId);
            }
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
    /// order-level id of their own); backstopped by a DB unique index for the concurrent-import case. Returns
    /// the newly-added entities so a caller can precisely detach them if the surrounding SaveChanges fails.</summary>
    private async Task<IReadOnlyList<LitigationCaseOrder>> UpsertOrdersAsync(
        LitigationCase litigationCase, IReadOnlyList<BprLitigationOrder> orders, DateTime now, CancellationToken ct)
    {
        if (orders.Count == 0) return [];

        // A brand-new case has LitigationCaseId == 0 at this point (not yet saved) — the query below then
        // correctly finds zero existing rows rather than needing a separate "is this new" branch.
        var existing = await db.LitigationCaseOrders
            .Where(o => o.LitigationCaseId == litigationCase.LitigationCaseId)
            .Select(o => new { o.PdfUrl, o.OrderDate, o.OrderType })
            .ToListAsync(ct);

        var added = new List<LitigationCaseOrder>();
        foreach (var order in orders)
        {
            var isDuplicate = existing.Any(e => e.PdfUrl == order.PdfUrl && e.OrderDate == order.OrderDate && e.OrderType == order.OrderType);
            if (isDuplicate) continue;

            var newOrder = new LitigationCaseOrder
            {
                Case = litigationCase, PdfUrl = order.PdfUrl, OrderDate = order.OrderDate, OrderType = order.OrderType, CreatedUtc = now
            };
            db.LitigationCaseOrders.Add(newOrder);
            added.Add(newOrder);
        }
        return added;
    }

    private static int? ParseYear(string? value) => int.TryParse(value, out var year) ? year : null;

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    /// <summary>Returns every completed job's id, for the worker's startup recovery sweep to re-enqueue.
    /// Deliberately unfiltered — re-enqueuing an already-fully-processed job is a cheap no-op (snapshot lookup
    /// finds <c>IsTerminal</c> immediately), and pre-filtering here would just duplicate that check and risk
    /// drifting out of sync with it.</summary>
    public async Task<IReadOnlyList<long>> FindUnprocessedCompletedJobIdsAsync(CancellationToken ct) =>
        await db.LitigationSearchJobs
            .Where(j => j.Status == LitigationSearchJobStatus.Completed)
            .Select(j => j.LitigationSearchJobId)
            .ToListAsync(ct);
}
