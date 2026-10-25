using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>
/// Unit tests for #452: person-name attribution grade and corroboration for partners, directors, and proprietors.
/// Verifies honorific stripping, transliteration canonicalisation (Md/Mohd), patronymic/relative extraction,
/// mass-party gating, relative-only exclusion, and multi-factor corroboration signals.
/// All test cases use synthetic placeholder names; no real individual names in committed files.
/// </summary>
public class PersonNameAttributionTests
{
    // ══════════════════════════════════════════════════════════════════════════
    // 1. PersonNameNormaliser Tests
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("SHRI RAMESH SHARMA", "RAMESH SHARMA")]
    [InlineData("SMT. ANITA DEVI", "ANITA DEVI")]
    [InlineData("DR. VIKRAM PATEL", "VIKRAM PATEL")]
    [InlineData("ADV. SANJAY VERMA", "SANJAY VERMA")]
    [InlineData("LATE RAJESH GUPTA", "RAJESH GUPTA")]
    [InlineData("JUSTICE K. S. HEGDE", "K S HEGDE")]
    [InlineData("1. MR. ANIL KUMAR", "ANIL KUMAR")]
    [InlineData("2) MRS. POOJA SINGH", "POOJA SINGH")]
    public void Normaliser_strips_honorifics_and_list_numbers(string input, string expected)
    {
        Assert.Equal(expected, PersonNameNormaliser.Normalise(input));
    }

    [Theory]
    [InlineData("MD. IBRAHIM", "MOHAMMED IBRAHIM")]
    [InlineData("MOHD RAFIQ", "MOHAMMED RAFIQ")]
    [InlineData("MOHAMMAD ISMAIL", "MOHAMMED ISMAIL")]
    [InlineData("MUHAMMED ARIF", "MOHAMMED ARIF")]
    public void Normaliser_canonicalises_transliteration_variants(string input, string expected)
    {
        Assert.Equal(expected, PersonNameNormaliser.Normalise(input));
    }

    [Fact]
    public void Normaliser_extracts_patronymics_and_relationships()
    {
        var p1 = PersonNameNormaliser.ParseParty("RAMESH KUMAR S/O SURESH KUMAR");
        Assert.Equal("RAMESH KUMAR", p1.PersonName);
        Assert.Equal("SURESH KUMAR", p1.RelativeName);
        Assert.Equal(PersonRelationship.Father, p1.Relationship);

        var p2 = PersonNameNormaliser.ParseParty("SUNITA DEVI W/O RAJESH SHARMA");
        Assert.Equal("SUNITA DEVI", p2.PersonName);
        Assert.Equal("RAJESH SHARMA", p2.RelativeName);
        Assert.Equal(PersonRelationship.Husband, p2.Relationship);

        var p3 = PersonNameNormaliser.ParseParty("PRIYA SINGH D/O MAHESH SINGH");
        Assert.Equal("PRIYA SINGH", p3.PersonName);
        Assert.Equal("MAHESH SINGH", p3.RelativeName);
        Assert.Equal(PersonRelationship.Father, p3.Relationship);

        var p4 = PersonNameNormaliser.ParseParty("VIKAS YADAV C/O RAM YADAV");
        Assert.Equal("VIKAS YADAV", p4.PersonName);
        Assert.Equal("RAM YADAV", p4.RelativeName);
        Assert.Equal(PersonRelationship.CareOf, p4.Relationship);
    }

    [Fact]
    public void Normaliser_detects_mass_party_indicators()
    {
        var p1 = PersonNameNormaliser.ParseParty("RAMESH KUMAR & ORS");
        Assert.True(p1.IsMassParty);
        Assert.Equal("RAMESH KUMAR", p1.PersonName);

        var p2 = PersonNameNormaliser.ParseParty("ANIL VERMA AND OTHERS");
        Assert.True(p2.IsMassParty);
        Assert.Equal("ANIL VERMA", p2.PersonName);

        var p3 = PersonNameNormaliser.ParseParty("VIKRAM SHAH & ANR.");
        Assert.True(p3.IsMassParty);
        Assert.Equal("VIKRAM SHAH", p3.PersonName);

        var p4 = PersonNameNormaliser.ParseParty("RAJESH GUPTA");
        Assert.False(p4.IsMassParty);
    }

    [Fact]
    public void Normaliser_distinguishes_litigant_from_relative_inside_name()
    {
        // Target is Suresh Kumar (the father)
        var parsed = PersonNameNormaliser.ParseParty("RAMESH KUMAR S/O SURESH KUMAR");
        var (matchesLitigant, appearsOnlyInRelative, _) = PersonNameNormaliser.MatchPerson("SURESH KUMAR", parsed);

        Assert.False(matchesLitigant);
        Assert.True(appearsOnlyInRelative);

        // Target is Ramesh Kumar (the litigant)
        var (matchesLitigant2, appearsOnlyInRelative2, isExact2) = PersonNameNormaliser.MatchPerson("RAMESH KUMAR", parsed);
        Assert.True(matchesLitigant2);
        Assert.False(appearsOnlyInRelative2);
        Assert.True(isExact2);
    }

    [Theory]
    [InlineData("KUMAR", true)]
    [InlineData("SINGH", true)]
    [InlineData("SHARMA", true)]
    [InlineData("VERMA", true)]
    [InlineData("ANIL KUMAR", false)]
    [InlineData("RAMESH SHARMA", false)]
    public void Normaliser_detects_single_common_tokens(string name, bool expected)
    {
        Assert.Equal(expected, PersonNameNormaliser.IsSingleCommonToken(PersonNameNormaliser.Normalise(name)));
    }

    [Fact]
    public void Normaliser_never_matches_substrings()
    {
        // "ANIL" should not match "ANIL KUMAR" or "SUNIL"
        var parsed1 = PersonNameNormaliser.ParseParty("ANIL KUMAR");
        var (match1, _, _) = PersonNameNormaliser.MatchPerson("ANIL", parsed1);
        Assert.False(match1);

        var parsed2 = PersonNameNormaliser.ParseParty("SUNIL SHARMA");
        var (match2, _, _) = PersonNameNormaliser.MatchPerson("ANIL", parsed2);
        Assert.False(match2);

        // "RAMESH GUPTA" should not match "RAMESH GUPTA ENTERPRISES" as a person
        var parsed3 = PersonNameNormaliser.ParseParty("RAMESH GUPTA ENTERPRISES");
        var (match3, _, _) = PersonNameNormaliser.MatchPerson("RAMESH GUPTA", parsed3);
        Assert.False(match3);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. Corroboration Signals and PersonAttributionRules Tests
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Name_alone_never_reaches_confirmed_and_stays_possible()
    {
        var target = new PersonTargetProfile("RAMESH KUMAR");
        var assessment = PersonAttributionRules.Assess(["RAMESH KUMAR"], target);

        Assert.Equal(PersonAttributionGrade.Possible, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, assessment.Outcome);
        Assert.Equal("possible-name-only", assessment.Rule);
    }

    [Fact]
    public void Matching_father_name_upgrades_to_confirmed()
    {
        var target = new PersonTargetProfile("RAMESH KUMAR", FatherOrHusbandName: "SURESH KUMAR");
        var assessment = PersonAttributionRules.Assess(["RAMESH KUMAR S/O SURESH KUMAR"], target);

        Assert.Equal(PersonAttributionGrade.Confirmed, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.Attributed, assessment.Outcome);
        Assert.Equal("confirmed-father-match", assessment.Rule);
    }

    [Fact]
    public void Associated_company_as_co_party_upgrades_to_confirmed()
    {
        var target = new PersonTargetProfile("ANIL VERMA", AssociatedCompanyName: "ACME INFRA PRIVATE LIMITED");
        var assessment = PersonAttributionRules.Assess(["ANIL VERMA", "ACME INFRA PRIVATE LIMITED"], target);

        Assert.Equal(PersonAttributionGrade.Confirmed, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.Attributed, assessment.Outcome);
        Assert.Equal("confirmed-company-coparty", assessment.Rule);
    }

    [Fact]
    public void Matching_district_upgrades_to_probable()
    {
        var target = new PersonTargetProfile("VIKRAM PATEL", District: "PUNE", State: "MAHARASHTRA");
        var assessment = PersonAttributionRules.Assess(["VIKRAM PATEL"], target, caseDistrict: "PUNE", caseState: "MAHARASHTRA");

        Assert.Equal(PersonAttributionGrade.Probable, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.Attributed, assessment.Outcome);
        Assert.Equal("probable-district-match", assessment.Rule);
    }

    [Fact]
    public void Conflicting_father_name_is_excluded()
    {
        var target = new PersonTargetProfile("RAMESH KUMAR", FatherOrHusbandName: "SURESH KUMAR");
        var assessment = PersonAttributionRules.Assess(["RAMESH KUMAR S/O DINESH KUMAR"], target);

        Assert.Equal(PersonAttributionGrade.Excluded, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, assessment.Outcome);
        Assert.Equal("father-husband-conflict", assessment.Rule);
    }

    [Fact]
    public void Relative_only_appearance_is_excluded()
    {
        // Target is the father, but case is against the son
        var target = new PersonTargetProfile("SURESH KUMAR");
        var assessment = PersonAttributionRules.Assess(["RAMESH KUMAR S/O SURESH KUMAR"], target);

        Assert.Equal(PersonAttributionGrade.Excluded, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, assessment.Outcome);
        Assert.Equal("relative-inside-name", assessment.Rule);
    }

    [Fact]
    public void Conflicting_district_without_company_link_is_excluded()
    {
        var target = new PersonTargetProfile("SANJAY SHARMA", State: "KARNATAKA", District: "BENGALURU");
        var assessment = PersonAttributionRules.Assess(["SANJAY SHARMA"], target, caseDistrict: "PATNA", caseState: "BIHAR");

        Assert.Equal(PersonAttributionGrade.Excluded, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, assessment.Outcome);
        Assert.Equal("district-conflict", assessment.Rule);
    }

    [Fact]
    public void Mass_party_cause_title_is_capped_at_possible()
    {
        var target = new PersonTargetProfile("RAJESH GUPTA");
        var assessment = PersonAttributionRules.Assess(["RAJESH GUPTA & ORS"], target);

        Assert.Equal(PersonAttributionGrade.Possible, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, assessment.Outcome);
        Assert.Equal("mass-party-cause-title", assessment.Rule);
    }

    [Fact]
    public void Single_common_token_name_stays_possible()
    {
        var target = new PersonTargetProfile("KUMAR");
        var assessment = PersonAttributionRules.Assess(["KUMAR"], target);

        Assert.Equal(PersonAttributionGrade.Possible, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, assessment.Outcome);
        Assert.Equal("single-common-token", assessment.Rule);
    }

    [Fact]
    public void All_corporate_parties_are_excluded()
    {
        var target = new PersonTargetProfile("ANIL VERMA");
        var assessment = PersonAttributionRules.Assess(["STATE BANK OF INDIA", "UNION OF INDIA", "XYZ INFRASTRUCTURE LIMITED"], target);

        Assert.Equal(PersonAttributionGrade.Excluded, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, assessment.Outcome);
        Assert.Equal("corporate-entities-only", assessment.Rule);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. Golden Namesake Benchmark Cases
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    // 1. Same name + different district -> Excluded
    [InlineData("DINESH SHAH", null, "SURAT", "GUJARAT", "DINESH SHAH", null, "KOLKATA", "WEST BENGAL", PersonAttributionGrade.Excluded, LitigationAttributionOutcome.NotTheCompany)]
    // 2. Relative-inside-name -> Excluded
    [InlineData("HARISH PATEL", null, null, null, "VIJAY PATEL S/O HARISH PATEL", null, null, null, PersonAttributionGrade.Excluded, LitigationAttributionOutcome.NotTheCompany)]
    // 3. Honorific variant with matching father -> Confirmed
    [InlineData("PRAVEEN JAIN", "MAHAVEER JAIN", null, null, "SHRI PRAVEEN JAIN S/O MAHAVEER JAIN", null, null, null, PersonAttributionGrade.Confirmed, LitigationAttributionOutcome.Attributed)]
    // 4. Md/Mohd transliteration with same district -> Probable
    [InlineData("MD. FARHAN", null, "HYDERABAD", "TELANGANA", "MOHAMMED FARHAN", null, "HYDERABAD", "TELANGANA", PersonAttributionGrade.Probable, LitigationAttributionOutcome.Attributed)]
    // 5. Mass party title with name only -> Possible
    [InlineData("SATISH CHOUDHARY", null, null, null, "SATISH CHOUDHARY & 5 ORS", null, null, null, PersonAttributionGrade.Possible, LitigationAttributionOutcome.Ambiguous)]
    // 6. Single-token common surname -> Possible
    [InlineData("SHARMA", null, null, null, "SHARMA", null, null, null, PersonAttributionGrade.Possible, LitigationAttributionOutcome.Ambiguous)]
    public void Golden_namesake_benchmark_cases(
        string targetName, string? targetFather, string? targetDist, string? targetState,
        string partyText, string? partyCoParty, string? caseDist, string? caseState,
        PersonAttributionGrade expectedGrade, LitigationAttributionOutcome expectedOutcome)
    {
        var target = new PersonTargetProfile(
            Name: targetName,
            FatherOrHusbandName: targetFather,
            District: targetDist,
            State: targetState);

        var parties = string.IsNullOrWhiteSpace(partyCoParty) ? new[] { partyText } : new[] { partyText, partyCoParty };
        var assessment = PersonAttributionRules.Assess(parties, target, caseDistrict: caseDist, caseState: caseState);

        Assert.Equal(expectedGrade, assessment.Grade);
        Assert.Equal(expectedOutcome, assessment.Outcome);
    }

    // ─────────────────────────────────────────────────────────────
    // 4. PR Review Regression Tests (Mixed-case, Prefix Company, Partial Names, Lender Context)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Normaliser_preserves_mixed_case_characters_and_does_not_collapse_to_initials()
    {
        Assert.Equal("RAMESH KUMAR", PersonNameNormaliser.Normalise("Ramesh Kumar"));
        Assert.Equal("RAKESH KHANNA", PersonNameNormaliser.Normalise("Rakesh Khanna"));
        Assert.Equal("R K SHARMA", PersonNameNormaliser.Normalise("R. K. Sharma"));
        Assert.Equal("MOHAMMED IMRAN KHAN", PersonNameNormaliser.Normalise("Dr. Mohd. Imran Khan"));
    }

    [Fact]
    public void Mixed_case_different_names_sharing_initials_never_match()
    {
        var target = new PersonTargetProfile("Ramesh Kumar", AssociatedCompanyName: "ACME LIMITED");
        var parties = new[] { "Rakesh Khanna", "ACME LIMITED" };

        var assessment = PersonAttributionRules.Assess(parties, target);
        Assert.Equal(PersonAttributionGrade.Excluded, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, assessment.Outcome);
        Assert.Equal("name-not-matched", assessment.Rule);
    }

    [Fact]
    public void Company_coparty_requires_whole_entity_match_and_rejects_unrelated_prefix_companies()
    {
        var target = new PersonTargetProfile("RAMESH KUMAR", AssociatedCompanyName: "ACME");

        // Unrelated prefix/extension company must NOT corroborate
        var unrelatedParties = new[] { "RAMESH KUMAR", "ACME LONG PRODUCTS LIMITED" };
        var assessmentUnrelated = PersonAttributionRules.Assess(unrelatedParties, target);
        Assert.Equal(PersonAttributionGrade.Possible, assessmentUnrelated.Grade);
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, assessmentUnrelated.Outcome);
        Assert.Equal("possible-name-only", assessmentUnrelated.Rule);

        // Exact whole-entity company must corroborate
        var matchingParties = new[] { "RAMESH KUMAR", "ACME LIMITED" };
        var assessmentMatching = PersonAttributionRules.Assess(matchingParties, target);
        Assert.Equal(PersonAttributionGrade.Confirmed, assessmentMatching.Grade);
        Assert.Equal(LitigationAttributionOutcome.Attributed, assessmentMatching.Outcome);
        Assert.Equal("confirmed-company-coparty", assessmentMatching.Rule);
    }

    [Fact]
    public void Partial_name_candidates_settle_as_possible_ambiguous_and_never_pending()
    {
        var target = new PersonTargetProfile("RAMESH KUMAR");
        var parties = new[] { "RAMESH", "STATE OF TEST" };

        var assessment = PersonAttributionRules.Assess(parties, target);
        Assert.Equal(PersonAttributionGrade.Possible, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, assessment.Outcome);
        Assert.Equal("possible-partial-name", assessment.Rule);
        Assert.False(assessment.NeedsJudgement);
    }

    [Fact]
    public void Lender_link_requires_credit_recovery_context()
    {
        var target = new PersonTargetProfile("RAMESH KUMAR");
        var parties = new[] { "RAMESH KUMAR", "STATE BANK OF INDIA" };

        // Without recovery context (generic civil dispute): stays name-only Possible
        var genericAssessment = PersonAttributionRules.Assess(parties, target, caseType: "Motor Accident Claim", court: "District Court");
        Assert.Equal(PersonAttributionGrade.Possible, genericAssessment.Grade);
        Assert.Equal("possible-name-only", genericAssessment.Rule);

        // With recovery context (DRT / O.A. / NI 138): reaches Probable via lender link
        var recoveryAssessment = PersonAttributionRules.Assess(parties, target, caseType: "Original Application", court: "Debts Recovery Tribunal");
        Assert.Equal(PersonAttributionGrade.Probable, recoveryAssessment.Grade);
        Assert.Equal("probable-lender-link", recoveryAssessment.Rule);
    }

    [Fact]
    public void Verified_personal_address_corroborates_and_conflicts_correctly()
    {
        // Verified person address is known (e.g. Surat, Gujarat)
        var targetWithPersonalAddress = new PersonTargetProfile(
            "DINESH SHAH",
            District: "SURAT",
            State: "GUJARAT");

        // Case in same district -> Probable
        var sameDistAssessment = PersonAttributionRules.Assess(
            ["DINESH SHAH", "STATE OF GUJARAT"], targetWithPersonalAddress, caseDistrict: "SURAT", caseState: "GUJARAT");
        Assert.Equal(PersonAttributionGrade.Probable, sameDistAssessment.Grade);
        Assert.Equal("probable-district-match", sameDistAssessment.Rule);

        // Case in different state -> Excluded (district-conflict) because verified personal residence contradicts
        var conflictAssessment = PersonAttributionRules.Assess(
            ["DINESH SHAH", "SOME OTHER PARTY"], targetWithPersonalAddress, caseDistrict: "KOLKATA", caseState: "WEST BENGAL");
        Assert.Equal(PersonAttributionGrade.Excluded, conflictAssessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, conflictAssessment.Outcome);
        Assert.Equal("district-conflict", conflictAssessment.Rule);
    }

    [Fact]
    public void Unverified_personal_address_never_triggers_district_conflict_even_across_states()
    {
        // Target without verified personal address (only associated company known)
        var targetWithoutPersonalAddress = new PersonTargetProfile(
            "RAMESH KUMAR",
            AssociatedCompanyName: "ACME LIMITED",
            District: null,
            State: null);

        // Case in Delhi when company might be Mumbai: must NOT conflict
        var assessment = PersonAttributionRules.Assess(
            ["RAMESH KUMAR", "STATE OF TEST"], targetWithoutPersonalAddress, caseDistrict: "DELHI", caseState: "DELHI");
        Assert.Equal(PersonAttributionGrade.Possible, assessment.Grade);
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, assessment.Outcome);
        Assert.Equal("possible-name-only", assessment.Rule);
    }
}
