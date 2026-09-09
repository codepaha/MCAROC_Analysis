namespace MCAROC_Analysis.Services.Chat;

/// <summary>Chunking constants — starting values, tuned against the real Coastal Projects corpus during
/// manual verification, not guessed-and-locked (same discipline Phase 2 applied to its OCR threshold).</summary>
public record ChatIndexingOptions(int ChunkMaxChars = 2000, int ChunkOverlapChars = 200, int ChunkMinChars = 50)
{
    public static readonly ChatIndexingOptions Default = new();
}

/// <summary>Retrieval constants. MaxCosineDistance is a starting default (SQL Server's cosine distance
/// range is [0,2], 0=identical) to be set from the retrieval benchmark's actual distance distribution, not
/// guessed. MinAcceptableResults drives the metadata-hint fail-open fallback — if hinted retrieval yields
/// fewer than this many chunks past the relevance threshold, an unfiltered RequestId-only search runs
/// instead, so a wrong or overly-specific hint can never silently starve an answer of evidence.</summary>
public record ChatRetrievalOptions(
    int TopK = 8, double MaxCosineDistance = 1.0, int MinAcceptableResults = 3, int ChatHistoryTurnLimit = 6)
{
    public static readonly ChatRetrievalOptions Default = new();
}
