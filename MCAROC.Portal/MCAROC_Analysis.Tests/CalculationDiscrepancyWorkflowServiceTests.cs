using System.Data.Common;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Full coverage of the #164 reviewer decision workflow. No Google-credential dependency, so this
/// service is constructed directly against the real test database (unlike the AI services).</summary>
public class CalculationDiscrepancyWorkflowServiceTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static AppDbContext CreateContextWithInterceptor(DbCommandInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).AddInterceptors(interceptor).Options);

    private static CalculationDiscrepancyWorkflowService NewService(AppDbContext db) =>
        new(db, NullLogger<CalculationDiscrepancyWorkflowService>.Instance);

    /// <summary>Genuine failure injection at the ADO.NET command level — throws when a command matching
    /// the given table name and SQL verb is about to execute, regardless of whether it came from
    /// SaveChangesAsync or ExecuteUpdateAsync (both go through this same interception point). Used to
    /// prove the transaction wrapping the status transition + hold create/release actually rolls back
    /// everything on a real failure, not just the one already-covered unique-index race.</summary>
    private sealed class ThrowOnTableWriteInterceptor(string tableName, string verb) : DbCommandInterceptor
    {
        private bool ShouldThrow(DbCommand command) =>
            command.CommandText.Contains(tableName, StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains(verb, StringComparison.OrdinalIgnoreCase);

        // An INSERT with an identity column reads back the generated key via ExecuteReader (an OUTPUT
        // clause / SELECT SCOPE_IDENTITY()), not ExecuteNonQuery — both hooks are needed to reliably catch
        // every write shape EF Core's SaveChangesAsync/ExecuteUpdateAsync might use.
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (ShouldThrow(command)) throw new InvalidOperationException($"Injected {verb} failure on {tableName} for test.");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (ShouldThrow(command)) throw new InvalidOperationException($"Injected {verb} failure on {tableName} for test.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static long NextId() => Random.Shared.NextInt64(1, long.MaxValue / 2);

    private static async Task<(CalculationAuditSnapshot Snapshot, CalculationLedgerEntry Ledger)> SeedSnapshotAndLedgerAsync(
        AppDbContext db, decimal? ledgerValue = 100m)
    {
        var id = NextId();
        var snapshot = new CalculationAuditSnapshot { RequestId = id, IngestionRunId = id, AnalysisRunId = id, CreatedUtc = DateTime.UtcNow };
        db.CalculationAuditSnapshots.Add(snapshot);
        var ledger = new CalculationLedgerEntry
        {
            Snapshot = snapshot, CalculationKey = "Test.Key", MetricLabel = "Test", Period = "FY2025",
            ValueNumeric = ledgerValue, CreatedUtc = DateTime.UtcNow
        };
        db.CalculationLedgerEntries.Add(ledger);
        await db.SaveChangesAsync();
        return (snapshot, ledger);
    }

    private static async Task<CalculationDiscrepancy> SeedAiCandidateAsync(
        AppDbContext db, CalculationAuditSnapshot snapshot, CalculationLedgerEntry ledger,
        decimal? claimedActualValue = 100m, int requiredApprovals = 1)
    {
        var discrepancy = new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            SourceType = CalculationDiscrepancySourceType.AiCandidate,
            PrimaryLedgerEntryId = ledger.CalculationLedgerEntryId,
            ClaimSummary = "AI candidate claim", ClaimedActualValue = claimedActualValue,
            Status = CalculationDiscrepancyStatus.Open, Severity = null, RequiredApprovals = requiredApprovals,
            CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow
        };
        db.CalculationDiscrepancies.Add(discrepancy);
        await db.SaveChangesAsync();
        return discrepancy;
    }

    private static async Task<CalculationDiscrepancy> SeedDeterministicConfirmedAsync(AppDbContext db, CalculationAuditSnapshot snapshot, CalculationLedgerEntry ledger)
    {
        var discrepancy = new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            SourceType = CalculationDiscrepancySourceType.Deterministic,
            OriginCheckKey = "Test.Check",
            PrimaryLedgerEntryId = ledger.CalculationLedgerEntryId,
            ClaimSummary = "Deterministic claim", Status = CalculationDiscrepancyStatus.Confirmed,
            Severity = CalculationDiscrepancySeverity.Material, CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow
        };
        db.CalculationDiscrepancies.Add(discrepancy);
        await db.SaveChangesAsync();
        return discrepancy;
    }

    private static async Task<CalculationDiscrepancy> Reload(AppDbContext db, long id) =>
        await db.CalculationDiscrepancies.FirstAsync(d => d.CalculationDiscrepancyId == id);

    // ── Confirm ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_MaterialSeverity_TransitionsAndCreatesAHold()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);

        var result = await NewService(db).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        Assert.True(result.Success);
        await using var verifyDb = CreateContext();
        var reloaded = await Reload(verifyDb, discrepancy.CalculationDiscrepancyId);
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, reloaded.Status);
        Assert.Equal(CalculationDiscrepancySeverity.Material, reloaded.Severity);
        var hold = await verifyDb.CalculationArtifactHolds.SingleAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId);
        Assert.True(hold.IsActive);
        Assert.Equal(CalculationArtifactHoldReason.ConfirmedMaterialDiscrepancyNoException, hold.HoldReason);
    }

    [Fact]
    public async Task Confirm_MinorSeverity_TransitionsButCreatesNoHold()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);

        var result = await NewService(db).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Minor, null, CancellationToken.None);

        Assert.True(result.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        Assert.False(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId));
    }

    [Fact]
    public async Task Confirm_WhenLedgerValueHasChangedSinceTheCandidateWasRaised_IsRefused()
    {
        // Regression test for "deterministic reproduction result required before Confirm sticks" — the
        // ledger value has drifted from what the candidate claimed as "actual", so the claim is stale.
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db, ledgerValue: 999m);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger, claimedActualValue: 100m);

        var result = await NewService(db).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        Assert.False(result.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Open, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
    }

    [Fact]
    public async Task Confirm_OnADeterministicDiscrepancy_IsRefused()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedDeterministicConfirmedAsync(db, snapshot, ledger);

        var result = await NewService(db).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        Assert.False(result.Success);
    }

    // ── RequiredApprovals gate (proven before it's ever turned on) ─────────────────────────────────

    [Fact]
    public async Task Confirm_WithTwoRequiredApprovals_DoesNotTransitionAfterOnlyOne()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger, requiredApprovals: 2);

        var result = await NewService(db).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        Assert.True(result.Success); // the approval itself is recorded successfully...
        await using var verifyDb = CreateContext();
        // ...but the transition has not happened yet, and (critically) no hold exists yet either.
        Assert.Equal(CalculationDiscrepancyStatus.Open, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        Assert.False(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId));
    }

    [Fact]
    public async Task Confirm_WithTwoRequiredApprovals_TransitionsOnceASecondDistinctReviewerAgrees()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger, requiredApprovals: 2);
        var service = NewService(db);

        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);
        var second = await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "bob", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        Assert.True(second.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        Assert.True(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId));
    }

    [Fact]
    public async Task Confirm_WithTwoRequiredApprovals_TheSameReviewerTwiceNeverSatisfiesTheGate()
    {
        // A second identical call from the same reviewer is now treated as an idempotent retry (it
        // succeeds, matching the "your approval already stands" reality) rather than a hard failure — but
        // it must still never record a second approval row or count as a second distinct reviewer, so a
        // RequiredApprovals=2 gate can never be satisfied by one person calling twice.
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger, requiredApprovals: 2);
        var service = NewService(db);

        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);
        var secondAttemptSameReviewer = await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        Assert.True(secondAttemptSameReviewer.Success); // idempotent no-op, not an error
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Open, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        var approvalCount = await verifyDb.CalculationDiscrepancyApprovals.CountAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == CalculationDiscrepancyDecisionAction.Confirm);
        Assert.Equal(1, approvalCount); // still only Alice's one approval — no duplicate was recorded
    }

    [Fact]
    public async Task Confirm_DisagreeingSeverity_IsRefusedAndNeverPersisted()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger, requiredApprovals: 2);
        var service = NewService(db);

        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);
        var disagreeing = await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "bob", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None);

        Assert.False(disagreeing.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Open, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        // Bob's disagreeing opinion was never recorded at all — only Alice's original approval exists.
        // This is what makes the disagreement recoverable rather than a permanent deadlock: nothing about
        // this discrepancy's stored state reflects Bob's conflicting Critical claim.
        var confirmApprovals = await verifyDb.CalculationDiscrepancyApprovals
            .Where(a => a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == CalculationDiscrepancyDecisionAction.Confirm)
            .ToListAsync();
        var onlyApproval = Assert.Single(confirmApprovals);
        Assert.Equal("alice", onlyApproval.ReviewerName);
    }

    [Fact]
    public async Task Confirm_AfterASeverityDisagreement_CanStillReachAValidTerminalOutcome()
    {
        // Regression test for the deadlock this class of bug would otherwise cause: Bob's rejected,
        // unpersisted disagreement must not prevent a third reviewer who agrees with the original severity
        // from completing the transition.
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger, requiredApprovals: 2);
        var service = NewService(db);

        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);
        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "bob", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None); // refused, per above

        var carolAgrees = await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "carol", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        Assert.True(carolAgrees.Success);
        await using var verifyDb = CreateContext();
        var reloaded = await Reload(verifyDb, discrepancy.CalculationDiscrepancyId);
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, reloaded.Status);
        Assert.Equal(CalculationDiscrepancySeverity.Material, reloaded.Severity);
        Assert.True(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId && h.IsActive));
    }

    // ── Failure injection: transition + hold create/release must commit atomically ─────────────────

    [Fact]
    public async Task Confirm_WhenTheHoldInsertFailsForAnyReason_RollsBackTheStatusTransitionToo()
    {
        // Real failure injection (not just the already-covered unique-index race): a command interceptor
        // throws specifically on the hold INSERT. Proves a Confirmed Critical/Material discrepancy can
        // never exist without its active hold, for ANY failure reason, not only a collision.
        await using var seedDb = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(seedDb);
        var discrepancy = await SeedAiCandidateAsync(seedDb, snapshot, ledger);

        await using var faultyDb = CreateContextWithInterceptor(new ThrowOnTableWriteInterceptor("CalculationArtifactHolds", "INSERT"));
        var result = await NewService(faultyDb).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None);

        Assert.False(result.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Open, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        Assert.False(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId));
        // The reviewer's own approval survived — it committed in its own, separate, already-successful
        // transaction before the faulty one ran.
        Assert.Equal(1, await verifyDb.CalculationDiscrepancyApprovals.CountAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == CalculationDiscrepancyDecisionAction.Confirm));

        // Retrying without the injected fault succeeds without re-approving (idempotent retry).
        await using var retryDb = CreateContext();
        var retry = await NewService(retryDb).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None);
        Assert.True(retry.Success);
        await using var finalDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, (await Reload(finalDb, discrepancy.CalculationDiscrepancyId)).Status);
        Assert.True(await finalDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId && h.IsActive));
        Assert.Equal(1, await finalDb.CalculationDiscrepancyApprovals.CountAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == CalculationDiscrepancyDecisionAction.Confirm));
    }

    [Fact]
    public async Task AcceptException_WhenTheHoldReleaseFailsForAnyReason_RollsBackTheStatusTransitionToo()
    {
        // Proves the reverse invariant: an accepted exception can never exist while its hold remains
        // active. Injects a real failure on the hold-release UPDATE specifically.
        await using var seedDb = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(seedDb);
        var discrepancy = await SeedAiCandidateAsync(seedDb, snapshot, ledger);
        await NewService(seedDb).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        await using var faultyDb = CreateContextWithInterceptor(new ThrowOnTableWriteInterceptor("CalculationArtifactHolds", "UPDATE"));
        var result = await NewService(faultyDb).AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "bob", "Documented internal exception.", CancellationToken.None);

        Assert.False(result.Success);
        await using var verifyDb = CreateContext();
        var reloaded = await Reload(verifyDb, discrepancy.CalculationDiscrepancyId);
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, reloaded.Status); // unchanged — the transition never committed
        Assert.True(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId && h.IsActive));

        await using var retryDb = CreateContext();
        var retry = await NewService(retryDb).AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "bob", "Documented internal exception.", CancellationToken.None);
        Assert.True(retry.Success);
        await using var finalDb = CreateContext();
        var finalReload = await Reload(finalDb, discrepancy.CalculationDiscrepancyId);
        Assert.Equal(CalculationDiscrepancyStatus.AcceptedAsSourceException, finalReload.Status);
        Assert.False(await finalDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId && h.IsActive));
    }

    // ── Concurrency: genuine multi-context races, not sequential awaits ────────────────────────────

    [Fact]
    public async Task Confirm_ConcurrentDifferentReviewers_CreatesExactlyOneTransitionAndOneHold()
    {
        // RequiredApprovals=1 (today's real default) — two different reviewers race to confirm the same
        // discrepancy at the same time (e.g. two people opening the same review queue). Each call uses its
        // own DbContext/service instance, mirroring two genuinely separate concurrent HTTP requests.
        await using var seedDb = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(seedDb);
        var discrepancy = await SeedAiCandidateAsync(seedDb, snapshot, ledger);

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();
        var callA = NewService(dbA).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);
        var callB = NewService(dbB).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "bob", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);
        var results = await Task.WhenAll(callA, callB);

        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        var holdCount = await verifyDb.CalculationArtifactHolds.CountAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId);
        Assert.Equal(1, holdCount);
        // Both reviewers' approvals are still legitimately recorded even though only one of them could
        // have won the transition/hold-creation race.
        var approvalCount = await verifyDb.CalculationDiscrepancyApprovals.CountAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == CalculationDiscrepancyDecisionAction.Confirm);
        Assert.Equal(2, approvalCount);
    }

    [Fact]
    public async Task Confirm_ConcurrentSameReviewerDoubleSubmit_RecordsExactlyOneApprovalAndOneHold()
    {
        // Simulates a double-click / accidental double form submit by the same reviewer.
        await using var seedDb = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(seedDb);
        var discrepancy = await SeedAiCandidateAsync(seedDb, snapshot, ledger);

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();
        var callA = NewService(dbA).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None);
        var callB = NewService(dbB).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None);
        var results = await Task.WhenAll(callA, callB);

        // Exactly one of the two identical concurrent submissions succeeds; the other is refused as an
        // already-recorded decision (caught by the unique DB constraint, not just the upfront check).
        Assert.Single(results, r => r.Success);
        Assert.Single(results, r => !r.Success);

        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        var holdCount = await verifyDb.CalculationArtifactHolds.CountAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId);
        Assert.Equal(1, holdCount);
        var approvalCount = await verifyDb.CalculationDiscrepancyApprovals.CountAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == CalculationDiscrepancyDecisionAction.Confirm && a.ReviewerName == "alice");
        Assert.Equal(1, approvalCount);
    }

    [Fact]
    public async Task ApprovalSequence_IsUniquePerDiscrepancyAndAction_RejectsADuplicateInsert()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);

        db.CalculationDiscrepancyApprovals.Add(new CalculationDiscrepancyApproval
        {
            CalculationDiscrepancyId = discrepancy.CalculationDiscrepancyId, ApprovalSequence = 1,
            DecisionAction = CalculationDiscrepancyDecisionAction.Confirm, ReviewerName = "alice", DecidedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.CalculationDiscrepancyApprovals.Add(new CalculationDiscrepancyApproval
        {
            CalculationDiscrepancyId = discrepancy.CalculationDiscrepancyId, ApprovalSequence = 1,
            DecisionAction = CalculationDiscrepancyDecisionAction.Confirm, ReviewerName = "bob", DecidedUtc = DateTime.UtcNow
        });

        // Proves the (DiscrepancyId, DecisionAction, ApprovalSequence) unique index is a real database
        // constraint, not just a service-layer convention a race could slip past.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ReviewerActionPair_IsUniquePerDiscrepancy_RejectsADuplicateInsert()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);

        db.CalculationDiscrepancyApprovals.Add(new CalculationDiscrepancyApproval
        {
            CalculationDiscrepancyId = discrepancy.CalculationDiscrepancyId, ApprovalSequence = 1,
            DecisionAction = CalculationDiscrepancyDecisionAction.Confirm, ReviewerName = "alice", DecidedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.CalculationDiscrepancyApprovals.Add(new CalculationDiscrepancyApproval
        {
            CalculationDiscrepancyId = discrepancy.CalculationDiscrepancyId, ApprovalSequence = 2,
            DecisionAction = CalculationDiscrepancyDecisionAction.Confirm, ReviewerName = "alice", DecidedUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ActiveHold_IsUniquePerDiscrepancy_RejectsADuplicateInsert()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);
        await db.CalculationDiscrepancies.Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, CalculationDiscrepancyStatus.Confirmed).SetProperty(d => d.Severity, CalculationDiscrepancySeverity.Critical));

        db.CalculationArtifactHolds.Add(new CalculationArtifactHold
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId, HoldReason = CalculationArtifactHoldReason.ConfirmedCriticalDiscrepancy,
            IsActive = true, SourceDiscrepancyId = discrepancy.CalculationDiscrepancyId, CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.CalculationArtifactHolds.Add(new CalculationArtifactHold
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId, HoldReason = CalculationArtifactHoldReason.ConfirmedCriticalDiscrepancy,
            IsActive = true, SourceDiscrepancyId = discrepancy.CalculationDiscrepancyId, CreatedUtc = DateTime.UtcNow
        });

        // Proves the last-resort idempotent-hold guard is a real database constraint.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Confirm_UndefinedSeverityValue_IsRefused_LeavesOpen_CreatesNoApprovalNoHold()
    {
        // An unvalidated model-bound/cast value outside Minor/Material/Critical must never reach the
        // hold-branch check ("Critical or Material") — an undefined value would silently fall through as
        // if it meant Minor, letting a bogus severity confirm without ever creating its hold.
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);
        var undefinedSeverity = (CalculationDiscrepancySeverity)999;

        var result = await NewService(db).ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", undefinedSeverity, null, CancellationToken.None);

        Assert.False(result.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Open, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        Assert.Equal(0, await verifyDb.CalculationDiscrepancyApprovals.CountAsync(a => a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId));
        Assert.False(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId));
    }

    [Fact]
    public async Task DiscrepancySeverity_CheckConstraint_RejectsAnUndefinedValue_EvenBypassingTheService()
    {
        // Defense in depth: a direct write (raw SQL, a future code path that doesn't call
        // Enum.IsDefined) must also be rejected at the database level, not just by the service.
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);

        db.CalculationDiscrepancies.Add(new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            SourceType = CalculationDiscrepancySourceType.AiCandidate,
            PrimaryLedgerEntryId = ledger.CalculationLedgerEntryId,
            ClaimSummary = "x", Status = CalculationDiscrepancyStatus.Confirmed,
            Severity = (CalculationDiscrepancySeverity)999, // bypasses the service's Enum.IsDefined guard entirely
            CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ── Reject ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_RequiresAReason()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);

        var result = await NewService(db).RejectAsync(discrepancy.CalculationDiscrepancyId, "alice", "", CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Reject_Transitions()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);

        var result = await NewService(db).RejectAsync(discrepancy.CalculationDiscrepancyId, "alice", "Not a real issue", CancellationToken.None);

        Assert.True(result.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Rejected, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
    }

    // ── AcceptException — no route reachable for Critical, ever ────────────────────────────────────

    [Fact]
    public async Task AcceptException_RetryWithADifferentReason_IsRefused()
    {
        // RequiredApprovals=2 so the first AcceptException call records its approval but does not yet
        // complete the transition — exactly the window where a genuine transactional-failure retry would
        // also land, and where a reviewer resubmitting with different text must not be allowed to apply a
        // reason the durable audit trail doesn't actually contain.
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger, requiredApprovals: 2);
        var service = NewService(db);
        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);
        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "bob", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        var first = await service.AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "alice", "Reason A.", CancellationToken.None);
        Assert.True(first.Success);

        var mismatchedRetry = await service.AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "alice", "Reason B — different text.", CancellationToken.None);
        Assert.False(mismatchedRetry.Success);

        await using var verifyDb = CreateContext();
        // Still awaiting the second reviewer's approval — the mismatched retry changed nothing.
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        var approval = await verifyDb.CalculationDiscrepancyApprovals.SingleAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == CalculationDiscrepancyDecisionAction.AcceptException);
        Assert.Equal("Reason A.", approval.ReviewerNotes); // the audit trail was never overwritten with Reason B

        // Retrying with the exact original reason still succeeds (once the second reviewer also agrees).
        var matchingRetry = await service.AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "alice", "Reason A.", CancellationToken.None);
        Assert.True(matchingRetry.Success);
        var secondReviewer = await service.AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "bob", "Reason A.", CancellationToken.None);
        Assert.True(secondReviewer.Success);

        await using var finalDb = CreateContext();
        var finalReload = await Reload(finalDb, discrepancy.CalculationDiscrepancyId);
        Assert.Equal(CalculationDiscrepancyStatus.AcceptedAsSourceException, finalReload.Status);
        Assert.Equal("Reason A.", finalReload.ExceptionReason);
    }

    [Fact]
    public async Task AcceptException_OnACriticalDiscrepancy_IsRefused()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);
        var service = NewService(db);
        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None);

        var result = await service.AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "alice", "trying to bypass the critical hold", CancellationToken.None);

        Assert.False(result.Success);
        await using var verifyDb = CreateContext();
        var reloaded = await Reload(verifyDb, discrepancy.CalculationDiscrepancyId);
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, reloaded.Status); // unchanged — still held
        Assert.True(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId && h.IsActive));
    }

    [Fact]
    public async Task AcceptException_RequiresAReason()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);
        var service = NewService(db);
        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        var result = await service.AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "alice", "  ", CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AcceptException_OnAConfirmedMaterialDiscrepancy_ReleasesTheHold()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);
        var service = NewService(db);
        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        var result = await service.AcceptExceptionAsync(discrepancy.CalculationDiscrepancyId, "bob", "Documented internal exception — corrected in the next filing cycle.", CancellationToken.None);

        Assert.True(result.Success);
        await using var verifyDb = CreateContext();
        var reloaded = await Reload(verifyDb, discrepancy.CalculationDiscrepancyId);
        Assert.Equal(CalculationDiscrepancyStatus.AcceptedAsSourceException, reloaded.Status);
        Assert.NotNull(reloaded.ExceptionReason);
        var hold = await verifyDb.CalculationArtifactHolds.SingleAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId);
        Assert.False(hold.IsActive);
        Assert.Equal("bob", hold.ReleasedByReviewerName);
    }

    // ── MarkFixedPendingReaudit / Resolve ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkFixedPendingReaudit_DoesNotReleaseTheHold()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);
        var service = NewService(db);
        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None);

        var result = await service.MarkFixedPendingReauditAsync(discrepancy.CalculationDiscrepancyId, "alice", "Source workbook re-uploaded.", CancellationToken.None);

        Assert.True(result.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.FixedPendingReaudit, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
        // The old, genuinely-defective snapshot stays held forever — only a new snapshot's own audit clears it.
        Assert.True(await verifyDb.CalculationArtifactHolds.AnyAsync(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId && h.IsActive));
    }

    [Fact]
    public async Task Resolve_OnlyReachableFromFixedPendingReaudit()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);
        var service = NewService(db);
        await service.ConfirmAsync(discrepancy.CalculationDiscrepancyId, "alice", CalculationDiscrepancySeverity.Critical, null, CancellationToken.None);

        var tooEarly = await service.ResolveAsync(discrepancy.CalculationDiscrepancyId, "alice", null, CancellationToken.None);
        Assert.False(tooEarly.Success);

        await service.MarkFixedPendingReauditAsync(discrepancy.CalculationDiscrepancyId, "alice", null, CancellationToken.None);
        var afterFix = await service.ResolveAsync(discrepancy.CalculationDiscrepancyId, "alice", "Re-audit confirmed the fix.", CancellationToken.None);

        Assert.True(afterFix.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Resolved, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
    }

    // ── Triage ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Triage_OnADeterministicDiscrepancy_IsRefused()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedDeterministicConfirmedAsync(db, snapshot, ledger);

        var result = await NewService(db).TriageAsync(discrepancy.CalculationDiscrepancyId, "alice", null, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Triage_Transitions()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);

        var result = await NewService(db).TriageAsync(discrepancy.CalculationDiscrepancyId, "alice", "Looking into this.", CancellationToken.None);

        Assert.True(result.Success);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Triaged, (await Reload(verifyDb, discrepancy.CalculationDiscrepancyId)).Status);
    }

    [Fact]
    public async Task WrongStatusTransition_IsRefused()
    {
        await using var db = CreateContext();
        var (snapshot, ledger) = await SeedSnapshotAndLedgerAsync(db);
        var discrepancy = await SeedAiCandidateAsync(db, snapshot, ledger);
        var service = NewService(db);
        await service.RejectAsync(discrepancy.CalculationDiscrepancyId, "alice", "Not real.", CancellationToken.None);

        var result = await service.RejectAsync(discrepancy.CalculationDiscrepancyId, "bob", "Also not real.", CancellationToken.None);

        Assert.False(result.Success); // already Rejected — a terminal status
    }
}
