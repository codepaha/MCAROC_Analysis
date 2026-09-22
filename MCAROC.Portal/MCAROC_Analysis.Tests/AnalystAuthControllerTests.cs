using MCAROC_Analysis.Controllers;

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
        Assert.Equal(expected, AnalystAuthController.NormalizeLoginName(input));
    }
}
