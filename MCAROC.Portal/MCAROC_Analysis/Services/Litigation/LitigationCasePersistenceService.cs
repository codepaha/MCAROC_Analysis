using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Parses a completed <see cref="LitigationSearchJob"/>'s raw report and persists its cases, orders
/// and source-report provenance. Built around an immutable <see cref="LitigationReportSnapshot"/> per report
/// (see its own remarks for why a copy separate from the mutable job row is required), which is also the
/// crash-safe, concurrency-safe, independently-recoverable unit of import work:
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
/// <item><b>Recovery is snapshot-driven, not job-driven.</b> <see cref="EnsureSnapshotAsync"/> is called once,
/// synchronously, the instant a job completes, in the SAME database transaction as that job's own
/// lease-guarded completion write (see <c>LitigationSearchJobService.PollUntilCompleteAsync</c>) — not just
/// ordered before it. If the lease check fails (another attempt has since reclaimed the job), the whole
/// transaction rolls back, undoing the snapshot admission along with it: a fenced-out worker can never leave
/// a committed-but-orphaned snapshot behind for <see cref="RecoverStaleWorkAsync"/> to import on a later
/// sweep. A raw process crash mid-transaction gets the same all-or-nothing outcome from SQL Server itself.
/// From then on, <see cref="RecoverStaleWorkAsync"/> queries <see cref="LitigationReportSnapshot"/> directly:
/// a request's <c>LitigationSearchJob</c> row is reused across reruns and its <c>Status</c> can move away
/// from Completed at any time (<c>CreateOrResetJobAsync</c>), so an older, still-incomplete snapshot must stay
/// reachable regardless of what the job's current status says.</item>
/// </list>
/// Only <see cref="BprReportFormat.Json"/> reports can be parsed today — <see cref="BprLitigationReportParser"/>
/// has no XLSX reader. A snapshot whose report is a different format is marked <c>Failed</c> (terminal, never
/// silently retried forever) rather than guessed at.</summary>
public sealed class LitigationCasePersistenceService(
    AppDbContext db, LitigationCasePersistenceQueue queue, LitigationOrderDocumentQueue orderDocumentQueue,
    IOptions<BprLitigationOptions> options, ILogger<LitigationCasePersistenceService> logger)
{
    private const int LeaseMinutes = 15; // generous: pure CPU/DB work, no external calls, "tens not thousands" of cases
    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly BprLitigationOptions _opts = options.Value;

    // ── Snapshot creation ──────────────────────────────────────────────────────────────────────────

    /// <summary>Creates the immutable snapshot row for (job id, report hash) if one doesn't already exist, and
    /// returns its id. Called once, synchronously, at the single trigger point (a job reaching Completed) —
    /// by the time anything is ever enqueued for <see cref="PersistSnapshotAsync"/>, the snapshot it names
    /// already exists, so that method never needs to create one. The caller (<c>LitigationSearchJobService</c>)
    /// runs this inside the same transaction as its own lease-guarded completion write and rolls both back
    /// together if the lease check fails — this method itself has no lease/job-token predicate of its own, so
    /// it must never be called outside that transaction, or a fenced-out worker's snapshot could be committed
    /// with no matching completion to vouch for it. Also race-safe against a concurrent creator via the unique
    /// index (defense in depth; under normal operation this runs at most once per real completion) — mirrors
    /// <c>CalculationAiAuditOrchestrator.EnqueueForSnapshotAsync</c>'s exact pattern for the same problem.</summary>
    public async Task<long> EnsureSnapshotAsync(
        long jobId, string reportHash, BprReportFormat reportFormat, byte[] rawReportBytes, DateTime retrievedUtc, CancellationToken ct)
    {
        var existingId = await db.LitigationReportSnapshots
            .Where(s => s.LitigationSearchJobId == jobId && s.ReportHash == reportHash)
            .Select(s => (long?)s.LitigationReportSnapshotId)
            .FirstOrDefaultAsync(ct);
        if (existingId is not null) return existingId.Value;

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = jobId, ReportHash = reportHash, ReportFormat = reportFormat,
            RawReportBytes = rawReportBytes, RawReportByteLength = rawReportBytes.LongLength,
            RetrievedUtc = retrievedUtc, Status = LitigationReportSnapshotStatus.Pending, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        try
        {
            await db.SaveChangesAsync(ct);
            return snapshot.LitigationReportSnapshotId;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            db.Entry(snapshot).State = EntityState.Detached;
            logger.LogInformation(
                "Lost a race creating the litigation report snapshot for job {JobId} — another attempt already created it.", jobId);
            return await db.LitigationReportSnapshots
                .Where(s => s.LitigationSearchJobId == jobId && s.ReportHash == reportHash)
                .Select(s => s.LitigationReportSnapshotId)
                .FirstAsync(ct);
        }
    }

    // ── Processing ─────────────────────────────────────────────────────────────────────────────────

    public async Task PersistSnapshotAsync(long snapshotId, CancellationToken ct)
    {
        var snapshot = await db.LitigationReportSnapshots.Include(s => s.SearchJob)
            .FirstOrDefaultAsync(s => s.LitigationReportSnapshotId == snapshotId, ct);
        if (snapshot is null || snapshot.IsTerminal) return; // gone, or already fully processed/permanently unparseable
        if (snapshot.SearchJob is null)
        {
            logger.LogError(
                "Litigation report snapshot {SnapshotId} has no reachable LitigationSearchJob {JobId} — cannot resolve a RequestId.",
                snapshotId, snapshot.LitigationSearchJobId);
            return;
        }
        var requestId = snapshot.SearchJob.RequestId;

        if (!await TryClaimAsync(snapshot, ct)) return; // already owned by a still-live attempt

        try
        {
            if (snapshot.ReportFormat != BprReportFormat.Json)
            {
                await MarkFailedAsync(snapshot,
                    $"No parser exists yet for report format {snapshot.ReportFormat} (only Json) — cases were not extracted.", ct);
                logger.LogWarning(
                    "Litigation report snapshot {SnapshotId} has format {Format} — no parser exists, marked Failed.",
                    snapshotId, snapshot.ReportFormat);
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
                logger.LogError(ex, "Litigation report snapshot {SnapshotId} failed to parse.", snapshotId);
                return;
            }

            // Every order this report surfaces — new or a re-surfaced existing one — is eligible for the same
            // retention window, computed once from this report's own retrieval time (not from whenever a
            // worker happens to get around to persisting it).
            var orderRetainedUntilUtc = snapshot.RetrievedUtc.AddDays(_opts.OrderRetentionDays);
            var orderDocumentsToEnqueue = new List<LitigationOrderDocument>();

            for (var i = snapshot.CasesPersistedCount; i < report.Cases.Count; i++)
                orderDocumentsToEnqueue.AddRange(
                    await PersistOneCaseAsync(requestId, snapshot, report.Cases[i], orderRetainedUntilUtc, ct));

            snapshot.Status = LitigationReportSnapshotStatus.Completed;
            snapshot.CompletedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            // Enqueued only after this snapshot's own commit succeeds — each order document was already
            // durably committed as part of its own case's SaveChangesAsync (see PersistOneCaseAsync), so this
            // is "notify workers now" not "make it safe," but still deliberately last: if the loop above were
            // interrupted (lease lost partway through), any already-committed-but-not-yet-enqueued documents
            // remain independently reachable via LitigationOrderDocumentService.RecoverStaleWorkAsync's own
            // sweep, exactly like this type's own snapshot-level recovery.
            foreach (var document in orderDocumentsToEnqueue)
                orderDocumentQueue.Enqueue(document.LitigationOrderDocumentId);

            logger.LogInformation(
                "Litigation report snapshot {SnapshotId} completed: {CaseCount} case(s) persisted, {OrderDocCount} order document(s) queued.",
                snapshotId, report.Cases.Count, orderDocumentsToEnqueue.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the lease mid-processing — another attempt has since claimed and possibly advanced this
            // snapshot. Stop touching it; whatever it committed stands, and a future attempt (if still needed)
            // will resume from its own CasesPersistedCount.
            logger.LogWarning(
                "Litigation report snapshot {SnapshotId} lost its claim mid-processing — another attempt has taken over.", snapshotId);
        }
    }

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

    private async Task<IReadOnlyList<LitigationOrderDocument>> PersistOneCaseAsync(
        long requestId, LitigationReportSnapshot snapshot, BprLitigationCase item, DateTime orderRetainedUntilUtc, CancellationToken ct)
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
            var (orderEntities, orderDocumentsToEnqueue) =
                await UpsertOrdersAsync(litigationCase, item.Orders, now, orderRetainedUntilUtc, ct);
            addedThisAttempt.AddRange(orderEntities);

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
                return orderDocumentsToEnqueue;
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

        return [];
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
    /// order-level id of their own); backstopped by a DB unique index for the concurrent-import case.
    ///
    /// Also admits (#243/LIT-03) each order's <see cref="LitigationOrderDocument"/> — a brand-new order gets a
    /// new Pending document row, added to the SAME tracked-entity graph as the order itself so both are part
    /// of the caller's single SaveChangesAsync (the same atomic-admission discipline as
    /// <c>EnsureSnapshotAsync</c>: never commit an order without a matching document eligible for import, and
    /// never leave a race between the two visible to any other connection). An order this report re-surfaces
    /// that ALREADY has a document is an explicit, auditable refresh opportunity if that document previously
    /// failed/expired and <paramref name="retainedUntilUtc"/> genuinely extends its window — see
    /// <see cref="LitigationOrderDocument"/>'s own remarks for why this is the only "refresh/refetch"
    /// mechanism epic #239 requires.
    ///
    /// Returns the newly-added/modified order and document entities (so a caller can precisely detach them if
    /// the surrounding SaveChanges fails) separately from the document entities that need enqueueing for
    /// download once that SaveChanges actually commits.</summary>
    private async Task<(IReadOnlyList<object> Added, IReadOnlyList<LitigationOrderDocument> ToEnqueue)> UpsertOrdersAsync(
        LitigationCase litigationCase, IReadOnlyList<BprLitigationOrder> orders, DateTime now, DateTime retainedUntilUtc, CancellationToken ct)
    {
        if (orders.Count == 0) return ([], []);

        // A brand-new case has LitigationCaseId == 0 at this point (not yet saved) — the query below then
        // correctly finds zero existing rows rather than needing a separate "is this new" branch.
        var existing = await db.LitigationCaseOrders
            .Where(o => o.LitigationCaseId == litigationCase.LitigationCaseId)
            .Select(o => new { o.LitigationCaseOrderId, o.PdfUrl, o.OrderDate, o.OrderType })
            .ToListAsync(ct);

        var added = new List<object>();
        var toEnqueue = new List<LitigationOrderDocument>();
        foreach (var order in orders)
        {
            var match = existing.FirstOrDefault(e => e.PdfUrl == order.PdfUrl && e.OrderDate == order.OrderDate && e.OrderType == order.OrderType);
            if (match is not null)
            {
                var document = await db.LitigationOrderDocuments.FirstOrDefaultAsync(d => d.LitigationCaseOrderId == match.LitigationCaseOrderId, ct);
                if (document is null)
                {
                    // A pre-existing order with no document yet — either persisted before #243 shipped, or an
                    // earlier attempt at this exact case failed before reaching admission. Admit it now.
                    document = new LitigationOrderDocument
                    {
                        LitigationCaseOrderId = match.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Pending,
                        RetainedUntilUtc = retainedUntilUtc, CreatedUtc = now
                    };
                    db.LitigationOrderDocuments.Add(document);
                    added.Add(document);
                    toEnqueue.Add(document);
                }
                else if (document.Status is LitigationOrderDocumentStatus.Failed or LitigationOrderDocumentStatus.Expired
                    && retainedUntilUtc > document.RetainedUntilUtc)
                {
                    document.RetainedUntilUtc = retainedUntilUtc;
                    document.Status = LitigationOrderDocumentStatus.Pending;
                    document.RefreshCount++;
                    document.LastRefreshedUtc = now;
                    // Already tracked (Modified), not Added, but still recorded here so a retry-on-conflict
                    // (see PersistOneCaseAsync) reverts this mutation too, rather than leaving it applied to
                    // the change tracker across attempts while this method's own return value moves on.
                    added.Add(document);
                    toEnqueue.Add(document);
                }
                continue;
            }

            var newOrder = new LitigationCaseOrder
            {
                Case = litigationCase, PdfUrl = order.PdfUrl, OrderDate = order.OrderDate, OrderType = order.OrderType, CreatedUtc = now
            };
            db.LitigationCaseOrders.Add(newOrder);
            added.Add(newOrder);

            var newDocument = new LitigationOrderDocument
            {
                Order = newOrder, Status = LitigationOrderDocumentStatus.Pending, RetainedUntilUtc = retainedUntilUtc, CreatedUtc = now
            };
            db.LitigationOrderDocuments.Add(newDocument);
            added.Add(newDocument);
            toEnqueue.Add(newDocument);
        }
        return (added, toEnqueue);
    }

    private static int? ParseYear(string? value) => int.TryParse(value, out var year) ? year : null;

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    // ── Recovery ───────────────────────────────────────────────────────────────────────────────────

    private void ScheduleRetry(long snapshotId, DateTime readyUtc, CancellationToken ct)
    {
        var delay = readyUtc - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            queue.Enqueue(snapshotId);
            return;
        }

        var capturedQueue = queue;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                capturedQueue.Enqueue(snapshotId);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — the row stays as-is; the next startup's recovery sweep re-schedules or
                // re-enqueues it.
            }
        }, ct);
    }

    /// <summary>Re-enqueues every non-terminal snapshot on startup — queried directly from
    /// <see cref="LitigationReportSnapshot"/>, independent of the associated job's current status (a rerun can
    /// move it away from Completed via <c>CreateOrResetJobAsync</c> at any time; an older, still-incomplete
    /// snapshot must remain reachable regardless — see this type's own remarks). A Pending snapshot, or an
    /// InProgress one whose lease has already expired, is enqueued immediately. An InProgress snapshot whose
    /// lease is still valid (the process that held it died without the lease itself expiring yet — e.g. an app
    /// restart) is instead scheduled for a delayed re-check at its own <c>LeaseExpiresUtc</c>, mirroring
    /// <c>LitigationSearchJobService</c>'s own <c>ScheduleRetry</c> pattern — never silently declined and left
    /// stuck the way a same-turn <see cref="TryClaimAsync"/> alone would leave it.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nonTerminal = await db.LitigationReportSnapshots
            .Where(s => s.Status == LitigationReportSnapshotStatus.Pending || s.Status == LitigationReportSnapshotStatus.InProgress)
            .Select(s => new { s.LitigationReportSnapshotId, s.Status, s.LeaseExpiresUtc })
            .ToListAsync(ct);

        foreach (var s in nonTerminal)
        {
            if (s.Status == LitigationReportSnapshotStatus.InProgress && s.LeaseExpiresUtc is { } expires && expires > now)
                ScheduleRetry(s.LitigationReportSnapshotId, expires, ct);
            else
                queue.Enqueue(s.LitigationReportSnapshotId);
        }

        return nonTerminal.Count;
    }
}
