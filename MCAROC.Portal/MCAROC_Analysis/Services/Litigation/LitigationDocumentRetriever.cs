using System.Data;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

public record LitigationOrderChunkMatch(LitigationOrderChunk Chunk, double Distance);

/// <summary>Litigation-specific counterpart to <c>Services.Chat.DocumentRetriever</c> for #244/LIT-04 — exposes
/// exactly one public entry point, <see cref="SearchRequestOrdersAsync"/>, always RequestId-scoped, never a raw
/// SearchAsync(queryVector) a caller could forget to scope. Deliberately simpler than DocumentRetriever: no
/// QuestionHints/soft-hint-then-fallback layer, since the #244 acceptance criteria don't call for
/// litigation-specific hint dimensions (CNR/court/order-type keyword extraction would be scope creep — nothing
/// in the issue asks for it).
///
/// Replicates DocumentRetriever's two hard-won, measured performance fixes exactly, since both are properties
/// of the underlying EF Core 10 / SQL Server 2025 VECTOR stack, not of DocumentChunk specifically: (1) raw
/// ADO.NET with a properly-typed vector parameter (SqlDbTypeExtensions.Vector) — EF Core 10.0.11's SqlServer
/// provider binds a SqlVector&lt;float&gt; LINQ parameter as plain DbType.Binary, forcing a per-row conversion
/// path that timed out (30s+) against a real corpus; (2) rank on a narrow (ChunkId, Distance) subquery first,
/// then join back for wide columns — sorting the full wide row (including ChunkText, nvarchar(max)) by
/// VECTOR_DISTANCE for every candidate row made SQL Server request a memory grant it couldn't satisfy
/// (RESOURCE_SEMAPHORE waits). Also reuses DocumentRetriever's dedicated-connection choice (never
/// db.Database.GetDbConnection()) — reusing EF's pooled connection produced a catastrophically slow plan for
/// this same query shape.</summary>
public class LitigationDocumentRetriever(AppDbContext db, ChatRetrievalOptions options)
{
    public virtual async Task<List<LitigationOrderChunkMatch>> SearchRequestOrdersAsync(
        long requestId, float[] queryEmbedding, CancellationToken ct)
    {
        var queryVector = new SqlVector<float>(queryEmbedding);

        // Rank on a narrow (LitigationOrderChunkId, Distance) subquery first, then join back for the wide
        // columns only for the winning rows — see this type's own remarks for why.
        var sql = """
            SELECT c.LitigationOrderChunkId, c.RequestId, c.LitigationOrderDocumentId, c.LitigationCaseOrderId, c.LitigationCaseId,
                c.CaseNumber, c.Cnr, c.Court, c.OrderType, c.OrderDate, c.ChunkIndex, c.PageNumber, c.ChunkText,
                c.EmbeddingModel, c.EmbeddingDimensions, c.ChunkingVersion, c.CreatedDate,
                ranked.Distance
            FROM (
                SELECT TOP (@topK) LitigationOrderChunkId, VECTOR_DISTANCE('cosine', Embedding, @queryVector) AS Distance
                FROM LitigationOrderChunks
                WHERE RequestId = @requestId
                ORDER BY VECTOR_DISTANCE('cosine', Embedding, @queryVector)
            ) AS ranked
            JOIN LitigationOrderChunks c ON c.LitigationOrderChunkId = ranked.LitigationOrderChunkId
            ORDER BY ranked.Distance
            """;

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@topK", SqlDbType.Int) { Value = options.TopK });
            cmd.Parameters.Add(new SqlParameter("@requestId", SqlDbType.BigInt) { Value = requestId });
            var vectorParam = cmd.Parameters.Add("@queryVector", Microsoft.Data.SqlDbTypeExtensions.Vector, EmbeddingService.Dimensions);
            vectorParam.Value = queryVector;

            var results = new List<LitigationOrderChunkMatch>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var chunk = new LitigationOrderChunk
                {
                    LitigationOrderChunkId = reader.GetInt64(0),
                    RequestId = reader.GetInt64(1),
                    LitigationOrderDocumentId = reader.GetInt64(2),
                    LitigationCaseOrderId = reader.GetInt64(3),
                    LitigationCaseId = reader.GetInt64(4),
                    CaseNumber = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Cnr = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Court = reader.IsDBNull(7) ? null : reader.GetString(7),
                    OrderType = reader.IsDBNull(8) ? null : reader.GetString(8),
                    OrderDate = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ChunkIndex = reader.GetInt32(10),
                    PageNumber = reader.GetInt32(11),
                    ChunkText = reader.GetString(12),
                    EmbeddingModel = reader.GetString(13),
                    EmbeddingDimensions = reader.GetInt32(14),
                    ChunkingVersion = reader.GetString(15),
                    CreatedDate = reader.GetDateTime(16)
                };
                var distance = reader.GetDouble(17);
                results.Add(new LitigationOrderChunkMatch(chunk, distance));
            }
            return results.Where(m => m.Distance <= options.MaxCosineDistance).ToList();
        }
    }
}
