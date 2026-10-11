using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using Xunit;

namespace MCAROC_Analysis.Tests;

public sealed class ConsumerCommissionRegistryTests
{
    [Fact]
    public void Registry_loads_all_731_commissions()
    {
        var all = ConsumerCommissionRegistry.GetAll();
        Assert.Equal(731, all.Count);

        var ncdrc = all.Count(c => c.Tier == ConsumerCommissionTier.National);
        Assert.Equal(1, ncdrc);

        var scdrc = all.Count(c => c.Tier == ConsumerCommissionTier.State);
        Assert.Equal(54, scdrc);

        var dcdrc = all.Count(c => c.Tier == ConsumerCommissionTier.District);
        Assert.Equal(676, dcdrc);
    }

    [Fact]
    public void GetById_resolves_ncdrc_properly()
    {
        var ncdrc = ConsumerCommissionRegistry.GetById(11000000);
        Assert.NotNull(ncdrc);
        Assert.Equal(1, ncdrc.CommissionTypeId);
        Assert.Equal(ConsumerCommissionTier.National, ncdrc.Tier);
        Assert.Equal("NC", ncdrc.Prefix);
        Assert.Equal("NCDRC (National)", ncdrc.Label);
    }

    [Fact]
    public void GetById_resolves_scdrc_and_benches_with_parent_states()
    {
        // Maharashtra SCDRC
        var maha = ConsumerCommissionRegistry.GetById(11270000);
        Assert.NotNull(maha);
        Assert.Equal(ConsumerCommissionTier.State, maha.Tier);
        Assert.False(maha.IsBench);
        Assert.Equal("MAHARASHTRA", maha.CommissionName);

        // Rajasthan Jodhpur Bench (22080000)
        var jodhpur = ConsumerCommissionRegistry.GetById(22080000);
        Assert.NotNull(jodhpur);
        Assert.Equal(ConsumerCommissionTier.State, jodhpur.Tier);
        Assert.True(jodhpur.IsBench);
        Assert.Equal("RAJASTHAN", jodhpur.ParentState);
        Assert.Equal("BENCH JODHPUR", jodhpur.CommissionName);
        Assert.Equal("SC/CB1/8", jodhpur.Prefix);
    }

    [Fact]
    public void GetById_resolves_dcdrc_district_commissions()
    {
        // Thane DCDRC (12270497)
        var thane = ConsumerCommissionRegistry.GetById(12270497);
        Assert.NotNull(thane);
        Assert.Equal(ConsumerCommissionTier.District, thane.Tier);
        Assert.Equal(3, thane.CommissionTypeId);
        Assert.Equal("Thane", thane.CommissionName);
        Assert.Equal("MAHARASHTRA", thane.StateName);
        Assert.Equal("Thane", thane.DistrictName);
        Assert.Equal("DC/497", thane.Prefix);
    }

    [Theory]
    [InlineData("NC", 11000000, ConsumerCommissionTier.National)]
    [InlineData("SC/35", 11350000, ConsumerCommissionTier.State)]
    [InlineData("DC/497", 12270497, ConsumerCommissionTier.District)]
    public void FindByPrefix_resolves_known_court_prefixes(string prefix, int expectedId, ConsumerCommissionTier expectedTier)
    {
        var comm = ConsumerCommissionRegistry.FindByPrefix(prefix);
        Assert.NotNull(comm);
        Assert.Equal(expectedId, comm.CommissionId);
        Assert.Equal(expectedTier, comm.Tier);
    }

    [Fact]
    public void FindByCourtName_matches_court_or_district_name()
    {
        var thane = ConsumerCommissionRegistry.FindByCourtName("Thane");
        Assert.NotNull(thane);
        Assert.Equal(12270497, thane.CommissionId);

        var ncdrc = ConsumerCommissionRegistry.FindByCourtName("National Consumer Disputes Redressal Commission");
        Assert.NotNull(ncdrc);
        Assert.Equal(11000000, ncdrc.CommissionId);
    }

    [Fact]
    public void Find_resolves_hierarchical_commission_selection()
    {
        var ncdrc = ConsumerCommissionRegistry.Find("NCDRC");
        Assert.NotNull(ncdrc);
        Assert.Equal(11000000, ncdrc.CommissionId);

        var scdrcMaha = ConsumerCommissionRegistry.Find("SCDRC", "MAHARASHTRA");
        Assert.NotNull(scdrcMaha);
        Assert.Equal(11270000, scdrcMaha.CommissionId);

        var dcdrcThane = ConsumerCommissionRegistry.Find("DCDRC", "MAHARASHTRA", "Thane");
        Assert.NotNull(dcdrcThane);
        Assert.Equal(12270497, dcdrcThane.CommissionId);
    }

    [Fact]
    public void StateCode_extracts_two_digit_code_correctly()
    {
        Assert.Equal("08", ConsumerCommissionRegistry.StateCode(11080000));
        Assert.Equal("08", ConsumerCommissionRegistry.StateCode(22080000));
        Assert.Equal("27", ConsumerCommissionRegistry.StateCode(11270000));
        Assert.Equal("27", ConsumerCommissionRegistry.StateCode(12270497));
    }

    [Fact]
    public void ToFullCaseNumber_prefixes_short_number_and_is_idempotent()
    {
        var full = ConsumerCommissionRegistry.ToFullCaseNumber("CC/599/2017", 12270497);
        Assert.Equal("DC/497/CC/599/2017", full);

        var idempotent = ConsumerCommissionRegistry.ToFullCaseNumber("DC/497/CC/599/2017", 12270497);
        Assert.Equal("DC/497/CC/599/2017", idempotent);

        var national = ConsumerCommissionRegistry.ToFullCaseNumber("RP/836/2005", 11000000);
        Assert.Equal("NC/RP/836/2005", national);
    }
}

