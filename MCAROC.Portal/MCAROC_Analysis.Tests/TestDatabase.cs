using System.Data;
using MCAROC_Analysis.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>The SQL Server the integration tests run against. Defaults to a local SQLEXPRESS instance
/// (dev machines + the self-hosted reconciliation runner); CI on a GitHub-hosted runner overrides it
/// with the <c>MCAROC_TEST_CONNECTION</c> environment variable pointing at the SQL Server the workflow
/// stood up. Test classes reference <see cref="ConnectionString"/> rather than hard-coding the string.</summary>
internal static class TestDatabase
{
    private const string MigrationLockResourcePrefix = "MCAROC_Analysis_TestDatabaseMigration:";

    public static readonly string ConnectionString = Build();

    /// <summary>
    /// Runs the actual create+migrate sequence **at most once per test process**, no matter how many test
    /// classes' fixtures call <see cref="MigrateAsync"/> — every caller after the first just awaits this
    /// same completed task. This is the real fix for a failure mode that looked like a timing race but
    /// wasn't: three consecutive hosted CI runs on the exact same commit all failed identically with
    /// "Database already exists" (SQL error 1801) raised from inside EF's own <c>MigrateAsync</c> — not
    /// from the explicit <c>CREATE DATABASE</c> statement below, which already correctly tolerates 1801 —
    /// and a bounded retry around that inner call (an earlier attempt at this fix) did not help either,
    /// which only makes sense for a *persistent*, not transient, condition. The self-hosted runner's own
    /// process ran the query directly afterward and found the database perfectly healthy between runs
    /// (`ONLINE`/`MULTI_USER`, zero connected sessions) — so the database itself was never actually broken;
    /// what kept recurring, deterministically, was redundant *attempts* to create an already-created
    /// database from more than one <see cref="Microsoft.EntityFrameworkCore.DbContext"/> instance within the
    /// same process (multiple `IAsyncLifetime.InitializeAsync()` fixtures each independently calling this
    /// method). <see cref="Lazy{Task}"/> with execution-and-publication thread safety collapses that down to
    /// one real attempt, which is the correct fix regardless of exactly which EF-internal state made the
    /// redundant attempts fail rather than silently no-op. The cross-process <c>sp_getapplock</c> below is
    /// kept for the other case it was originally added for (a genuinely separate OS process — a stray local
    /// checkout, a different machine — racing this one), which this per-process gate cannot cover.
    /// </summary>
    private static readonly Lazy<Task> MigrationOnce = new(() => MigrateCoreAsync(CancellationToken.None),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task MigrateAsync(AppDbContext db, CancellationToken cancellationToken = default) =>
        // `db` and `cancellationToken` are accepted for call-site compatibility (every existing fixture
        // already has an AppDbContext in hand) but only the *first* caller's arguments are actually used —
        // migrating the database is not a per-DbContext-instance operation, so every later caller correctly
        // just awaits that first attempt's own task instead of touching the database again.
        MigrationOnce.Value;

    private static async Task MigrateCoreAsync(CancellationToken cancellationToken)
    {
        var target = new SqlConnectionStringBuilder(ConnectionString);
        if (string.IsNullOrWhiteSpace(target.InitialCatalog))
            throw new InvalidOperationException("The shared test database connection string must specify a database name.");

        var targetDatabase = target.InitialCatalog;
        target.InitialCatalog = "master";

        await using var lockConnection = new SqlConnection(target.ConnectionString);
        await lockConnection.OpenAsync(cancellationToken);

        await using var acquire = new SqlCommand("sp_getapplock", lockConnection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 150
        };
        acquire.Parameters.AddWithValue("@Resource", MigrationLockResourcePrefix + targetDatabase);
        acquire.Parameters.AddWithValue("@LockMode", "Exclusive");
        acquire.Parameters.AddWithValue("@LockOwner", "Session");
        acquire.Parameters.AddWithValue("@LockTimeout", 120_000);
        var returnValue = acquire.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;
        await acquire.ExecuteNonQueryAsync(cancellationToken);

        if ((int)returnValue.Value < 0)
            throw new InvalidOperationException($"Could not acquire the shared test-database migration lock for '{targetDatabase}' (sp_getapplock return {(int)returnValue.Value}).");

        try
        {
            try
            {
                await using var create = new SqlCommand("""
                    IF DB_ID(@databaseName) IS NULL
                    BEGIN
                        DECLARE @createDatabaseSql nvarchar(258) = N'CREATE DATABASE ' + QUOTENAME(@databaseName);
                        EXEC(@createDatabaseSql);
                    END
                    """, lockConnection);
                create.Parameters.Add("@databaseName", SqlDbType.NVarChar, 128).Value = targetDatabase;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex) when (ex.Number == 1801)
            {
                // A pre-existing local checkout can still be running the old, uncoordinated setup path.
                // SQL Server has committed that competing CREATE DATABASE by the time it returns 1801, so
                // continue with EF's idempotent migration while holding the lock.
            }

            // One AppDbContext, constructed here rather than accepted from a caller — see MigrateAsync's own
            // comment: this whole method now runs at most once per process, so there is no "the caller's db
            // instance" to use; any instance pointed at the same connection string does the same work.
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(ConnectionString).Options);

            // A short, defensive retry on top of the once-per-process gate above: db.Database.MigrateAsync
            // can independently attempt to create the database (EF's own SqlServerDatabaseCreator.CreateAsync,
            // invoked as MigrateAsync's own first step) — a second, separate code path from the explicit
            // CREATE DATABASE just above, and one this method cannot wrap in the same narrow try/catch
            // because it isn't our SQL to retry piecemeal. Not expected to fire in the common case now that
            // only one attempt happens per process at all; kept for the genuinely-separate-OS-process case
            // the cross-process lock above targets, where a competing CREATE can still land in this window.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await db.Database.MigrateAsync(cancellationToken);
                    break;
                }
                catch (SqlException ex) when (ex.Number == 1801 && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                }
            }
        }
        finally
        {
            await using var release = new SqlCommand("sp_releaseapplock", lockConnection)
            {
                CommandType = CommandType.StoredProcedure
            };
            release.Parameters.AddWithValue("@Resource", MigrationLockResourcePrefix + targetDatabase);
            release.Parameters.AddWithValue("@LockOwner", "Session");
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static string Build()
    {
        var baseString = Environment.GetEnvironmentVariable("MCAROC_TEST_CONNECTION") is { Length: > 0 } fromEnv
            ? fromEnv
            : @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";

        var builder = new SqlConnectionStringBuilder(baseString);
        if (!baseString.Contains("Encrypt", StringComparison.OrdinalIgnoreCase))
        {
            builder.Encrypt = false;
        }
        if (!baseString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
        {
            builder.TrustServerCertificate = true;
        }
        if (!baseString.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase))
        {
            builder.CommandTimeout = 120;
        }
        return builder.ConnectionString;
    }
}
