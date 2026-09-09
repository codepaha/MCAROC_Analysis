using Google.Apis.Auth.OAuth2;
using Google.GenAI.Types;
using GenAiClient = Google.GenAI.Client;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Wraps Vertex AI's gemini-embedding-001 (same Google.GenAI client construction pattern as
/// VertexAiExtractionService/AiCrossSectionAnalysisService). Chunks are embedded with TaskType=
/// RETRIEVAL_DOCUMENT, questions with RETRIEVAL_QUERY — the model's own documented convention for
/// asymmetric retrieval. Always L2-normalizes the returned vector: gemini-embedding-001 requires manual
/// normalization at non-default output dimensionality, and normalizing unconditionally here removes any
/// doubt about whether it's needed rather than trusting an assumption about when.</summary>
public class EmbeddingService
{
    public const string ModelId = "gemini-embedding-001";
    public const int Dimensions = 768;
    private const int MaxBatchSize = 32;

    private readonly GenAiClient _client;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(string projectId, string location, string credentialsPath, ILogger<EmbeddingService> logger)
    {
        _logger = logger;
        var credential = GoogleCredential.FromFile(credentialsPath).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        _client = new GenAiClient(vertexAI: true, project: projectId, location: location, credential: credential);
    }

    public async Task<List<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var results = new List<float[]>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += MaxBatchSize)
        {
            var batch = texts.Skip(offset).Take(MaxBatchSize)
                .Select(t => new Content { Parts = [Part.FromText(t)] })
                .ToList();
            var config = new EmbedContentConfig { TaskType = "RETRIEVAL_DOCUMENT", OutputDimensionality = Dimensions };
            var response = await _client.Models.EmbedContentAsync(ModelId, batch, config, ct);
            var batchEmbeddings = response.Embeddings ?? [];
            // The caller (DocumentChunkingOrchestrator) pairs results[i] with chunk[i] positionally, so the
            // batch API must return exactly one vector per input, in input order. A short/null response
            // would otherwise silently truncate or misalign a document's index — fail loudly instead.
            if (batchEmbeddings.Count != batch.Count)
                throw new InvalidOperationException(
                    $"Embedding batch returned {batchEmbeddings.Count} vectors for {batch.Count} inputs.");
            foreach (var embedding in batchEmbeddings)
                results.Add(Normalize(embedding.Values ?? []));
        }
        return results;
    }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct)
    {
        var config = new EmbedContentConfig { TaskType = "RETRIEVAL_QUERY", OutputDimensionality = Dimensions };
        var response = await _client.Models.EmbedContentAsync(ModelId, text, config, ct);
        var values = response.Embeddings?.FirstOrDefault()?.Values
            ?? throw new InvalidOperationException("Embedding response contained no values.");
        return Normalize(values);
    }

    internal static float[] Normalize(IReadOnlyList<double> values)
    {
        var vector = values.Select(v => (float)v).ToArray();
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude <= 0f) return vector;
        for (var i = 0; i < vector.Length; i++)
            vector[i] /= magnitude;
        return vector;
    }
}
