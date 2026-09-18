namespace MCAROC_Analysis.Migrations;

/// <summary>The enforced pre-flight check <c>MultiHolderOperationalSlotLeases</c>'s <c>Up()</c>/<c>Down()</c>
/// run before touching <c>OperationalSlotLeases</c> — extracted to its own class (rather than an inline
/// string in the migration) so the exact SQL text can be executed and verified directly in a test, against
/// a real throwaway connection, without needing to drive a full EF migration run to prove it fires.
///
/// That migration drops and recreates <c>OperationalSlotLeases</c> outright (see its own remarks on why —
/// the table holds only transient in-flight state, nothing worth migrating row-by-row). Dropping it while
/// a real upload or unpack is genuinely in flight would silently discard that lease instead of its holder
/// releasing it normally, and — worse — nothing would then stop a second operation from starting
/// concurrently with the one that "lost" its lease, which is exactly what leases exist to prevent. See
/// README, "Deploying the multi-holder slot-lease migration" for the full stop/drain → confirm → migrate →
/// restart sequence this exists to enforce the "confirm" step of.
///
/// A first version of this guard just ran <c>IF EXISTS (... ExpiresUtc &gt; SYSUTCDATETIME())</c> with no
/// locking — a plain read under READ COMMITTED takes no lock that blocks a concurrent writer, so the check
/// could see zero active leases, and then, in the gap before <c>DropTable</c> actually runs (a separate
/// statement later in the same migration), a real in-flight
/// <c>OperationalSlotLeaseService.TryAcquireSlotAsync</c> call on another node could commit a brand new
/// lease — which <c>DropTable</c> would then destroy anyway. This version closes that gap by taking the
/// SAME <c>sp_getapplock</c> resources <c>TryAcquireSlotAsync</c> takes (one per slot type,
/// <c>@LockOwner = 'Transaction'</c>) BEFORE running the existence check, in the same transaction the whole
/// migration runs in (EF wraps a migration in one transaction by default). That makes the migration and
/// every real acquisition attempt on either slot type mutually exclusive: if an acquisition is already in
/// flight when the migration runs, the migration blocks until that acquisition's transaction resolves
/// (commit or rollback) before it even reads the table, so the existence check always sees the true final
/// state of anything that was in flight; if the migration acquires the locks first, no new acquisition on
/// either slot type can proceed until the migration's own transaction ends. The locks are held through
/// <c>DropTable</c> automatically (transaction-scoped) — no extra code needed for that part.</summary>
public static class OperationalSlotLeaseMigrationGuard
{
    /// <summary>The exact resource strings <see cref="MCAROC_Analysis.Services.McaFilings.OperationalSlotLeaseService"/>
    /// locks per slot type — duplicated here as literals rather than referenced from that class, since a
    /// migration is a historical, immutable record and must not silently pick up a future change to that
    /// class's constants.</summary>
    private static readonly string[] SlotLockResources = ["OperationalSlot_LargeUpload", "OperationalSlot_LargeUnpack"];

    public const string Message =
        "Cannot apply migration MultiHolderOperationalSlotLeases while an upload or unpack lease is " +
        "still active (OperationalSlotLeases has a row with a non-expired ExpiresUtc) - this migration " +
        "drops that table, which would silently discard in-flight lease state instead of the holder " +
        "releasing it normally. Stop or drain FilingProcessingWorker (unpack leases) and any in-flight " +
        "large-archive upload sessions (upload leases), confirm zero active leases " +
        "(SELECT COUNT(*) FROM OperationalSlotLeases WHERE ExpiresUtc > SYSUTCDATETIME() should be 0), " +
        "THEN run this migration, THEN start the new workers. See README, \"Deploying the multi-holder " +
        "slot-lease migration\".";

    private const string LockTimeoutMessage =
        "Could not acquire an operational slot lock while checking for active leases before migrating - " +
        "another operation is currently acquiring or holding one. Wait for it to finish and retry.";

    /// <summary>Acquires the LargeUpload and LargeUnpack sp_getapplock resources (transaction-scoped, so
    /// they release automatically when the migration's transaction commits or rolls back — nothing extra
    /// to unlock), then throws SQL error 51000 with <see cref="Message"/> if any row in
    /// <c>OperationalSlotLeases</c> has a non-expired <c>ExpiresUtc</c>. A no-op if the table doesn't exist
    /// at all (a fresh database that has never had a lease, or one already past this migration) — nothing
    /// to quiesce there, though the locks are still taken first regardless, so a concurrent acquisition
    /// that would CREATE the table for the first time is equally serialized against.</summary>
    public static string Sql => $@"
{string.Join("\n", SlotLockResources.Select(LockAcquisitionSql))}

IF OBJECT_ID('dbo.OperationalSlotLeases', 'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM OperationalSlotLeases WHERE ExpiresUtc IS NOT NULL AND ExpiresUtc > SYSUTCDATETIME())
        THROW 51000, '{Message}', 1;
END";

    private static string LockAcquisitionSql(string resource) => $@"
DECLARE @lockResult_{Suffix(resource)} INT;
EXEC @lockResult_{Suffix(resource)} = sp_getapplock @Resource = '{resource}', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
IF (@lockResult_{Suffix(resource)} < 0) THROW 51001, '{LockTimeoutMessage}', 1;";

    // sp_getapplock's own return value can only be captured into a locally-declared variable, and a T-SQL
    // batch can't declare two variables with the same name — this keeps the two acquisitions' variable
    // names distinct without hardcoding them per resource.
    private static string Suffix(string resource) => resource.Replace("OperationalSlot_", "");
}
