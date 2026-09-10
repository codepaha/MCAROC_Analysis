namespace MCAROC_Analysis.Tests;

/// <summary>The SQL Server the integration tests run against. Defaults to a local SQLEXPRESS instance
/// (dev machines + the self-hosted reconciliation runner); CI on a GitHub-hosted runner overrides it
/// with the <c>MCAROC_TEST_CONNECTION</c> environment variable pointing at the SQL Server the workflow
/// stood up. Test classes reference <see cref="ConnectionString"/> rather than hard-coding the string.</summary>
internal static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MCAROC_TEST_CONNECTION") is { Length: > 0 } fromEnv
            ? fromEnv
            : @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";
}
