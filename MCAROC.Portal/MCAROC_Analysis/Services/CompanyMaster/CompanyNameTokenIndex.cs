using System.Data;
using Microsoft.Data.SqlClient;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>The inverted word index behind the resolver's token-overlap retrieval (issue #294, plan §5A.2 step 2):
/// one <c>CompanyNameTokens</c> row per (word of <c>NameCore</c>, identifier), Company and LLP rows only.
///
/// Chosen over SQL Server Full-Text Search because it works on every edition — Full-Text is an optional
/// feature not guaranteed on SQL Server Express — and CI can test it. Words come from the stored
/// <c>NameCore</c>, which <see cref="CompanyNameNormalizer"/> already produced single-spaced and normalized,
/// so splitting on a space is all the T-SQL does: no normalization rule is duplicated here. Needs database
/// compatibility level 130+ (<c>STRING_SPLIT</c>). One-letter words are not indexed.
///
/// Kept current by: <see cref="RebuildAsync"/> after the bulk import and after the name backfill, and the sync
/// promotion's own per-batch refresh (<see cref="RefreshForBatchSql"/>).</summary>
public static class CompanyNameTokenIndex
{
    public const int TokenMaxLength = 100;
    private const int RebuildBatchSize = 100_000;

    /// <summary>Rebuilds the whole index from <c>CompanyMasterRecords.NameCore</c>, in identifier batches so the
    /// log never has to hold ~11M inserted rows at once. Token-overlap retrieval sees a partial index while this
    /// runs; exact and prefix retrieval are unaffected.</summary>
    public static async Task<long> RebuildAsync(SqlConnection connection, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using (var truncate = new SqlCommand("TRUNCATE TABLE dbo.CompanyNameTokens;", connection) { CommandTimeout = 300 })
            await truncate.ExecuteNonQueryAsync(cancellationToken);

        long inserted = 0;
        var after = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var batch = new SqlCommand($"""
                DECLARE @Batch TABLE (Identifier nvarchar(25) COLLATE DATABASE_DEFAULT PRIMARY KEY, NameCore nvarchar(450) COLLATE DATABASE_DEFAULT);
                INSERT INTO @Batch (Identifier, NameCore)
                SELECT TOP (@BatchSize) Identifier, NameCore
                FROM dbo.CompanyMasterRecords
                WHERE Identifier > @After
                ORDER BY Identifier;

                INSERT INTO dbo.CompanyNameTokens (Token, Identifier)
                SELECT DISTINCT LEFT(s.value, {TokenMaxLength}), b.Identifier
                FROM @Batch b
                INNER JOIN dbo.CompanyMasterRecords m ON m.Identifier = b.Identifier
                CROSS APPLY STRING_SPLIT(b.NameCore, ' ') s
                WHERE m.RecordType IN ('Company', 'Llp') AND b.NameCore IS NOT NULL AND LEN(s.value) >= 2;

                SELECT @@ROWCOUNT AS Inserted, (SELECT MAX(Identifier) FROM @Batch) AS LastIdentifier;
                """, connection) { CommandTimeout = 600 };
            batch.Parameters.Add("@BatchSize", SqlDbType.Int).Value = RebuildBatchSize;
            batch.Parameters.Add("@After", SqlDbType.NVarChar, 25).Value = after;

            await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(1)) break;
            inserted += reader.GetInt32(0);
            after = reader.GetString(1);
            progress?.Invoke(inserted);
        }
        return inserted;
    }

    /// <summary>T-SQL fragment for <c>CompanyMasterDeltaService</c>'s promotion batch: replaces the tokens of every
    /// identifier the batch just updated or inserted, inside the same transaction as the row change itself. Uses
    /// the batch's <c>@UpdatedIds</c>/<c>@InsertedIds</c> table variables.</summary>
    public static readonly string RefreshForBatchSql = $"""
            DECLARE @TokenIds TABLE (Identifier nvarchar(25) COLLATE DATABASE_DEFAULT PRIMARY KEY);
            INSERT INTO @TokenIds (Identifier)
            SELECT DISTINCT s.Identifier
            FROM dbo.Staging_CompanyMasterRecords s
            WHERE s.StagingId IN (SELECT StagingId FROM @UpdatedIds UNION SELECT StagingId FROM @InsertedIds);

            DELETE t FROM dbo.CompanyNameTokens t INNER JOIN @TokenIds i ON i.Identifier = t.Identifier;

            INSERT INTO dbo.CompanyNameTokens (Token, Identifier)
            SELECT DISTINCT LEFT(v.value, {TokenMaxLength}), c.Identifier
            FROM dbo.CompanyMasterRecords c
            INNER JOIN @TokenIds i ON i.Identifier = c.Identifier
            CROSS APPLY STRING_SPLIT(c.NameCore, ' ') v
            WHERE c.RecordType IN ('Company', 'Llp') AND c.NameCore IS NOT NULL AND LEN(v.value) >= 2;
""";
}
