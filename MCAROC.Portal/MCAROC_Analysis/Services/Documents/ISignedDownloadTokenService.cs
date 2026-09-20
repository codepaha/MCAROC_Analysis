namespace MCAROC_Analysis.Services.Documents;

/// <summary>
/// Cryptographically mints and verifies short-lived bearer tokens for uploaded document downloads.
/// Tokens are strictly scoped to (RequestId, DocumentId) and expire automatically.
/// </summary>
public interface ISignedDownloadTokenService
{
    string GenerateToken(long requestId, long docId, TimeSpan? lifetime = null);
    bool ValidateToken(long requestId, long docId, string? token);
}

