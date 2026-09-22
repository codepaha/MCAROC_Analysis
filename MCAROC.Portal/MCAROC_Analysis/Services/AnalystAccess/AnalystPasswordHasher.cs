using Microsoft.AspNetCore.Identity;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>
/// Portal-managed credential hashing for the database-backed analyst identity selected for demo phase.
/// Uses ASP.NET Core Identity's versioned password-hash format rather than a home-grown format. The
/// eventual credential store contains only this output; it never stores or returns a raw password.
/// </summary>
public interface IAnalystPasswordHasher
{
    string Hash(string password);
    PasswordVerificationResult Verify(string password, string? passwordHash);
}

public sealed class AnalystPasswordHasher : IAnalystPasswordHasher
{
    // The subject is deliberately opaque: no PII is folded into the password hash, which lets a display-name
    // correction remain independent of credentials and avoids retaining identity data in hash inputs.
    private static readonly object Subject = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return _hasher.HashPassword(Subject, password);
    }

    public PasswordVerificationResult Verify(string password, string? passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash))
            return PasswordVerificationResult.Failed;

        // A malformed persisted value is indistinguishable from a wrong password to the caller. Identity's
        // parser can throw FormatException for a non-Base64 payload, which must not turn an attempted sign-in
        // into a 500 or leak that the account record is corrupted.
        try
        {
            return _hasher.VerifyHashedPassword(Subject, passwordHash, password);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }
    }
}
