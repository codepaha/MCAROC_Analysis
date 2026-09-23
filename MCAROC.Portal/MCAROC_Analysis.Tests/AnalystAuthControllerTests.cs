using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using MCAROC_Analysis.Services.Audit;

namespace MCAROC_Analysis.Tests;

public sealed class AnalystAuthControllerTests
{
    [Theory]
    [InlineData("  analyst.one  ", "ANALYST.ONE")]
    [InlineData("ANALYST.ONE", "ANALYST.ONE")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void NormalizeLoginName_UsesTheCanonicalDatabaseLookupForm(string? input, string expected)
    {
        Assert.Equal(expected, AnalystLoginNameNormalizer.Normalize(input));
    }

    [Fact]
    public void AuditRegistry_LeavesAnalystAuthToItsCredentialAwareAuditEvents()
    {
        var login = AuditRouteRegistry.Resolve("AnalystAuth", "Login");
        var logout = AuditRouteRegistry.Resolve("AnalystAuth", "Logout");

        Assert.Equal((AuditActionType.AnalystLoginAttempted, AuditRulePolicy.Never), login);
        Assert.Equal((AuditActionType.AnalystLoggedOut, AuditRulePolicy.Never), logout);
    }
}
