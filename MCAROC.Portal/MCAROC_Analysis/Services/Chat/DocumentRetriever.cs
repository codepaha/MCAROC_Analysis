using System.Data;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

public record DocumentChunkMatch(DocumentChunk Chunk, double Distance);

/// <summary>Exposes exactly one public entry point — SearchRequestDocumentsAsync — never a raw
/// SearchAsync(queryVector) that would trust a caller to remember the RequestId filter. Makes cross-request
/// leakage structurally harder, not just procedurally avoided.
///
/// The actual vector search runs as raw ADO.NET, not LINQ-to-Entities, for a real, measured reason: EF
/// Core 10.0.11's SqlServer provider binds a SqlVector&lt;float&gt; LINQ parameter as plain DbType.Binary
/// rather than the native SqlDbType.Vector, which forces SQL Server into a per-row conversion path so slow
/// it timed out (30s+) against a real ~13,500-row corpus — confirmed by reproducing the identical query via
/// raw ADO.NET with a properly-typed vector parameter (SqlDbTypeExtensions.Vector), which ran in under
/// 200ms. Column names are hardcoded (never string-interpolated from caller input), and every value is a
/// parameter — this is not a SQL-injection shortcut, it's working around an EF Core parameter-mapping gap
/// for a type this new (SQL Server 2025's VECTOR + EF Core 10 shipped together).</summary>
public class DocumentRetriever(AppDbContext db, ChatRetrievalOptions options)
{
    public virtual async Task<List<DocumentChunkMatch>> SearchRequestDocumentsAsync(
        long requestId, long? batchId, float[] queryEmbedding, QuestionHints hints, CancellationToken ct)
    {
        var queryVector = new SqlVector<float>(queryEmbedding);

        var hinted = await SearchAsync(requestId, batchId, queryVector, hints, ct);
        var passingHinted = hinted.Where(m => m.Distance <= options.MaxCosineDistance).ToList();

        if (!hints.HasSoftHints || passingHinted.Count >= options.MinAcceptableResults)
            return passingHinted;

        // Fail open: the soft-hinted search didn't yield enough evidence — retry with only the RequestId
        // (and any hard SRN match) filter, so an overly-specific hint can never silently starve the answer
        // of evidence an unfiltered search would have found.
        var fallbackHints = hints with { Category = null, FormTypeKeyword = null, LenderNameKeyword = null };
        var fallback = await SearchAsync(requestId, batchId, queryVector, fallbackHints, ct);
        return fallback.Where(m => m.Distance <= options.MaxCosineDistance).ToList();
    }

    public Task<List<DocumentChunkMatch>> SearchRequestDocumentsAsync(
        long requestId, float[] queryEmbedding, QuestionHints hints, CancellationToken ct) =>
        SearchRequestDocumentsAsync(requestId, null, queryEmbedding, hints, ct);

    private async Task<List<DocumentChunkMatch>> SearchAsync(long requestId, long? batchId, SqlVector<float> queryVector, QuestionHints hints, CancellationToken ct)
    {
        var whereClauses = new List<string> { "RequestId = @requestId" };
        if (batchId.HasValue) whereClauses.Add("BatchId = @batchId");
        if (hints.SrnMatch is not null) whereClauses.Add("Srn = @srn");
        if (hints.Category is not null) whereClauses.Add("Category = @category");
        if (hints.FormTypeKeyword is not null) whereClauses.Add("FormType IS NOT NULL AND FormType LIKE @formType");
        if (hints.LenderNameKeyword is not null) whereClauses.Add("ChunkText LIKE @lender");

        // Rank on a narrow (ChunkId, Distance) subquery first, then join back for the wide columns only for
        // the winning rows — found live: sorting the FULL wide row (including ChunkText, nvarchar(max)) by
        // VECTOR_DISTANCE for every candidate row made SQL Server request a memory grant it couldn't
        // satisfy (confirmed via sys.dm_exec_requests: wait_type=RESOURCE_SEMAPHORE), stalling for 30s+
        // against ~13,500 rows even though the vector distance computation itself takes well under a
        // second. Sorting only ChunkId+Distance keeps the memory grant trivial regardless of ChunkText size.
        var sql = $"""
            SELECT c.ChunkId, c.RequestId, c.FilingDocumentId, c.FilingId, c.BatchId, c.Srn, c.Category, c.FormType,
                c.DocumentName, c.ChunkIndex, c.PageNumber, c.ChunkText, c.EmbeddingModel, c.EmbeddingDimensions, c.ChunkingVersion, c.CreatedDate,
                ranked.Distance
            FROM (
                SELECT TOP (@topK) ChunkId, VECTOR_DISTANCE('cosine', Embedding, @queryVector) AS Distance
                FROM DocumentChunks
                WHERE {string.Join(" AND ", whereClauses)}
                ORDER BY VECTOR_DISTANCE('cosine', Embedding, @queryVector)
            ) AS ranked
            JOIN DocumentChunks c ON c.ChunkId = ranked.ChunkId
            ORDER BY ranked.Distance
            """;

        // A dedicated connection, not db.Database.GetDbConnection() — empirically, reusing EF's
        // pooled/managed connection for this exact query made SQL Server pick a catastrophically slow plan
        // (30s+ timeout) even though the identical parameterized query on a fresh connection runs in
        // under a second. Root cause not fully chased down (suspected session-settings-dependent plan
        // caching interacting badly with the brand-new VECTOR_DISTANCE optimizer path) — a dedicated
        // connection for this specific hot path is a reasonable, defensible choice regardless.
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@topK", SqlDbType.Int) { Value = options.TopK });
            cmd.Parameters.Add(new SqlParameter("@requestId", SqlDbType.BigInt) { Value = requestId });
            if (batchId.HasValue) cmd.Parameters.Add(new SqlParameter("@batchId", SqlDbType.BigInt) { Value = batchId.Value });
            var vectorParam = cmd.Parameters.Add("@queryVector", Microsoft.Data.SqlDbTypeExtensions.Vector, EmbeddingService.Dimensions);
            vectorParam.Value = queryVector;
            if (hints.SrnMatch is not null) cmd.Parameters.Add(new SqlParameter("@srn", SqlDbType.NVarChar, 450) { Value = hints.SrnMatch });
            if (hints.Category is not null) cmd.Parameters.Add(new SqlParameter("@category", SqlDbType.NVarChar, 20) { Value = hints.Category.Value.ToString() });
            if (hints.FormTypeKeyword is not null) cmd.Parameters.Add(new SqlParameter("@formType", SqlDbType.NVarChar, 450) { Value = $"%{hints.FormTypeKeyword}%" });
            if (hints.LenderNameKeyword is not null) cmd.Parameters.Add(new SqlParameter("@lender", SqlDbType.NVarChar, -1) { Value = $"%{hints.LenderNameKeyword}%" });

            var results = new List<DocumentChunkMatch>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var chunk = new DocumentChunk
                {
                    ChunkId = reader.GetInt64(0),
                    RequestId = reader.GetInt64(1),
                    FilingDocumentId = reader.GetInt64(2),
                    FilingId = reader.GetInt64(3),
                    BatchId = reader.GetInt64(4),
                    Srn = reader.GetString(5),
                    Category = Enum.Parse<FilingCategory>(reader.GetString(6)),
                    FormType = reader.IsDBNull(7) ? null : reader.GetString(7),
                    DocumentName = reader.GetString(8),
                    ChunkIndex = reader.GetInt32(9),
                    PageNumber = reader.GetInt32(10),
                    ChunkText = reader.GetString(11),
                    EmbeddingModel = reader.GetString(12),
                    EmbeddingDimensions = reader.GetInt32(13),
                    ChunkingVersion = reader.GetString(14),
                    CreatedDate = reader.GetDateTime(15)
                };
                var distance = reader.GetDouble(16);
                results.Add(new DocumentChunkMatch(chunk, distance));
            }
            return results;
        }
    }
}
