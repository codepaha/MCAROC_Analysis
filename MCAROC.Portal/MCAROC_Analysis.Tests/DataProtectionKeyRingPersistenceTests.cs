using System.Security.Cryptography;
using MCAROC_Analysis.Services.Documents;
using Microsoft.AspNetCore.DataProtection;

namespace MCAROC_Analysis.Tests;

public class DataProtectionKeyRingPersistenceTests : IDisposable
{
    private readonly string _tempKeysFolder;

    public DataProtectionKeyRingPersistenceTests()
    {
        _tempKeysFolder = Path.Combine(Path.GetTempPath(), "mcaroc_keys_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempKeysFolder);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempKeysFolder))
            {
                Directory.Delete(_tempKeysFolder, recursive: true);
            }
        }
        catch { }
    }

    private IDataProtectionProvider CreateProvider()
    {
        return DataProtectionProvider.Create(new DirectoryInfo(_tempKeysFolder), builder =>
        {
            builder.SetApplicationName("MCAROC_Analysis");
        });
    }

    [Fact]
    public void Token_SurvivesDataProtectionProviderReinitialization()
    {
        // Provider 1 (simulating instance 1 or pre-restart)
        var provider1 = CreateProvider();
        var service1 = new TimeLimitedSignedDownloadTokenService(provider1);

        var token = service1.GenerateToken(requestId: 42, docId: 101, lifetime: TimeSpan.FromMinutes(10));
        Assert.NotNull(token);

        // Provider 2 (simulating instance 2 or post-restart with same key-ring directory)
        var provider2 = CreateProvider();
        var service2 = new TimeLimitedSignedDownloadTokenService(provider2);

        var isValid = service2.ValidateToken(requestId: 42, docId: 101, token);
        Assert.True(isValid, "Token minted by provider 1 must be valid under provider 2 using the same key repository.");
    }

    [Fact]
    public void ExpiredToken_FailsValidation()
    {
        var provider = CreateProvider();
        var service = new TimeLimitedSignedDownloadTokenService(provider);

        // Negative lifetime -> expired immediately
        var token = service.GenerateToken(requestId: 42, docId: 101, lifetime: TimeSpan.FromSeconds(-5));
        var isValid = service.ValidateToken(requestId: 42, docId: 101, token);

        Assert.False(isValid, "Expired token must not be accepted.");
    }

    [Fact]
    public void TamperedToken_FailsValidation()
    {
        var provider = CreateProvider();
        var service = new TimeLimitedSignedDownloadTokenService(provider);

        var token = service.GenerateToken(requestId: 42, docId: 101);
        var tampered = token.Length > 10 ? token[..^4] + "AAAA" : "invalid-token";

        var isValid = service.ValidateToken(requestId: 42, docId: 101, tampered);
        Assert.False(isValid, "Tampered token must not be accepted.");
    }

    [Fact]
    public void MismatchedRouteParameters_FailsValidation()
    {
        var provider = CreateProvider();
        var service = new TimeLimitedSignedDownloadTokenService(provider);

        var token = service.GenerateToken(requestId: 42, docId: 101);

        // Wrong requestId
        Assert.False(service.ValidateToken(requestId: 99, docId: 101, token));

        // Wrong docId
        Assert.False(service.ValidateToken(requestId: 42, docId: 999, token));
    }
}
