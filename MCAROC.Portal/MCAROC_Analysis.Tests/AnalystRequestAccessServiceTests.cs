using System.Security.Claims;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public sealed class AnalystRequestAccessServiceTests
{
    private static AnalystRequestAccessService CreateService() =>
        new(new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options));

    [Fact]
    public void TryGetAnalystId_AcceptsOnlyPositiveOpaqueDatabaseIds()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AnalystAccessConstants.AnalystIdClaimType, "42")],
            AnalystAccessConstants.AuthenticationScheme));

        var found = CreateService().TryGetAnalystId(principal, out var analystId);

        Assert.True(found);
        Assert.Equal(42, analystId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("analyst@example.test")]
    public void TryGetAnalystId_RejectsMissingMalformedAndNonPositiveClaims(string? value)
    {
        Claim[] claims = value is null ? [] : [new Claim(AnalystAccessConstants.AnalystIdClaimType, value)];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AnalystAccessConstants.AuthenticationScheme));

        var found = CreateService().TryGetAnalystId(principal, out _);

        Assert.False(found);
    }
}
