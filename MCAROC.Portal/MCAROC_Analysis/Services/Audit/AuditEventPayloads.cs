namespace MCAROC_Analysis.Services.Audit;

/// <summary>
/// Strictly typed, versioned DTOs for audit event payloads.
/// Contains allow-listed fields only. PII (such as company names or reviewer names) is deliberately omitted.
/// </summary>

public record RequestCreatedPayload(string RequestNumber, string EntityType);

public record ArchiveUploadedPayload(long BatchId, long FileSizeBytes);

public record ChunkingCompletedPayload(
    long FilingDocumentId,
    int ChunksWritten,
    string EmbeddingModel,
    string ChunkingVersion,
    string CorrelationId);

public record ChunkingFailedPayload(
    long FilingDocumentId,
    string ErrorCategory,
    string SanitizedError,
    int RetryCount,
    bool IsTerminal,
    string CorrelationId);

public record WorkerOrphanRecoveredPayload(long BatchId, long? RequestId, int DocumentsReset, string CorrelationId);

public record DiscrepancyDecidedPayload(long DiscrepancyId, string DecisionAction);

public record HttpMutationPayload(string Controller, string Action, int HttpStatusCode);

/// <summary>
/// Valid, compact JSON fallback envelope for payloads that exceed the 2,000-character DB boundary.
/// Prevents saving broken sliced JSON strings.
/// </summary>
public record TruncatedPayloadEnvelope(bool Truncated, string PayloadType);
