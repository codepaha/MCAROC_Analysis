using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace MCAROC_Analysis.Services.ApplicationAuth;

public sealed class ApplicationCredentialChecker(IConfiguration configuration)
{
    public string Username => configuration["ApplicationAuth:Username"] ?? "usermcaroc@ct.com";
    private string? Hash => configuration["ApplicationAuth:PasswordHash"];
    public string Version => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Username + "\n" + Hash)));

    public bool Verify(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Hash) || string.IsNullOrEmpty(password)
            || !string.Equals(username?.Trim(), Username, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return new PasswordHasher<string>().VerifyHashedPassword(Username, Hash, password)
                != PasswordVerificationResult.Failed;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return false;
        }
    }
}
