using System.Data;
using System.Text;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>Candidate retrieval for the name resolver (issue #294, plan §5A.2 step 2): at most
/// <see cref="MaxCandidates"/> Company/LLP master rows, gathered in order of evidence strength — exact
/// <c>NameNormalized</c>, exact <c>NameCore</c>, <c>NameCore</c> prefix, then shared rare words from
/// <see cref="CompanyNameTokenIndex"/>. Every stage is an index seek; there is no fuzzy matching in SQL — typos
/// and word order are scored in memory by <see cref="CompanyNameResolver"/>, which is why the word stage exists:
/// it reaches a reordered or misspelt name through the words that did survive.</summary>
public static class CompanyNameCandidateRetriever
{
    public const int MaxCandidates = 50;

    /// <summary>Words on more rows than this ("INDIA", "TRADING", "SERVICES") are useless for narrowing a search
    /// and expensive to group; they are never used as retrieval keys, only as ranking evidence.</summary>
    public const int MaxTokenFrequency = 100_000;

    /// <summary>How many of the input's rarest words drive the word-overlap stage.</summary>
    private const int TokensUsed = 3;

    private const string CandidateColumns =
        "Identifier, RecordType, Name, NameNormalized, NameCore, EntityForm, Status, State, District, PinCode, RegistrationDate, Category, Class, ListingStatus";

    public static async Task<IReadOnlyList<MasterCandidate>> RetrieveAsync(
        SqlConnection connection, string inputName, CancellationToken cancellationToken = default)
    {
        var input = CompanyNameNormalizer.Normalize(inputName);
        if (input.NameNormalized.Length == 0) return [];
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<string> ids)
        {
            foreach (var id in ids)
                if (found.Count < MaxCandidates && seen.Add(id)) found.Add(id);
        }

        Add(await IdentifiersAsync(connection, "NameNormalized = @Value", input.NameNormalized, cancellationToken));
        Add(await IdentifiersAsync(connection, "NameCore = @Value", input.NameCore, cancellationToken));
        if (found.Count < MaxCandidates)
            Add(await IdentifiersAsync(connection, @"NameCore LIKE @Value ESCAPE '\'", EscapeLike(input.NameCore) + "%", cancellationToken));
        if (found.Count < MaxCandidates)
            Add(await TokenOverlapAsync(connection, input.NameCore, cancellationToken));

        return await LoadAsync(connection, found, cancellationToken);
    }

    /// <summary>The master row for a supplied CIN/LLPIN, for the resolver's short-circuit.</summary>
    public static async Task<MasterCandidate?> GetByIdentifierAsync(
        SqlConnection connection, string identifier, CancellationToken cancellationToken = default)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        var rows = await LoadAsync(connection, [identifier.Trim().ToUpperInvariant()], cancellationToken);
        return rows.FirstOrDefault();
    }

    private static async Task<List<string>> IdentifiersAsync(
        SqlConnection connection, string predicate, string value, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"""
            SELECT TOP (@Limit) Identifier
            FROM dbo.CompanyMasterRecords
            WHERE RecordType IN ('Company', 'Llp') AND {predicate}
            ORDER BY Identifier;
            """, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = MaxCandidates;
        command.Parameters.Add("@Value", SqlDbType.NVarChar, CompanyNameNormalizer.MaxLength + 1).Value = value;
        return await ReadStringsAsync(command, cancellationToken);
    }

    private static async Task<List<string>> TokenOverlapAsync(SqlConnection connection, string nameCore, CancellationToken cancellationToken)
    {
        var tokens = nameCore.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2).Select(t => t.Length > CompanyNameTokenIndex.TokenMaxLength ? t[..CompanyNameTokenIndex.TokenMaxLength] : t)
            .Distinct(StringComparer.Ordinal).ToList();
        if (tokens.Count == 0) return [];

        // Document frequency of each input word, to pick the rarest (most identifying) ones.
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var df = new SqlCommand(
            $"SELECT Token, COUNT_BIG(*) FROM dbo.CompanyNameTokens WHERE Token IN ({Parameters("@t", tokens.Count)}) GROUP BY Token;", connection))
        {
            AddParameters(df, "@t", tokens);
            await using var reader = await df.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                frequency[reader.GetString(0)] = (int)Math.Min(reader.GetInt64(1), int.MaxValue);
        }

        var keys = frequency.Where(kv => kv.Value <= MaxTokenFrequency)
            .OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(TokensUsed).Select(kv => kv.Key).ToList();
        if (keys.Count == 0) return [];

        await using var overlap = new SqlCommand($"""
            SELECT TOP (@Limit) t.Identifier
            FROM dbo.CompanyNameTokens t
            WHERE t.Token IN ({Parameters("@k", keys.Count)})
            GROUP BY t.Identifier
            ORDER BY COUNT(*) DESC, t.Identifier;
            """, connection);
        overlap.Parameters.Add("@Limit", SqlDbType.Int).Value = MaxCandidates;
        AddParameters(overlap, "@k", keys);
        return await ReadStringsAsync(overlap, cancellationToken);
    }

    private static async Task<IReadOnlyList<MasterCandidate>> LoadAsync(
        SqlConnection connection, IReadOnlyList<string> identifiers, CancellationToken cancellationToken)
    {
        if (identifiers.Count == 0) return [];
        await using var command = new SqlCommand(
            $"SELECT {CandidateColumns} FROM dbo.CompanyMasterRecords WHERE RecordType IN ('Company', 'Llp') AND Identifier IN ({Parameters("@i", identifiers.Count)});",
            connection);
        AddParameters(command, "@i", identifiers);

        var byId = new Dictionary<string, MasterCandidate>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? Str(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            var candidate = new MasterCandidate(
                Identifier: reader.GetString(0),
                RecordType: Enum.Parse<CompanyMasterRecordType>(reader.GetString(1)),
                Name: reader.GetString(2),
                NameNormalized: Str(3),
                NameCore: Str(4),
                EntityForm: Str(5) is { } form && Enum.TryParse<EntityForm>(form, out var parsed) ? parsed : null,
                Status: Str(6),
                State: Str(7),
                District: Str(8),
                PinCode: Str(9),
                RegistrationDate: reader.IsDBNull(10) ? null : DateOnly.FromDateTime(reader.GetDateTime(10)),
                Category: Str(11),
                Class: Str(12),
                ListingStatus: Str(13));
            byId[candidate.Identifier] = candidate;
        }
        // Keep retrieval order (strongest evidence first) — it is the tie-break of last resort for a reviewer.
        return identifiers.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private static string Parameters(string prefix, int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    private static void AddParameters(SqlCommand command, string prefix, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
            command.Parameters.Add($"{prefix}{i}", SqlDbType.NVarChar, CompanyNameNormalizer.MaxLength).Value = values[i];
    }

    private static async Task<List<string>> ReadStringsAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    private static string EscapeLike(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_' or '[') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
