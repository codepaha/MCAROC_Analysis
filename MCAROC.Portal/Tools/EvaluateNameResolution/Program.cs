// Offline name-resolution harness (issue #293, plan §5A.4). Read-only: it queries CompanyMasterRecords and
// Requests, changes nothing, and prints a precision/recall report per threshold for each retrieval strategy.
// This is the number the resolver's auto-select stays switched off behind (≥ 99.5% precision on the
// auto-selected subset, read against the 95% CI's lower bound); the third strategy is the #294 resolver.
//
// Run after the AddCompanyMasterNameNormalization migration and the name backfill:
//   dotnet run --project Tools/EvaluateNameResolution -- [--connection="..."] [--sample-modulus=1000] [--out=report.md]

using System.Globalization;
using MCAROC_Analysis.Services.CompanyMaster;
using Microsoft.Data.SqlClient;

var connectionString = Arg("--connection=")
    ?? @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis;Trusted_Connection=True;TrustServerCertificate=True;";
var modulus = int.Parse(Arg("--sample-modulus=") ?? "1000", CultureInfo.InvariantCulture);
var outPath = Arg("--out=");

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

var missing = await CompanyMasterNameBackfill.CountMissingAsync(connection);
if (missing > 0)
{
    Console.Error.WriteLine($"{missing:N0} master rows have no derived name columns yet — the normalized strategy would be " +
        "measured on partial data. Run: dotnet run --project Tools/ImportCompanyMasterData -- --backfill-names");
    return 2;
}

await using (var tokenCount = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.CompanyNameTokens;", connection))
{
    if ((long)(await tokenCount.ExecuteScalarAsync())! == 0)
    {
        Console.Error.WriteLine("The resolver's word index (CompanyNameTokens) is empty, so the resolver strategy would be " +
            "measured without its word-overlap retrieval. Run: dotnet run --project Tools/ImportCompanyMasterData -- --backfill-names");
        return 2;
    }
}

Console.WriteLine("Building the case set...");
var sample = await NameResolutionHarness.SampleMasterAsync(connection, modulus);
var cases = new List<NameResolutionCase>();
cases.AddRange(await NameResolutionHarness.LoadLabelledRequestsAsync(connection));
cases.AddRange(SyntheticNameCaseGenerator.Generate(sample));
cases.AddRange(await NameResolutionHarness.LoadDuplicateCasesAsync(connection));
cases.AddRange(await NameResolutionHarness.LoadCompanyLlpTwinCasesAsync(connection));
foreach (var group in cases.GroupBy(c => c.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
    Console.WriteLine($"  {group.Key}: {group.Count():N0}");

var reports = new[]
{
    await NameResolutionHarness.EvaluateAsync(NameResolutionHarness.LegacyPrefix,
        (name, ct) => NameResolutionHarness.LegacyPrefixAsync(connection, name, ct), cases),
    await NameResolutionHarness.EvaluateAsync(NameResolutionHarness.NormalizedLookup,
        (name, ct) => NameResolutionHarness.NormalizedLookupAsync(connection, name, ct), cases),
    await NameResolutionHarness.EvaluateAsync(NameResolutionHarness.Resolver,
        (name, ct) => NameResolutionHarness.ResolverAsync(connection, name, ct), cases),
};

var text = $"# Name-resolution harness — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, normalizer v{CompanyNameNormalizer.Version}\n\n"
    + string.Join("\n", reports.Select(NameResolutionEvaluator.FormatReport));
Console.WriteLine();
Console.WriteLine(text);
if (outPath is not null)
{
    await File.WriteAllTextAsync(outPath, text);
    Console.WriteLine($"Report written to {outPath}");
}
return 0;

string? Arg(string prefix) => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
