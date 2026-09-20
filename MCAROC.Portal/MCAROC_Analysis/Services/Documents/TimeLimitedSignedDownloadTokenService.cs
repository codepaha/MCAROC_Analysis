using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MCAROC_Analysis.Services.Documents;

/// <summary>
/// Implements signed-download token minting and validation using ASP.NET Core ITimeLimitedDataProtector.
/// Tokens are bound to (RequestId, DocumentId) with a short 10-minute lifetime.
/// </summary>
public class TimeLimitedSignedDownloadTokenService(IDataProtectionProvider dataProtectionProvider) : ISignedDownloadTokenService
{
    private const string Purpose = "UploadedDocuments.DownloadAuthorization.v1";
    private readonly ITimeLimitedDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    public string GenerateToken(long requestId, long docId, TimeSpan? lifetime = null)
    {
        var payload = $"{requestId}:{docId}";
        return _protector.Protect(payload, lifetime ?? DefaultLifetime);
    }

    public bool ValidateToken(long requestId, long docId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var payload = _protector.Unprotect(token);
            var parts = payload.Split(':');
            if (parts.Length != 2) return false;
            if (!long.TryParse(parts[0], out var tokenReqId) || tokenReqId != requestId) return false;
            if (!long.TryParse(parts[1], out var tokenDocId) || tokenDocId != docId) return false;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

