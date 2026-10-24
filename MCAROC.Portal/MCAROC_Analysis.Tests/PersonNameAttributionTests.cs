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
}

