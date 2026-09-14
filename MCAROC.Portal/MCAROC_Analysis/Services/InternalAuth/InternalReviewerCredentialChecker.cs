using System.Security.Cryptography;

namespace MCAROC_Analysis.Services.InternalAuth;

/// <summary>PBKDF2 password hashing for the #164 internal calculation-audit reviewer credential — config-
/// based (InternalAuth:ReviewerUsername/ReviewerPasswordHash/ReviewerDisplayName), not a DB table: with
/// exactly one reviewer role today and no self-service reset in scope, a table buys nothing and adds a
/// place a hash could leak into source control via seed data. Trivially swappable to a real user table
/// later — nothing outside this class and InternalAuthController knows the credential is config-based.
/// Stored hash format: "{iterations}.{saltBase64}.{hashBase64}".</summary>
public static class InternalReviewerCredentialChecker
{
    private const int DefaultIterations = 210_000;
    private const int HashLength = 32;

    public static bool Verify(string password, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;

        var parts = storedHash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;

        byte[] salt, expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedHash = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        // Constant-time comparison — a hash check is exactly the kind of comparison a timing side-channel
        // could otherwise leak information through.
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>Produces a new stored-hash string for InternalAuth:ReviewerPasswordHash. Not called at
    /// runtime by the app itself — this is the tool an operator uses to generate the config value.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashLength);
        return $"{DefaultIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
}
