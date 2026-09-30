using System.Data;
using Microsoft.Data.SqlClient;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>The lexical half of #194's hybrid retrieval, shared by <see cref="DocumentRetriever"/> and
/// <c>LitigationDocumentRetriever</c>: SQL Server Full-Text Search (<c>CONTAINSTABLE</c>) over <c>ChunkText</c>,
/// fused with the existing vector ranking by Reciprocal Rank Fusion. RRF uses only each ranker's rank position,
/// never its raw score — cosine distance and FTS <c>RANK</c> are on unrelated scales, so any score-blending
/// formula would be arbitrary.
///
/// Fail-open by construction: the lexical search only ever runs when <see cref="QuestionHintExtractor"/> found
/// an exact-match term worth boosting (never the raw question — a whole sentence as a full-text query is mostly
/// noise), and it can only add candidates to the fused list, never filter the semantic ones out. Where Full-Text
/// Search is not installed (e.g. plain SQL Server Express) the migration skips creating the index and
/// <see cref="IsFullTextIndexedAsync"/> reports false, so retrieval degrades to today's pure vector search.
///
/// Population timing (the gotcha #194 flags): the indexes use <c>CHANGE_TRACKING AUTO</c>, so a freshly chunked
/// row becomes lexically searchable a moment after its insert, not at the instant of it. That window is
/// accepted deliberately — the vector path sees every new row immediately, and a lexical miss only means the
/// fused list equals the pure semantic one for that brief window, never a missing or wrong answer.</summary>
public static class HybridSearch
{
    /// <summary>The standard RRF constant — dampens how much the very top ranks dominate.</summary>
    public const int RrfK = 60;

    /// <summary>Fuses two ranked lists (best first) by <c>score = Σ 1/(k + rank)</c> and returns the top
    /// <paramref name="take"/> keys. Ties keep the semantic list's order first, then the lexical list's, so a
    /// query with no lexical matches returns exactly the semantic ranking.</summary>
    public static List<TKey> ReciprocalRankFusion<TKey>(
        IReadOnlyList<TKey> semanticRanked, IReadOnlyList<TKey> lexicalRanked, int take, int k = RrfK)
        where TKey : notnull
    {
        var scores = new Dictionary<TKey, (double Score, int FirstSeen)>();
        var order = 0;
        void Accumulate(IReadOnlyList<TKey> ranked)
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                var contribution = 1.0 / (k + i + 1);
                scores[ranked[i]] = scores.TryGetValue(ranked[i], out var existing)
                    ? (existing.Score + contribution, existing.FirstSeen)
                    : (contribution, order++);
            }
        }
        Accumulate(semanticRanked);
        Accumulate(lexicalRanked);

        return scores
            .OrderByDescending(kv => kv.Value.Score)
            .ThenBy(kv => kv.Value.FirstSeen)
            .Take(take)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>Builds a <c>CONTAINS</c> search condition that ORs each term as an exact phrase. Every term is
    /// wrapped in double quotes (embedded quotes and the prefix wildcard <c>*</c> stripped), so no user text is
    /// ever interpreted as full-text operators; the result is still passed as a parameter, never interpolated
    /// into SQL.</summary>
    public static string? BuildContainsQuery(IReadOnlyList<string>? terms)
    {
        var phrases = (terms ?? [])
            .Select(t => string.Join(' ', t.Replace('"', ' ').Replace('*', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => $"\"{t}\"")
            .ToList();
        return phrases.Count == 0 ? null : string.Join(" OR ", phrases);
    }

    /// <summary>True only when Full-Text Search is installed and <paramref name="tableName"/> has a full-text
    /// index — checked per search (a trivial catalog lookup) rather than cached, so an index created or
    /// dropped after startup is picked up without a restart.</summary>
    public static async Task<bool> IsFullTextIndexedAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CASE WHEN FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(@table)) THEN 1 ELSE 0 END
            """;
        cmd.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 256) { Value = tableName });
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }
}
