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
    /// Migrates the shared SQL Server test database while holding an instance-wide session applock in
    /// <c>master</c>. xUnit is already serial within a test process, but this repository's self-hosted runner
    /// also shares SQLEXPRESS with local/other-agent test processes. Without this lock, two processes can both
    /// decide that the database does not exist and race in EF's create path, producing "Database already exists"
    /// before any test assertion executes.
    /// </summary>
    public static async Task MigrateAsync(AppDbContext db, CancellationToken cancellationToken = default)
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

            // db.Database.MigrateAsync itself can independently attempt to create the database (EF's own
            // SqlServerDatabaseCreator.CreateAsync, invoked as MigrateAsync's own first step) — a second,
            // separate code path from the explicit CREATE DATABASE just above, and one this method cannot
            // wrap in the same narrow try/catch because it isn't our SQL to retry piecemeal. Observed for
            // real on the self-hosted runner: two consecutive hosted CI runs both failed with error 1801
            // surfacing from inside MigrateAsync itself, not from the block above — this lock alone was not
            // sufficient, confirming the gap rather than assuming it. Retrying the whole MigrateAsync call
            // on 1801 covers this uncoordinated-racer case the same way the block above already does for its
            // own statement: by the time SQL Server returns "already exists", the competing CREATE has
            // already committed, so the retry proceeds straight to applying migrations.
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
