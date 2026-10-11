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
    IOptions<BprLitigationOptions> options, ILogger<LitigationCasePersistenceService> logger,
    IOptions<MCAROC_Analysis.Services.LitigationModule.Bridge.LitigationBridgeOptions>? bridgeOptions = null)
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

        var requestId = await db.LitigationSearchJobs.Where(j => j.LitigationSearchJobId == jobId).Select(j => j.RequestId).SingleAsync(ct);
        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = jobId, RequestId = requestId, ReportHash = reportHash, ReportFormat = reportFormat,
            RawReportBytes = rawReportBytes, RawReportByteLength = rawReportBytes.LongLength,
            RetrievedUtc = retrievedUtc, Status = LitigationReportSnapshotStatus.Pending, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        try
        {
            // #291: OriginSnapshotId stays null — null means "this row is its own lineage root," so a
            // freshly-purchased snapshot needs no explicit self-reference (see the entity's own remarks).
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
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
            try
            {

                var byCnr = await FindAutoDedupeMatchAsync(requestId, identity, ct);
                var byProviderRecord = await FindSameProviderRecordAsync(requestId, item.ProviderCaseId, ct);
                var litigationCase = byCnr ?? byProviderRecord
                    ?? await FindLegacyRowWithoutProviderRecordAsync(requestId, identity, item, ct);
                // The CNR says this is case C; the provider's record id is already held by a different stored row P (an earlier report listed
                // the record before its CNR was known). The CNR is the stronger identity, so C takes the item — but the id stays with P, which is
                // left as it was with its reports, orders and documents: assigning it to C would break the per-request unique id, and giving C's
                // CNR to P would break the unique CNR. P is no longer listed by reports from here on, since this one lists C.
                var providerIdHeldElsewhere = byCnr is not null && byProviderRecord is not null && byProviderRecord.LitigationCaseId != byCnr.LitigationCaseId;
                // A case that was soft-merged into another (plan 4.6): the vendor row is applied to the survivor, never to the loser and never recreating it.
                var followedMerge = false;
                for (var hop = 0; hop < 5 && litigationCase?.MergedIntoCaseId is { } survivorId; hop++)
                {
                    litigationCase = await db.LitigationCases.FirstOrDefaultAsync(c => c.LitigationCaseId == survivorId && c.RequestId == requestId, ct) ?? litigationCase;
                    followedMerge = true;
                }
                var isNewCase = litigationCase is null;
                litigationCase ??= new LitigationCase { RequestId = requestId, FirstSeenUtc = now };
                if (isNewCase)
                {
                    db.LitigationCases.Add(litigationCase);
                    addedThisAttempt.Add(litigationCase);
                }

                // Vendor guard (plan 4.2c): columns the court portal owns for this request's case (verified link, portal-sourced) are not assigned from the vendor row. The check and the
                // write happen under the same per-case lock a live publication takes, in one transaction, so an overlapping publication cannot slip between them: whichever runs second
                // sees the other's committed result (the entity is reloaded under the lock, so the merge starts from what is stored now).
                IReadOnlySet<string>? portalOwned = null;
                if (!isNewCase)
                {
                    tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
                    await MCAROC_Analysis.Services.LitigationModule.Refresh.CaseLock.AcquireAsync(db, litigationCase.LitigationCaseId, ct);
                    await db.Entry(litigationCase).ReloadAsync(ct);
                    portalOwned = await MCAROC_Analysis.Services.LitigationModule.Refresh.LivePortalOwnership.OwnedColumnsAsync(db, requestId, litigationCase.LitigationCaseId, ct);
                }
                // When the row was matched through a merged loser, the loser keeps the CNR, proceeding type and provider ids (they are unique per request and are how this case is found again); the survivor gets everything else.
                ApplyFields(litigationCase, identity, item, now, keepProviderCaseId: providerIdHeldElsewhere || followedMerge, portalOwned, keepIdentity: followedMerge);
                // The bridge to the litigation module: the event is saved in the same transaction as this write (nothing is recorded while the bridge is off).
                if (MCAROC_Analysis.Services.LitigationModule.Bridge.LitigationBridgeWriter.RecordCaseUpserted(db, litigationCase, bridgeOptions?.Value, now) is { } bridgeEvent)
                    addedThisAttempt.Add(bridgeEvent);
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
                    if (tx is not null) await tx.CommitAsync(ct);
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
            finally
            {
                if (tx is not null) await tx.DisposeAsync();
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

    /// <summary>The same provider record seen again — a refresh returns the cases it already returned. The provider's record id is
    /// stable per case, so a case stored under it is this case, with or without a CNR. This is "already stored", not a judgement that two
    /// different records are one case: records with different ids stay separate (nothing is merged on case number or parties).</summary>
    private async Task<LitigationCase?> FindSameProviderRecordAsync(long requestId, string? providerCaseId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerCaseId)) return null;
        var id = providerCaseId.Trim();
        return await db.LitigationCases.FirstOrDefaultAsync(c => c.RequestId == requestId && c.ProviderCaseId == id, ct);
    }

    /// <summary>#425: rows stored before #388 have no provider record id, so a refresh that returns the same record (now with its id)
    /// finds nothing above and would store the case a second time. Such a row is this case only when it is the single row in the request
    /// that has no id, the same CSP, and the same proceeding type, case number and year — all three present on both sides, so blank
    /// fields never reduce the match to CSP alone — and no conflicting CNR (two different CNRs are two cases). Applying the item then
    /// fills in the id, so this runs once per legacy row. A row that already has an id is never adopted.</summary>
    private async Task<LitigationCase?> FindLegacyRowWithoutProviderRecordAsync(
        long requestId, LitigationCaseIdentityEvidence identity, BprLitigationCase item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.ProviderCaseId) || string.IsNullOrWhiteSpace(item.CspId)) return null;
        if (identity.ProceedingType is null || string.IsNullOrWhiteSpace(item.CaseNumber) || string.IsNullOrWhiteSpace(item.CaseYear)) return null;

        var candidates = await db.LitigationCases
            .Where(c => c.RequestId == requestId && c.ProviderCaseId == null && c.CspId == item.CspId)
            .ToListAsync(ct);
        var matches = candidates.Where(c =>
            c.ProceedingType == identity.ProceedingType
            && SameText(c.CaseNumber, item.CaseNumber)
            && SameText(c.CaseYear, item.CaseYear)
            && (c.Cnr is null || identity.Cnr is null || c.Cnr == identity.Cnr)).ToList();
        return matches.Count == 1 ? matches[0] : null;

        static bool SameText(string? left, string? right) =>
            string.Equals(left?.Trim() ?? "", right?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies one vendor observation to a case. The vendor's newer real values replace the held ones, as always; a field the vendor
    /// leaves <b>missing</b> (null, empty, a placeholder such as NA, an empty list) never erases a value the case already holds - it may have
    /// been filled by an earlier report or another source (<see cref="CaseValue"/>).</summary>
    private static void ApplyFields(LitigationCase litigationCase, LitigationCaseIdentityEvidence identity, BprLitigationCase item, DateTime now, bool keepProviderCaseId,
        IReadOnlySet<string>? portalOwned = null, bool keepIdentity = false)
    {
        // A column the portal owns keeps its value; every other column merges from the vendor as always.
        string? Merge(string column, string? current, string? incoming) => portalOwned is not null && portalOwned.Contains(column) ? current : CaseValue.Merge(current, incoming);
        if (!keepProviderCaseId) litigationCase.ProviderCaseId = CaseValue.Merge(litigationCase.ProviderCaseId, item.ProviderCaseId);
        litigationCase.CspId = CaseValue.Merge(litigationCase.CspId, item.CspId);
        if (!keepIdentity) litigationCase.Cnr = CaseValue.Merge(litigationCase.Cnr, identity.Cnr);
        if (!keepIdentity) litigationCase.ProceedingType = CaseValue.Merge(litigationCase.ProceedingType, identity.ProceedingType);
        litigationCase.CourtCategory = CaseValue.Merge(litigationCase.CourtCategory, item.CourtCategory);
        litigationCase.Direction = CaseValue.Merge(litigationCase.Direction, item.Direction);
        litigationCase.CaseClassification = CaseValue.Merge(litigationCase.CaseClassification, item.CaseClassification);
        litigationCase.Type = CaseValue.Merge(litigationCase.Type, item.Type);
        litigationCase.Court = Merge("Court", litigationCase.Court, item.Court);
        litigationCase.Bench = CaseValue.Merge(litigationCase.Bench, item.Bench);
        litigationCase.CaseNumber = CaseValue.Merge(litigationCase.CaseNumber, item.CaseNumber);
        litigationCase.CaseType = CaseValue.Merge(litigationCase.CaseType, item.CaseType);
        litigationCase.CaseYear = CaseValue.Merge(litigationCase.CaseYear, item.CaseYear);
        litigationCase.CaseStage = Merge("CaseStage", litigationCase.CaseStage, item.CaseStage);
        litigationCase.CaseStatus = Merge("CaseStatus", litigationCase.CaseStatus, item.CaseStatus);
        litigationCase.Act = CaseValue.Merge(litigationCase.Act, item.Act);
        litigationCase.FilingDate = Merge("FilingDate", litigationCase.FilingDate, item.FilingDate);
        litigationCase.LastHearingDate = Merge("LastHearingDate", litigationCase.LastHearingDate, item.LastHearingDate);
        litigationCase.NextHearingDate = Merge("NextHearingDate", litigationCase.NextHearingDate, item.NextHearingDate);
        litigationCase.DecisionDate = Merge("DecisionDate", litigationCase.DecisionDate, item.DecisionDate);
        litigationCase.State = CaseValue.Merge(litigationCase.State, item.State);
        litigationCase.District = CaseValue.Merge(litigationCase.District, item.District);
        litigationCase.PetitionersJson = CaseValue.Merge(litigationCase.PetitionersJson, item.PetitionersJson);
        litigationCase.RespondentsJson = CaseValue.Merge(litigationCase.RespondentsJson, item.RespondentsJson);
        litigationCase.PetitionerAdvocatesJson = CaseValue.Merge(litigationCase.PetitionerAdvocatesJson, item.PetitionerAdvocatesJson);
        litigationCase.RespondentAdvocatesJson = CaseValue.Merge(litigationCase.RespondentAdvocatesJson, item.RespondentAdvocatesJson);
        litigationCase.LastSeenUtc = now;
    }

    /// <summary>Adds only orders not already recorded for this case — de-duplicated on
    /// <c>(PdfUrl, OrderDate, OrderType)</c> or <c>(NormalizedPdfUrl, OrderDate, OrderType)</c> where
    /// <c>NormalizedPdfUrl</c> normalizes only ephemeral query parameters (such as S3 presigned signatures
    /// and expiry) while strictly preserving document-selecting path and query parameters (issue #362).
    /// Orders with distinct document identities on the same date/type are never collapsed.
    ///
    /// Also admits (#243/LIT-03) each order's <see cref="LitigationOrderDocument"/> — a brand-new order gets a
    /// new Pending document row, added to the SAME tracked-entity graph as the order itself so both are part
    /// of the caller's single SaveChangesAsync (the same atomic-admission discipline as
    /// <c>EnsureSnapshotAsync</c>: never commit an order without a matching document eligible for import, and
    /// never leave a race between the two visible to any other connection). An order this report re-surfaces
    /// that ALREADY has a document is an explicit, auditable refresh opportunity if that document previously
    /// failed/expired and <paramref name="retainedUntilUtc"/> genuinely extends its window (or if a fresh URL was
    /// provided) — see <see cref="LitigationOrderDocument"/>'s own remarks for why this is the only "refresh/refetch"
    /// mechanism epic #239 requires.
    ///
    /// Returns the newly-added/modified order and document entities (so a caller can precisely detach them if
    /// the surrounding SaveChanges fails) separately from the document entities that need enqueueing for
    /// download once that SaveChanges actually commits.</summary>
    private async Task<(IReadOnlyList<object> Added, IReadOnlyList<LitigationOrderDocument> ToEnqueue)> UpsertOrdersAsync(
        LitigationCase litigationCase, IReadOnlyList<BprLitigationOrder> orders, DateTime now, DateTime retainedUntilUtc, CancellationToken ct)
    {
        if (orders.Count == 0) return ([], []);

        // Load existing orders already persisted in the database for this case.
        var existing = litigationCase.LitigationCaseId != 0
            ? await db.LitigationCaseOrders
                .Where(o => o.LitigationCaseId == litigationCase.LitigationCaseId)
                .ToListAsync(ct)
            : [];

        var added = new List<object>();
        var toEnqueue = new List<LitigationOrderDocument>();
        // Tracks newly admitted orders and documents within this single snapshot attempt to handle
        // intra-report duplicates (e.g. two equivalent orders signed with different presigned tokens).
        var newlyAdmitted = new List<(LitigationCaseOrder Order, LitigationOrderDocument Document)>();
        // Tracks newly admitted documents for pre-existing orders within this batch to prevent
        // duplicate admissions when duplicate report rows match an existing order that has no document yet.
        var admittedExistingDocuments = new Dictionary<long, LitigationOrderDocument>();

        foreach (var order in orders)
        {
            // 1. Check if order matches an unsaved order newly added in this current method execution
            var newMatch = newlyAdmitted.FirstOrDefault(n => IsSameOrder(n.Order, order));
            if (newMatch.Order is not null)
            {
                // Intra-report duplicate of an order newly added in this batch.
                // If the second appearance has a fresh URL, update it.
                if (!string.IsNullOrWhiteSpace(order.PdfUrl) && newMatch.Order.PdfUrl != order.PdfUrl)
                {
                    newMatch.Order.PdfUrl = order.PdfUrl;
                }
                continue;
            }

            // 2. Check if order matches an existing database order
            var match = existing.FirstOrDefault(e => IsSameOrder(e, order));
            if (match is not null)
            {
                var urlChanged = !string.IsNullOrWhiteSpace(order.PdfUrl) && match.PdfUrl != order.PdfUrl;
                if (urlChanged)
                {
                    match.PdfUrl = order.PdfUrl;
                    added.Add(match);
                }

                if (!admittedExistingDocuments.TryGetValue(match.LitigationCaseOrderId, out var document))
                {
                    document = await db.LitigationOrderDocuments.FirstOrDefaultAsync(d => d.LitigationCaseOrderId == match.LitigationCaseOrderId, ct);
                }

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
                    admittedExistingDocuments[match.LitigationCaseOrderId] = document;
                }
                else if (document.Status is LitigationOrderDocumentStatus.Failed or LitigationOrderDocumentStatus.Expired
                        or LitigationOrderDocumentStatus.NotPublished
                    && (retainedUntilUtc > document.RetainedUntilUtc || urlChanged))
                {
                    if (retainedUntilUtc > document.RetainedUntilUtc)
                    {
                        document.RetainedUntilUtc = retainedUntilUtc;
                    }
                    document.Status = LitigationOrderDocumentStatus.Pending;
                    document.RefreshCount++;
                    document.LastRefreshedUtc = now;
                    // Already tracked (Modified), not Added, but still recorded here so a retry-on-conflict
                    // (see PersistOneCaseAsync) reverts this mutation too, rather than leaving it applied to
                    // the change tracker across attempts while this method's own return value moves on.
                    added.Add(document);
                    toEnqueue.Add(document);
                    admittedExistingDocuments[match.LitigationCaseOrderId] = document;
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

            newlyAdmitted.Add((newOrder, newDocument));
        }
        return (added, toEnqueue);
    }

    private static bool IsSameOrder(LitigationCaseOrder existing, BprLitigationOrder candidate)
    {
        // 1. Exact match on (PdfUrl, OrderDate, OrderType)
        if (existing.PdfUrl == candidate.PdfUrl && existing.OrderDate == candidate.OrderDate && existing.OrderType == candidate.OrderType)
            return true;

        // 2. Normalized URL match: only compare normalized URLs if both are present and date/type match.
        // Strips ephemeral signing parameters (e.g. S3 presigned tokens) while preserving document-selecting
        // paths and query parameters.
        if (existing.OrderDate == candidate.OrderDate && existing.OrderType == candidate.OrderType)
        {
            var existingNorm = NormalizePdfUrl(existing.PdfUrl);
            var candidateNorm = NormalizePdfUrl(candidate.PdfUrl);
            if (!string.IsNullOrWhiteSpace(existingNorm) && !string.IsNullOrWhiteSpace(candidateNorm) && existingNorm == candidateNorm)
                return true;
        }

        return false;
    }

    private static readonly HashSet<string> EphemeralQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-amz-algorithm", "x-amz-credential", "x-amz-date", "x-amz-expires",
        "x-amz-signedheaders", "x-amz-signature", "x-amz-security-token",
        "awsaccesskeyid", "signature", "expires"
    };

    private static string NormalizePdfUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var trimmed = url.Trim();
        var qIdx = trimmed.IndexOf('?');
        if (qIdx == -1) return trimmed;

        var baseUri = trimmed[..qIdx];
        var query = trimmed[(qIdx + 1)..];
        var fIdx = query.IndexOf('#');
        var fragment = string.Empty;
        if (fIdx != -1)
        {
            fragment = query[fIdx..];
            query = query[..fIdx];
        }

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var pair in pairs)
        {
            var eqIdx = pair.IndexOf('=');
            var key = eqIdx != -1 ? pair[..eqIdx] : pair;
            if (!EphemeralQueryKeys.Contains(key))
            {
                kept.Add(pair);
            }
        }

        return kept.Count > 0
            ? $"{baseUri}?{string.Join("&", kept)}{fragment}"
            : $"{baseUri}{fragment}";
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
