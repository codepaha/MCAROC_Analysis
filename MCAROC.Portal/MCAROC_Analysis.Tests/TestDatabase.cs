namespace MCAROC_Analysis.Tests;

/// <summary>The SQL Server the integration tests run against. Defaults to a local SQLEXPRESS instance
/// (dev machines + the self-hosted reconciliation runner); CI on a GitHub-hosted runner overrides it
/// with the <c>MCAROC_TEST_CONNECTION</c> environment variable pointing at the SQL Server the workflow
/// stood up. Test classes reference <see cref="ConnectionString"/> rather than hard-coding the string.</summary>
internal static class TestDatabase
{
    public static readonly string ConnectionString = Build();

    private static string Build()
    {
        var baseString = Environment.GetEnvironmentVariable("MCAROC_TEST_CONNECTION") is { Length: > 0 } fromEnv
            ? fromEnv
            : @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

        // The self-hosted Windows CI box runs several agents + local builds against one SQLEXPRESS
        // instance, and the default 30s command timeout is exceeded under that load — MigrateAsync and
        // the dossier assembler's wide reads time out even though nothing is deadlocked. 120s absorbs
        // the contention while still failing a genuinely stuck query in bounded time.
        return baseString.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase)
            ? baseString
            : baseString.TrimEnd(';') + ";Command Timeout=120";
    }
}
