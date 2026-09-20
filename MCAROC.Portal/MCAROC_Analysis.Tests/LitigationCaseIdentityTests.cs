using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationCaseIdentityTests
{
    [Theory]
    [InlineData("TNKP070001332020", "TNKP070001332020")]
    [InlineData("tnkp-0700 0133-2020", "TNKP070001332020")]
    [InlineData("-", null)]
    [InlineData("NA", null)]
    [InlineData("AAAAAAAAAAAAAAAA", null)]
    [InlineData("TNKP07000133202", null)]
    public void NormaliseCnr_accepts_only_a_valid_non_placeholder_identifier(string source, string? expected) =>
        Assert.Equal(expected, LitigationCaseIdentity.NormaliseCnr(source));

    [Fact]
    public void CanAutoDedupe_requires_a_shared_valid_Cnr_and_compatible_type()
    {
        var left = LitigationCaseIdentity.Normalise(new("TNKP070001332020", "OS 22/2020", 2020, "OS", "101"));
        var right = LitigationCaseIdentity.Normalise(new("tnkp-0700 0133-2020", "22/2020", 2020, "OS", "202"));

        Assert.True(LitigationCaseIdentity.CanAutoDedupe(left, right));
        Assert.Equal("101", left.CspId);
        Assert.Equal("202", right.CspId);
    }

    [Fact]
    public void CanAutoDedupe_does_not_treat_shared_Csp_as_case_identity()
    {
        var left = LitigationCaseIdentity.Normalise(new(null, "OS 22/2020", 2020, "OS", "101"));
        var right = LitigationCaseIdentity.Normalise(new(null, "OS 99/2020", 2020, "OS", "101"));

        Assert.False(LitigationCaseIdentity.CanAutoDedupe(left, right));
    }

    [Fact]
    public void CanAutoDedupe_rejects_different_proceeding_types_even_when_Cnr_matches()
    {
        var left = LitigationCaseIdentity.Normalise(new("TNKP070001332020", "CC 22/2020", 2020, "CC"));
        var right = LitigationCaseIdentity.Normalise(new("TNKP070001332020", "A 22/2020", 2020, "A"));

        Assert.False(LitigationCaseIdentity.CanAutoDedupe(left, right));
    }

    [Fact]
    public void Normalise_parses_case_number_only_when_supplied_year_agrees()
    {
        var accepted = LitigationCaseIdentity.Normalise(new(null, "OS 22/2020", 2020, "OS"));
        var rejected = LitigationCaseIdentity.Normalise(new(null, "OS 22/2020", 2021, "OS"));

        Assert.Equal(new LitigationCaseNumber("OS", "22", 2020), accepted.CaseNumber);
        Assert.Null(rejected.CaseNumber);
    }
}
