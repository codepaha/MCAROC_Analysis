using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #294: known real variants rank correctly; exact-name twins are always ambiguous unless a hint
/// or the sole-active policy separates them; nothing is auto-selected unless every condition holds; auto-select
/// is off by default.</summary>
public class CompanyNameResolverTests
{
    private static readonly ResolverOptions Disabled = new();
    private static readonly ResolverOptions Enabled = new() { AutoSelectEnabled = true };

    private static MasterCandidate Row(string id, string name, string? status = "Active", string? state = null,
        CompanyMasterRecordType type = CompanyMasterRecordType.Company, DateOnly? registered = null, bool toolOnly = false)
    {
        var n = CompanyNameNormalizer.Normalize(name);
        return new MasterCandidate(id, type, name, n.NameNormalized, n.NameCore, n.EntityForm, status, state,
            RegistrationDate: registered, IsToolOnly: toolOnly);
    }

    private static ResolutionDecision Resolve(string input, ResolverOptions options, ResolutionHints? hints = null, params MasterCandidate[] rows) =>
        CompanyNameResolver.Decide(input, CompanyNameResolver.Rank(input, hints ?? ResolutionHints.None, rows), options);

    private static readonly MasterCandidate Sharma = Row("U10000MH2010PTC000001", "SHARMA AND SONS TRADING PRIVATE LIMITED");
    private static readonly MasterCandidate Kanpur = Row("U20000UP2015PTC000002", "KANPUR FLOWERCYCLING PRIVATE LIMITED");
    private static readonly MasterCandidate Unrelated = Row("U30000DL2012PTC000003", "DELHI STEEL WORKS PRIVATE LIMITED");

    // ── Real variants rank the right company first ──

    [Theory]
    [InlineData("Sharma & Sons Trading Pvt. Ltd.")]      // Pvt/Private, &/AND, punctuation → exact normalized
    [InlineData("Trading Sharma and Sons Pvt Ltd")]      // word order
    [InlineData("Sharma and Sons Tradng Private Limited")] // typo
    public void Variants_RankTheRightCompanyFirst(string input)
    {
        var ranked = CompanyNameResolver.Rank(input, ResolutionHints.None, [Unrelated, Kanpur, Sharma]);

        Assert.Equal(Sharma.Identifier, ranked[0].Candidate.Identifier);
        Assert.True(ranked[0].Score > ranked[1].Score + 0.3);
    }

    [Fact]
    public void ExactVariant_AutoSelectDisabled_NeedsConfirmationButIsEligible()
    {
        var d = Resolve("Sharma & Sons Trading Pvt. Ltd.", Disabled, null, Sharma, Kanpur);

        Assert.Equal(ResolutionStatus.NeedsConfirmation, d.Status);
        Assert.Null(d.ChosenIdentifier);
        Assert.Equal(Sharma.Identifier, d.RecommendedIdentifier);
        Assert.True(d.AutoSelectEligible);
        Assert.Equal(ResolutionReasonCodes.AutoSelectDisabled, d.ReasonCode);
    }

    [Fact]
    public void ExactVariant_AutoSelectEnabled_Resolves()
    {
        var d = Resolve("Sharma & Sons Trading Pvt. Ltd.", Enabled, null, Sharma, Kanpur);

        Assert.Equal(ResolutionStatus.Resolved, d.Status);
        Assert.Equal(ResolutionMethod.AutoSelected, d.Method);
        Assert.Equal(Sharma.Identifier, d.ChosenIdentifier);
    }

    [Theory]
    [InlineData("Trading Sharma and Sons Pvt Ltd")]
    [InlineData("Sharma and Sons Tradng Private Limited")]
    public void NonExactMatch_NeverAutoSelected_EvenWhenEnabled(string input)
    {
        var d = Resolve(input, Enabled, null, Sharma, Unrelated);

        Assert.Equal(ResolutionStatus.NeedsConfirmation, d.Status);
        Assert.False(d.AutoSelectEligible);
        Assert.Equal(Sharma.Identifier, d.RecommendedIdentifier);
        Assert.Null(d.ChosenIdentifier);
    }

    // ── Exact-name twins ──

    private static readonly MasterCandidate TwinMh = Row("U40000MH2011PTC000004", "TWIN TRADERS PRIVATE LIMITED", state: "Maharashtra");
    private static readonly MasterCandidate TwinKa = Row("U40000KA2018PTC000005", "TWIN TRADERS PRIVATE LIMITED", state: "Karnataka");

    [Fact]
    public void ExactTwins_NoHint_AlwaysAmbiguous_EvenWhenEnabled()
    {
        var d = Resolve("Twin Traders Pvt Ltd", Enabled, null, TwinMh, TwinKa);

        Assert.Equal(ResolutionStatus.Ambiguous, d.Status);
        Assert.Null(d.ChosenIdentifier);
        Assert.Equal(2, d.Candidates.Count);
    }

    [Fact]
    public void ExactTwins_StruckOffTwin_StillAmbiguousWithoutPolicy()
    {
        // Status separates their scores, but the requester may mean the struck-off one.
        var struck = TwinKa with { Status = "Strike Off" };

        var d = Resolve("Twin Traders Pvt Ltd", Enabled, null, TwinMh, struck);

        Assert.Equal(ResolutionStatus.Ambiguous, d.Status);
    }

    [Fact]
    public void ExactTwins_SoleActivePolicy_SelectsTheOnlyActiveOne()
    {
        var struck = TwinKa with { Status = "Strike Off" };

        var d = Resolve("Twin Traders Pvt Ltd", new ResolverOptions { AutoSelectEnabled = true, AllowSoleActive = true }, null, TwinMh, struck);

        Assert.Equal(ResolutionStatus.Resolved, d.Status);
        Assert.Equal(TwinMh.Identifier, d.ChosenIdentifier);
        Assert.Equal(ResolutionReasonCodes.SoleActive, d.ReasonCode);
    }

    [Fact]
    public void ExactTwins_BothActive_SoleActivePolicyDoesNotApply()
    {
        var d = Resolve("Twin Traders Pvt Ltd", new ResolverOptions { AutoSelectEnabled = true, AllowSoleActive = true }, null, TwinMh, TwinKa);

        Assert.Equal(ResolutionStatus.Ambiguous, d.Status);
    }

    [Fact]
    public void ExactTwins_StateHint_Distinguishes()
    {
        var d = Resolve("Twin Traders Pvt Ltd", Enabled, new ResolutionHints(State: "karnataka"), TwinMh, TwinKa);

        Assert.Equal(ResolutionStatus.Resolved, d.Status);
        Assert.Equal(TwinKa.Identifier, d.ChosenIdentifier);
    }

    [Fact]
    public void ExactTwins_IncorporationYearHint_Distinguishes()
    {
        var mh = TwinMh with { RegistrationDate = new DateOnly(2011, 5, 1) };
        var ka = TwinKa with { RegistrationDate = new DateOnly(2018, 2, 1) };

        var d = Resolve("Twin Traders Pvt Ltd", Enabled, new ResolutionHints(IncorporationYear: 2011), mh, ka);

        Assert.Equal(mh.Identifier, d.ChosenIdentifier);
    }

    // ── Other safety rules ──

    [Fact]
    public void ToolOnlyHit_NeverAutoSelected()
    {
        var toolOnly = Row("U50000TN2020PTC000006", "SUNRISE ADVISORS PRIVATE LIMITED", toolOnly: true);

        var d = Resolve("Sunrise Advisors Pvt Ltd", Enabled, null, toolOnly);

        Assert.Equal(ResolutionStatus.NeedsConfirmation, d.Status);
        Assert.False(d.AutoSelectEligible);
        Assert.Equal(ResolutionReasonCodes.ToolOnlyNeedsConfirmation, d.ReasonCode);
    }

    [Fact]
    public void LegalFormConflict_IsPenalised()
    {
        var company = Row("U60000MH2019PTC000007", "SUNRISE ADVISORS PRIVATE LIMITED");
        var llp = Row("AAB-1234", "SUNRISE ADVISORS LLP", type: CompanyMasterRecordType.Llp);

        var ranked = CompanyNameResolver.Rank("Sunrise Advisors LLP", ResolutionHints.None, [company, llp]);

        Assert.Equal(llp.Identifier, ranked[0].Candidate.Identifier);
        Assert.Equal(0, ranked[1].Features["EntityFormMatch"]);
    }

    [Fact]
    public void InactiveCompany_IsPenalisedNotExcluded()
    {
        var struck = Sharma with { Status = "Strike Off" };

        var ranked = CompanyNameResolver.Rank("Sharma and Sons Trading Pvt Ltd", ResolutionHints.None, [struck]);

        var only = Assert.Single(ranked);
        Assert.True(only.Score < 1);
        Assert.Contains(only.Reasons, r => r.Contains("Strike Off"));
    }

    [Fact]
    public void HintDisagreement_BlocksAutoSelectOfAnExactMatch()
    {
        var d = Resolve("Twin Traders Pvt Ltd", Enabled, new ResolutionHints(State: "Kerala"), TwinMh);

        Assert.NotEqual(ResolutionStatus.Resolved, d.Status);
    }

    [Fact]
    public void NoPlausibleCandidate_NotFound_WithSuggestions()
    {
        var d = Resolve("Completely Different Name Pvt Ltd", Enabled, null, Unrelated);

        Assert.Equal(ResolutionStatus.NotFound, d.Status);
        Assert.Equal(ResolutionReasonCodes.NotFound, d.ReasonCode);
    }

    [Fact]
    public void BlankName_InvalidInput() =>
        Assert.Equal(ResolutionStatus.InvalidInput, Resolve("  ", Enabled, null, Sharma).Status);

    [Fact]
    public void Ranking_IsDeterministic_TiesBrokenByIdentifier()
    {
        var ranked = CompanyNameResolver.Rank("Twin Traders Pvt Ltd", ResolutionHints.None, [TwinMh, TwinKa]);

        Assert.Equal(TwinKa.Identifier, ranked[0].Candidate.Identifier); // "U40000KA…" < "U40000MH…"
    }

    // ── Supplied identifier short-circuit ──

    [Fact]
    public void ProvidedIdentifier_InMaster_Resolves()
    {
        var d = CompanyNameResolver.ForProvidedIdentifier(" u10000mh2010ptc000001 ", Sharma);

        Assert.Equal(ResolutionStatus.Resolved, d.Status);
        Assert.Equal(ResolutionMethod.UserProvidedCin, d.Method);
        Assert.Equal(Sharma.Identifier, d.ChosenIdentifier);
    }

    [Fact]
    public void ProvidedIdentifier_BadShape_Invalid() =>
        Assert.Equal(ResolutionStatus.InvalidInput, CompanyNameResolver.ForProvidedIdentifier("NOT-A-CIN", null).Status);

    [Fact]
    public void ProvidedIdentifier_NotInMaster_NotResolved()
    {
        var d = CompanyNameResolver.ForProvidedIdentifier("U99999MH2024PTC999999", null);

        Assert.Equal(ResolutionStatus.NotFound, d.Status);
        Assert.Equal(ResolutionReasonCodes.IdentifierNotInMaster, d.ReasonCode);
    }

    // ── Similarity primitives ──

    [Theory]
    [InlineData("SHARMA AND SONS", "SONS AND SHARMA", 1.0)]
    [InlineData("FILM NAGAR", "FILMNAGAR", 0.95)]
    [InlineData("ABC", "XYZ", 0.0)]
    public void CoreSimilarity_KnownValues(string a, string b, double expected) =>
        Assert.Equal(expected, CompanyNameResolver.CoreSimilarity(a, b), 3);

    [Theory]
    [InlineData("FLOWERCYCLING", "FLOWERCYCLNG", 1)]  // deletion
    [InlineData("TRADING", "TRADIGN", 1)]             // transposition
    [InlineData("TRADERS", "TRADERS", 0)]
    [InlineData("KANPUR", "NAGPUR", 2)]
    public void EditDistance_KnownValues(string a, string b, int expected) =>
        Assert.Equal(expected, CompanyNameResolver.EditDistance(a, b));

    [Fact]
    public void ShortWordsWithinOneEdit_DoNotMatch()
    {
        // "SONS"/"SOND" is one edit, but 4-letter words are too short to trust a fuzzy match.
        Assert.Equal(0.5, CompanyNameResolver.CoreSimilarity("SONS ALPHA", "SOND ALPHA"), 3);
    }
}
