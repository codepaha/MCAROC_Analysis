using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.AspNetCore.Identity;

namespace MCAROC_Analysis.Tests;

public class AnalystPasswordHasherTests
{
    private readonly AnalystPasswordHasher _hasher = new();

    [Fact]
    public void Hash_UsesFrameworkFormatAndDoesNotReturnThePassword()
    {
        const string password = "Synthetic-Demo-Password-Only";

        var hash = _hasher.Hash(password);

        Assert.NotEqual(password, hash);
        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify(password, hash));
    }

    [Fact]
    public void Hash_UsesAUniqueSaltForEachCredential()
    {
        const string password = "Synthetic-Demo-Password-Only";

        Assert.NotEqual(_hasher.Hash(password), _hasher.Hash(password));
    }

    [Fact]
    public void Verify_RejectsWrongEmptyAndMalformedCredentials()
    {
        var hash = _hasher.Hash("Synthetic-Demo-Password-Only");

        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("not-the-password", hash));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("", hash));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("Synthetic-Demo-Password-Only", "not-a-password-hash"));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("Synthetic-Demo-Password-Only", null));
    }
}
