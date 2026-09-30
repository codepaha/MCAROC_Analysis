using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Shared plumbing for #194's hybrid-retrieval tests, which need real SQL Server Full-Text Search.
/// FTS is an optional server feature (absent from plain SQL Server Express and the stock mssql/server Linux
/// image), so these tests skip where it isn't installed — except where <c>MCAROC_REQUIRE_FULLTEXT=true</c> (the
/// Linux CI job, whose SQL Server image installs FTS on purpose), where a missing FTS install or index is a hard
/// failure, so the lexical path can never go silently untested.</summary>
internal static class FullTextTestSupport
{
    private static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("MCAROC_REQUIRE_FULLTEXT"), "true", StringComparison.OrdinalIgnoreCase);

    public static async Task RequireFullTextIndexAsync(string tableName)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CASE WHEN FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(@table)) THEN 1 ELSE 0 END
            """;
        cmd.Parameters.AddWithValue("@table", tableName);
        var indexed = (int)(await cmd.ExecuteScalarAsync())! == 1;

        if (Required)
            Assert.True(indexed, $"MCAROC_REQUIRE_FULLTEXT=true but {tableName} has no full-text index (is Full-Text Search installed?).");
        else
            Skip.IfNot(indexed, $"SQL Server Full-Text Search is not installed or {tableName} is not full-text indexed.");
    }

    /// <summary>Change-tracked full-text population is asynchronous (the gotcha #194 calls out), so a test that
    /// asserts on a lexical hit waits — deterministically, by probing for the exact row — until that row is
    /// searchable, instead of sleeping for a guessed duration and flaking when population is slow.</summary>
    public static async Task WaitUntilIndexedAsync(string tableName, string keyColumn, long key, string containsQuery)
    {
        var sw = Stopwatch.StartNew();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        while (true)
        {
            await using var cmd = connection.CreateCommand();
            // Table/column names are test-code constants, never external input.
            cmd.CommandText = $"SELECT COUNT(*) FROM CONTAINSTABLE({tableName}, ChunkText, @q) ft WHERE ft.[KEY] = @key";
            cmd.Parameters.AddWithValue("@q", containsQuery);
            cmd.Parameters.AddWithValue("@key", key);
            if ((int)(await cmd.ExecuteScalarAsync())! > 0) return;

            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"{tableName}.{keyColumn}={key} was not full-text indexed for {containsQuery} within 60s.");
            await Task.Delay(250);
        }
    }
}
