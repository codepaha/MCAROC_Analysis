using System.Data;
using Microsoft.Data.SqlClient;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>The database half of the offline name-resolution harness (issue #293, plan §5A.4): the labelled
/// and database-derived case sets, and the two retrieval strategies I1 compares. Read-only — it never writes
/// and changes no behaviour; <c>Tools/EvaluateNameResolution</c> runs it and prints the report.
///
/// Both strategies are baselines, not the resolver: they exist so I2's ranking has a measured floor to beat
/// and a report to be judged by. Only Company and LLP rows are searched, matching AutoFetch (an FCRN can't be
/// used there).</summary>
public static class NameResolutionHarness
{
    public const string LegacyPrefix = "Legacy prefix search (today's AutoFetch)";
    public const string NormalizedLookup = "Normalized-name lookup (I1 baseline)";
    public const string Resolver = "CompanyNameResolver (I2)";

    public const string Labelled = "Labelled";
    public const string Duplicate = "Duplicate";
    public const string CompanyLlpTwin = "CompanyLlpTwin";

    private const int CandidateLimit = 50;

    /// <summary>Mirrors <c>AutoFetchController.SearchLocalMasterDataAsync</c> (<c>Name LIKE query%</c>,
    /// alphabetical, top 10). It has no score of its own, so an exact (case-insensitive) name match scores 1.0
    /// and any other prefix hit 0.5 — i.e. it auto-selects only a unique exact hit, the best today's search
    /// could honestly do.</summary>
    public static async Task<IReadOnlyList<NameCandidate>> LegacyPrefixAsync(
        SqlConnection connection, string input, CancellationToken cancellationToken = default)
    {
        var query = input.Trim();
        if (query.Length == 0) return [];

        await using var command = new SqlCommand("""
            SELECT TOP (10) Identifier, Name
            FROM dbo.CompanyMasterRecords
            WHERE RecordType IN ('Company', 'Llp') AND Name LIKE @Prefix ESCAPE '\'
            ORDER BY Name;
            """, connection);
        command.Parameters.Add("@Prefix", SqlDbType.NVarChar, 410).Value = EscapeLike(query) + "%";

        var candidates = new List<NameCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var exact = string.Equals(reader.GetString(1).Trim(), query, StringComparison.OrdinalIgnoreCase);
            candidates.Add(new NameCandidate(reader.GetString(0), exact ? 1.0 : 0.5));
        }
        return candidates;
    }

    /// <summary>Normalizes the input with the same <see cref="CompanyNameNormalizer"/> that filled the stored
    /// columns, then seeks the new indexes: exact <c>NameNormalized</c> scores 1.0; exact <c>NameCore</c> scores
    /// 0.8 when the legal form agrees and 0.6 when it doesn't ("X LLP" asked, "X PRIVATE LIMITED" found).</summary>
    public static async Task<IReadOnlyList<NameCandidate>> NormalizedLookupAsync(
        SqlConnection connection, string input, CancellationToken cancellationToken = default)
    {
        var normalized = CompanyNameNormalizer.Normalize(input);
        if (normalized.NameNormalized.Length == 0) return [];

        await using var command = new SqlCommand("""
            SELECT Identifier, NameNormalized, NameCore, EntityForm FROM (
                SELECT TOP (@Limit) Identifier, NameNormalized, NameCore, EntityForm
                FROM dbo.CompanyMasterRecords
                WHERE RecordType IN ('Company', 'Llp') AND NameNormalized = @Normalized) exact
            UNION
            SELECT Identifier, NameNormalized, NameCore, EntityForm FROM (
                SELECT TOP (@Limit) Identifier, NameNormalized, NameCore, EntityForm
                FROM dbo.CompanyMasterRecords
                WHERE RecordType IN ('Company', 'Llp') AND NameCore = @Core) core;
            """, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = CandidateLimit;
        command.Parameters.Add("@Normalized", SqlDbType.NVarChar, CompanyNameNormalizer.MaxLength).Value = normalized.NameNormalized;
        command.Parameters.Add("@Core", SqlDbType.NVarChar, CompanyNameNormalizer.MaxLength).Value = normalized.NameCore;

        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        var form = normalized.EntityForm.ToString();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var score = !reader.IsDBNull(1) && reader.GetString(1) == normalized.NameNormalized ? 1.0
                : !reader.IsDBNull(3) && reader.GetString(3) == form ? 0.8
                : 0.6;
            var id = reader.GetString(0);
            best[id] = Math.Max(best.GetValueOrDefault(id), score);
        }
        return best.Select(kv => new NameCandidate(kv.Key, kv.Value)).OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>The #294 resolver's ranking: full retrieval (exact, core, prefix, rare-word overlap) scored by
    /// <see cref="CompanyNameResolver.Rank"/>, no hints. The report's thresholds and tie rule then show where its
    /// <c>AutoSelectThreshold</c> and <c>MinimumMargin</c> should sit.</summary>
    public static async Task<IReadOnlyList<NameCandidate>> ResolverAsync(
        SqlConnection connection, string input, CancellationToken cancellationToken = default)
    {
        var candidates = await CompanyNameCandidateRetriever.RetrieveAsync(connection, input, cancellationToken);
        return CompanyNameResolver.Rank(input, ResolutionHints.None, candidates)
            .Select(s => new NameCandidate(s.Candidate.Identifier, s.Score))
            .ToList();
    }

    /// <summary>Real requests whose company was actually ingested (so the identifier is confirmed) and whose
    /// identifier is in the master: the requester-typed name, and the CIN/LLPIN it turned out to be.</summary>
    public static async Task<IReadOnlyList<NameResolutionCase>> LoadLabelledRequestsAsync(
        SqlConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = new SqlCommand("""
            SELECT r.CompanyName, m.Identifier
            FROM dbo.Requests r
            INNER JOIN dbo.CompanyMasterRecords m
                ON m.Identifier = COALESCE(r.Cin, r.Llpin, r.AutoFetchCompanyIdentifier)
            WHERE r.LatestCompletedIngestionRunId IS NOT NULL AND LEN(r.CompanyName) > 0;
            """, connection) { CommandTimeout = 300 };

        var cases = new List<NameResolutionCase>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            cases.Add(new NameResolutionCase(reader.GetString(0), reader.GetString(1), Labelled));
        return cases;
    }

    /// <summary>A deterministic spread of master rows (every <paramref name="modulus"/>-th by a stable hash of
    /// the identifier) to feed <see cref="SyntheticNameCaseGenerator"/>. ~3.7M / 1000 ≈ 3.7k rows.</summary>
    public static async Task<IReadOnlyList<(string Identifier, string Name)>> SampleMasterAsync(
        SqlConnection connection, int modulus = 1000, CancellationToken cancellationToken = default)
    {
        await using var command = new SqlCommand("""
            SELECT Identifier, Name
            FROM dbo.CompanyMasterRecords
            WHERE RecordType IN ('Company', 'Llp') AND ABS(CHECKSUM(Identifier)) % @Modulus = 0
            ORDER BY Identifier;
            """, connection) { CommandTimeout = 300 };
        command.Parameters.Add("@Modulus", SqlDbType.Int).Value = modulus;

        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    /// <summary>Names shared by two or more master rows (same NameNormalized): with no distinguishing hint there
    /// is no right answer, so the expected outcome is to abstain (<c>ExpectedIdentifier = null</c>).</summary>
    public static async Task<IReadOnlyList<NameResolutionCase>> LoadDuplicateCasesAsync(
        SqlConnection connection, int limit = 500, CancellationToken cancellationToken = default)
    {
        await using var command = new SqlCommand("""
            SELECT TOP (@Limit) MIN(Name)
            FROM dbo.CompanyMasterRecords
            WHERE RecordType IN ('Company', 'Llp') AND NameNormalized IS NOT NULL
            GROUP BY NameNormalized
            HAVING COUNT(*) > 1
            ORDER BY NameNormalized;
            """, connection) { CommandTimeout = 300 };
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;

        var cases = new List<NameResolutionCase>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            cases.Add(new NameResolutionCase(reader.GetString(0), null, Duplicate));
        return cases;
    }

    /// <summary>A company and an LLP sharing one NameCore ("SUNRISE ADVISORS PRIVATE LIMITED" / "SUNRISE ADVISORS
    /// LLP"): asked for the company by its full name, the strategy must pick the company, not its twin.</summary>
    public static async Task<IReadOnlyList<NameResolutionCase>> LoadCompanyLlpTwinCasesAsync(
        SqlConnection connection, int limit = 500, CancellationToken cancellationToken = default)
    {
        await using var command = new SqlCommand("""
            SELECT TOP (@Limit) c.Name, c.Identifier
            FROM dbo.CompanyMasterRecords c
            WHERE c.RecordType = 'Company' AND c.NameCore IS NOT NULL
              AND EXISTS (SELECT 1 FROM dbo.CompanyMasterRecords l WHERE l.RecordType = 'Llp' AND l.NameCore = c.NameCore)
              AND NOT EXISTS (SELECT 1 FROM dbo.CompanyMasterRecords d
                              WHERE d.RecordType IN ('Company', 'Llp') AND d.NameNormalized = c.NameNormalized
                                AND d.Identifier <> c.Identifier)
            ORDER BY c.Identifier;
            """, connection) { CommandTimeout = 300 };
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;

        var cases = new List<NameResolutionCase>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            cases.Add(new NameResolutionCase(reader.GetString(0), reader.GetString(1), CompanyLlpTwin));
        return cases;
    }

    /// <summary>Runs one strategy over every case and scores it.</summary>
    public static async Task<NameResolutionReport> EvaluateAsync(
        string strategyName,
        Func<string, CancellationToken, Task<IReadOnlyList<NameCandidate>>> strategy,
        IReadOnlyList<NameResolutionCase> cases,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<NameResolutionOutcome>(cases.Count);
        foreach (var c in cases)
            outcomes.Add(new NameResolutionOutcome(c, await strategy(c.InputName, cancellationToken)));
        return NameResolutionEvaluator.Evaluate(strategyName, outcomes);
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");
}
