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
/// concurrently with the one that "lost" its lease, which is exactly what leases exist to prevent. This
/// check makes that a hard failure instead of a silent one: see README, "Deploying the multi-holder
/// slot-lease migration" for the full stop/drain → confirm → migrate → restart sequence this exists to
/// enforce the "confirm" step of.</summary>
public static class OperationalSlotLeaseMigrationGuard
{
    public const string Message =
        "Cannot apply migration MultiHolderOperationalSlotLeases while an upload or unpack lease is " +
        "still active (OperationalSlotLeases has a row with a non-expired ExpiresUtc) - this migration " +
        "drops that table, which would silently discard in-flight lease state instead of the holder " +
        "releasing it normally. Stop or drain FilingProcessingWorker (unpack leases) and any in-flight " +
        "large-archive upload sessions (upload leases), confirm zero active leases " +
        "(SELECT COUNT(*) FROM OperationalSlotLeases WHERE ExpiresUtc > SYSUTCDATETIME() should be 0), " +
        "THEN run this migration, THEN start the new workers. See README, \"Deploying the multi-holder " +
        "slot-lease migration\".";

    /// <summary>Throws SQL error 51000 with <see cref="Message"/> if any row in <c>OperationalSlotLeases</c>
    /// has a non-expired <c>ExpiresUtc</c>. A no-op if the table doesn't exist at all (a fresh database
    /// that has never had a lease, or one already past this migration) — nothing to quiesce there.</summary>
    public static string Sql => $@"
IF OBJECT_ID('dbo.OperationalSlotLeases', 'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM OperationalSlotLeases WHERE ExpiresUtc IS NOT NULL AND ExpiresUtc > SYSUTCDATETIME())
        THROW 51000, '{Message}', 1;
END";
}
