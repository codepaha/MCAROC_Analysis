using MCAROC_Analysis.Services.LitigationModule.Merging;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class MergeRulesTests
{
    private static readonly DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Key = "DRT|DRT-MUMBAI-1|OA|413|2022";

    private static MergeCandidate C(long id, string? key = Key, bool verified = true, int orders = 0, int hearings = 0, long request = 1, int ageDays = 0, long? mergedInto = null) =>
        new(id, request, key, verified, orders, hearings, T.AddDays(-ageDays), mergedInto);

    [Fact]
    public void Two_verified_cases_with_equal_gate_complete_keys_in_one_request_merge()
    {
        var d = MergeRules.Evaluate(C(1), C(2));
        Assert.True(d.Allowed);
        Assert.NotNull(d.SurvivorCaseId);
        Assert.NotEqual(d.SurvivorCaseId, d.LoserCaseId);
    }

    [Theory]
    [InlineData("DRT|DRT-MUMBAI-2|OA|413|2022")]    // same number at another tribunal
    [InlineData("DRT|DRT-MUMBAI-1|SA|413|2022")]    // another type
    [InlineData("DRT|DRT-MUMBAI-1|OA|413|2021")]    // another year
    [InlineData("DRT|DRT-MUMBAI-1|OA|414|2022")]    // another number
    public void A_different_key_never_merges(string otherKey)
    {
        var d = MergeRules.Evaluate(C(1), C(2, key: otherKey));
        Assert.False(d.Allowed);
        Assert.Equal(MergeRefusal.DifferentCaseKey, d.Refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_case_without_a_gate_complete_key_is_never_merged(string? key)
    {
        Assert.Equal(MergeRefusal.NoCaseKey, MergeRules.Evaluate(C(1, key: key), C(2)).Refusal);
        Assert.Equal(MergeRefusal.NoCaseKey, MergeRules.Evaluate(C(1), C(2, key: key)).Refusal);
        Assert.Equal(MergeRefusal.NoCaseKey, MergeRules.Evaluate(C(1, key: key), C(2, key: key)).Refusal);   // two keyless cases are not "equal"
    }

    [Fact]
    public void Equal_keys_are_refused_when_identity_is_not_verified_for_both()
    {
        Assert.Equal(MergeRefusal.NotLiveVerified, MergeRules.Evaluate(C(1), C(2, verified: false)).Refusal);
        Assert.Equal(MergeRefusal.NotLiveVerified, MergeRules.Evaluate(C(1, verified: false), C(2, verified: false)).Refusal);
    }

    [Fact]
    public void Cases_of_different_requests_a_case_with_itself_and_an_already_merged_case_are_refused()
    {
        Assert.Equal(MergeRefusal.DifferentRequest, MergeRules.Evaluate(C(1), C(2, request: 2)).Refusal);
        Assert.Equal(MergeRefusal.SameCase, MergeRules.Evaluate(C(1), C(1)).Refusal);
        Assert.Equal(MergeRefusal.AlreadyMerged, MergeRules.Evaluate(C(1), C(2, mergedInto: 9)).Refusal);
    }

    [Fact]
    public void The_survivor_is_the_live_verified_case_then_the_one_with_more_orders_and_hearings_then_the_older_then_the_lower_id()
    {
        // verified beats everything else
        Assert.Equal(1L, MergeRules.ChooseSurvivor(C(1, verified: true, orders: 0), C(2, verified: false, orders: 50)).Survivor.LegacyCaseId);
        // equal verification: more orders and hearings
        Assert.Equal(2L, MergeRules.ChooseSurvivor(C(1, orders: 1, hearings: 2), C(2, orders: 2, hearings: 2)).Survivor.LegacyCaseId);
        // equal weight: older
        Assert.Equal(2L, MergeRules.ChooseSurvivor(C(1, ageDays: 1), C(2, ageDays: 30)).Survivor.LegacyCaseId);
        // equal everything: lower id, whichever order they are given in
        Assert.Equal(3L, MergeRules.ChooseSurvivor(C(3), C(7)).Survivor.LegacyCaseId);
        Assert.Equal(3L, MergeRules.ChooseSurvivor(C(7), C(3)).Survivor.LegacyCaseId);
    }

    [Fact]
    public void The_decision_does_not_depend_on_the_order_the_cases_are_given_in()
    {
        var x = MergeRules.Evaluate(C(1, orders: 3), C(2, orders: 1));
        var y = MergeRules.Evaluate(C(2, orders: 1), C(1, orders: 3));
        Assert.Equal((x.SurvivorCaseId, x.LoserCaseId), (y.SurvivorCaseId, y.LoserCaseId));
        Assert.Equal(1L, x.SurvivorCaseId);
    }

    [Fact]
    public void Merge_groups_collect_only_verified_keyed_unmerged_cases_with_one_survivor_each()
    {
        var groups = MergeRules.FindMergeGroups([
            C(1, orders: 2), C(2), C(3),                                   // three equal verified cases -> one group, survivor 1
            C(4, key: "DRT|DRT-DELHI-1|OA|9|2020"),                        // alone: no group
            C(5, key: "DRT|DRT-DELHI-2|OA|9|2020"), C(6, key: "DRT|DRT-DELHI-1|OA|9|2020", verified: false),   // different tribunals, one unverified: no group
            C(7, key: null), C(8, key: null),                              // keyless: never
            C(9, mergedInto: 1),                                           // already merged: ignored
            C(10, request: 2), C(11, request: 2)                           // another request: its own group
        ]);
        Assert.Equal(2, groups.Count);
        var first = groups[0];
        Assert.Equal((1L, 1L), (first.RequestId, first.SurvivorCaseId));
        Assert.Equal([2L, 3L], first.LoserCaseIds);
        Assert.Equal((2L, 10L), (groups[1].RequestId, groups[1].SurvivorCaseId));
    }
}
