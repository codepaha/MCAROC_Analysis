using MCAROC_Analysis.Services.AutoFetch;

namespace MCAROC_Analysis.Tests;

public class CompanyRefreshPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Locked_company_never_starts_or_joins_a_refresh()
    {
        var decision = CompanyRefreshPolicy.Decide(new CompanyRefreshState(false, Now.AddDays(-2), true), Now);

        Assert.Equal(CompanyRefreshDecision.Locked, decision);
    }

    [Theory]
    [InlineData(-23)]
    [InlineData(-24)]
    [InlineData(1)]
    public void Snapshot_at_or_inside_the_24_hour_window_is_reused(int ageHours)
    {
        var decision = CompanyRefreshPolicy.Decide(new CompanyRefreshState(true, Now.AddHours(ageHours), false), Now);

        Assert.Equal(CompanyRefreshDecision.ReuseFreshSnapshot, decision);
    }

    [Fact]
    public void Existing_refresh_is_joined_even_when_the_snapshot_is_stale()
    {
        var decision = CompanyRefreshPolicy.Decide(new CompanyRefreshState(true, Now.AddHours(-25), true), Now);

        Assert.Equal(CompanyRefreshDecision.JoinActiveRefresh, decision);
    }

    [Theory]
    [InlineData(-25)]
    [InlineData(-48)]
    public void Stale_snapshot_starts_a_refresh_when_none_is_active(int ageHours)
    {
        var decision = CompanyRefreshPolicy.Decide(new CompanyRefreshState(true, Now.AddHours(ageHours), false), Now);

        Assert.Equal(CompanyRefreshDecision.StartRefresh, decision);
    }

    [Fact]
    public void Company_without_a_successful_snapshot_starts_a_refresh()
    {
        var decision = CompanyRefreshPolicy.Decide(new CompanyRefreshState(true, null, false), Now);

        Assert.Equal(CompanyRefreshDecision.StartRefresh, decision);
    }
}
