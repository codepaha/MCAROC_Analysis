using System.Data;
using Microsoft.Data.SqlClient;

namespace MCAROC_Analysis.Services.CompanyMaster;

public sealed record CompanyMasterNameBackfillResult(long RowsScanned, long RowsUpdated, long RowsStillMissing);

/// <summary>Fills <c>NameNormalized</c>/<c>NameCore</c>/<c>EntityForm</c> on existing
/// <c>CompanyMasterRecords</c> rows (issue #293). Needed once after the migration that adds the columns, and
/// again whenever <see cref="CompanyNameNormalizer.Version"/> changes; a full re-import or a sync promotion
/// also fills them, but only for the rows it touches.
///
/// Works in primary-key order in batches, so it never holds a long lock on the ~3.7M-row table and can be
/// stopped and re-run at any point. Each batch is computed in C# (the one normalizer shared with import and
/// sync — the rules are not duplicated in T-SQL) and applied with a single set-based UPDATE. The UPDATE only
/// touches a row whose <c>Name</c> is still the one that was normalized, so a sync promotion renaming a
/// company mid-run can't be overwritten with columns derived from its old name.</summary>
public static class CompanyMasterNameBackfill
{
    public const int DefaultBatchSize = 50_000;

    /// <param name="onlyMissing">True: only rows with a missing derived column (the post-migration fill).
    /// False: recompute every row (after a normalizer change); unchanged rows are read but not rewritten.</param>
    public static async Task<CompanyMasterNameBackfillResult> RunAsync(
        SqlConnection connection, bool onlyMissing, int batchSize = DefaultBatchSize,
        Action<long, long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);

        // COLLATE DATABASE_DEFAULT: a temp table otherwise takes tempdb's collation, which need not match the
        // database's, and the joins below would then fail with a collation conflict.
        await ExecuteAsync(connection, """
            IF OBJECT_ID('tempdb..#NameBackfill') IS NOT NULL DROP TABLE #NameBackfill;
            CREATE TABLE #NameBackfill (
                Identifier nvarchar(25) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                Name nvarchar(400) COLLATE DATABASE_DEFAULT NOT NULL,
                NameNormalized nvarchar(450) COLLATE DATABASE_DEFAULT NOT NULL,
                NameCore nvarchar(450) COLLATE DATABASE_DEFAULT NOT NULL,
                EntityForm nvarchar(20) COLLATE DATABASE_DEFAULT NOT NULL);
            """, cancellationToken);

        var selectSql = $"""
            SELECT TOP (@BatchSize) Identifier, Name, NameNormalized, NameCore, EntityForm
            FROM dbo.CompanyMasterRecords
            WHERE Identifier > @After
            {(onlyMissing ? "AND (NameNormalized IS NULL OR NameCore IS NULL OR EntityForm IS NULL)" : "")}
            ORDER BY Identifier;
            """;

        long scanned = 0, updated = 0;
        var after = string.Empty;
        var changes = NewChangeTable();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changes.Clear();
            var read = 0;

            await using (var select = new SqlCommand(selectSql, connection) { CommandTimeout = 300 })
            {
                select.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
                select.Parameters.Add("@After", SqlDbType.NVarChar, 25).Value = after;
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    read++;
                    after = reader.GetString(0);
                    var name = reader.GetString(1);
                    var derived = CompanyNameNormalizer.Normalize(name);
                    var form = derived.EntityForm.ToString();

                    var unchanged = !reader.IsDBNull(2) && !reader.IsDBNull(3) && !reader.IsDBNull(4)
                        && reader.GetString(2) == derived.NameNormalized
                        && reader.GetString(3) == derived.NameCore
                        && reader.GetString(4) == form;
                    if (!unchanged)
                        changes.Rows.Add(after, name, derived.NameNormalized, derived.NameCore, form);
                }
            }

            if (read == 0) break;
            scanned += read;

            if (changes.Rows.Count > 0)
            {
                using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#NameBackfill", BulkCopyTimeout = 300 })
                {
                    foreach (DataColumn column in changes.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    await bulk.WriteToServerAsync(changes, cancellationToken);
                }

                await using var apply = new SqlCommand("""
                    UPDATE c
                    SET c.NameNormalized = b.NameNormalized, c.NameCore = b.NameCore, c.EntityForm = b.EntityForm
                    FROM dbo.CompanyMasterRecords c
                    INNER JOIN #NameBackfill b ON b.Identifier = c.Identifier
                    WHERE c.Name = b.Name;
                    TRUNCATE TABLE #NameBackfill;
                    """, connection) { CommandTimeout = 300 };
                updated += await apply.ExecuteNonQueryAsync(cancellationToken);
            }

            progress?.Invoke(scanned, updated);
            if (read < batchSize) break;
        }

        await ExecuteAsync(connection, "DROP TABLE #NameBackfill;", cancellationToken);
        return new CompanyMasterNameBackfillResult(scanned, updated, await CountMissingAsync(connection, cancellationToken));
    }

    /// <summary>Rows still missing any derived column — the backfill's "100% covered" check (issue #293).</summary>
    public static async Task<long> CountMissingAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var count = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.CompanyMasterRecords
            WHERE NameNormalized IS NULL OR NameCore IS NULL OR EntityForm IS NULL;
            """, connection) { CommandTimeout = 300 };
        return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
    }

    private static DataTable NewChangeTable()
    {
        var table = new DataTable();
        table.Columns.Add("Identifier", typeof(string));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("NameNormalized", typeof(string));
        table.Columns.Add("NameCore", typeof(string));
        table.Columns.Add("EntityForm", typeof(string));
        return table;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
