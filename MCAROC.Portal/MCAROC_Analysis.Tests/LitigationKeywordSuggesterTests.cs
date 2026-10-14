using MCAROC_Analysis.Services.LitigationData;
using Xunit;
using Xunit.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>#489: the keywords the system suggests by itself. The three companies are the owner's examples (2026-10-13), each with the forms the owner
/// listed; forms that need knowledge (Hindi, a written-out acronym) come from the AI and are checked here through the same checks.</summary>
public class LitigationKeywordSuggesterTests(ITestOutputHelper output)
{
    private static IReadOnlyList<LitigationKeyword> Suggest(string name, CompanyNameSuggestions? ai = null, IEnumerable<string>? former = null) =>
        LitigationKeywordSuggester.Combine(name, LitigationKeywordPlanner.Build(name, former), NameFormVariants.Generate(name),
            ai ?? CompanyNameSuggestions.None, PhoneticVariantGenerator.Generate(name));

    private void Print(IEnumerable<LitigationKeyword> keywords)
    {
        foreach (var k in keywords) output.WriteLine($"{k.Value}  [{k.Source}] {k.Reason}");
    }

    [Fact]
    public void Bharat_Petroleum_gets_the_forms_the_owner_listed()
    {
        var keywords = Suggest("Bharat Petroleum Corporation Limited");
        Print(keywords);
        var values = keywords.Select(k => k.Value).ToList();
        foreach (var expected in new[]
        {
            "Bharat Petroleum Corporation Limited", "Bharat Petroleum Corporation Ltd", "Bharat Petroleum Corporation", "Bharat Petroleum", "BPCL", "B.P.C.L.",
            "Bharat Petroleum Corp Ltd", "Bharat Petroleum Corp. Ltd.", "Bharat Petroleum Corp"
        })
            Assert.Contains(expected, values);
    }

    [Fact]
    public void Hero_FinCorp_gets_the_forms_the_owner_listed()
    {
        var keywords = Suggest("Hero FinCorp Limited");
        Print(keywords);
        var values = keywords.Select(k => k.Value).ToList();
        foreach (var expected in new[] { "Hero FinCorp Limited", "Hero FinCorp Ltd", "Hero FinCorp", "Hero Fin Corp", "Herofincorp", "HFCL" })
            Assert.Contains(expected, values, StringComparer.OrdinalIgnoreCase);
        // The owner's list has "Hero FinCorp Pvt Ltd", but the company is a public limited company: a private-limited form is another entity's name and is
        // never generated. The litigation data service's non-exact search on "Hero FinCorp" returns such records anyway, and they are attributed (#489 a).
        Assert.DoesNotContain(values, v => v.Contains("Pvt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HDFC_Bank_gets_the_forms_the_owner_listed_and_its_written_out_acronym()
    {
        var (expansions, refused) = CompanyNameSuggestionService.CheckExpansions("HDFC Bank Limited",
        [
            new AcronymExpansion("HDFC", "Housing Development Finance Corporation"),
            new AcronymExpansion("HDFC", "Hindustan Development Finance"),          // initials do not spell HDFC
            new AcronymExpansion("ICICI", "Industrial Credit and Investment Corporation of India")   // not a word of this name
        ]);
        Assert.Single(expansions);
        Assert.Equal(2, refused.Count);

        var keywords = Suggest("HDFC Bank Limited", new CompanyNameSuggestions([], expansions));
        Print(keywords);
        var values = keywords.Select(k => k.Value).ToList();
        foreach (var expected in new[] { "HDFC Bank Limited", "HDFC Bank Ltd", "HDFC Bank", "H D F C Bank", "H.D.F.C Bank", "Housing Development Finance Corporation Bank" })
            Assert.Contains(expected, values);
        Assert.DoesNotContain(values, v => v.StartsWith("HDFCB", StringComparison.Ordinal));   // no initials of a name that already has an acronym
    }

    [Theory]
    [InlineData("भारत पेट्रोलियम", "Bharat Petroleum Corporation Limited")]
    [InlineData("भारत पेट्रोलियम कॉर्पोरेशन", "Bharat Petroleum Corporation Limited")]
    [InlineData("भारत पेट्रोलियम कॉर्पोरेशन लिमिटेड", "Bharat Petroleum Corporation Limited")]
    [InlineData("हीरो फिनकॉर्प लिमिटेड", "Hero FinCorp Limited")]
    [InlineData("एचडीएफसी बैंक लिमिटेड", "HDFC Bank Limited")]
    [InlineData("अल्टीग्रीन प्रोपल्शन लैब्स प्राइवेट लिमिटेड", "Altigreen Propulsion Labs Private Limited")]
    [InlineData("लक्ष्मी स्टील्स", "Laxmi Steels Limited")]
    public void A_correct_Devanagari_spelling_passes_the_back_check(string spelling, string legalName)
    {
        var forms = LitigationKeywordPlanner.Build(legalName).Select(k => k.Value).Concat(NameFormVariants.Generate(legalName).Select(f => f.Value)).ToList();
        var (accepted, rejected) = CompanyNameSuggestionService.CheckSpellings(legalName, forms, [spelling]);
        output.WriteLine($"{spelling} -> {NamePhoneticKey.Transliterate(spelling)} key {NamePhoneticKey.Of(spelling)} vs {NamePhoneticKey.Of(legalName)}");
        Assert.Single(accepted);
        Assert.Empty(rejected);
    }

    [Theory]
    [InlineData("इंडियन ऑयल कॉर्पोरेशन", "Bharat Petroleum Corporation Limited")]     // a different company
    [InlineData("भारत", "Bharat Petroleum Corporation Limited")]                        // truncated
    [InlineData("भारतीय तेल निगम", "Bharat Petroleum Corporation Limited")]            // translated, not transliterated
    [InlineData("Bharat Petroleum", "Bharat Petroleum Corporation Limited")]           // not Devanagari
    public void A_wrong_spelling_is_refused(string spelling, string legalName)
    {
        var (accepted, rejected) = CompanyNameSuggestionService.CheckSpellings(legalName, [], [spelling]);
        Assert.Empty(accepted);
        output.WriteLine(Assert.Single(rejected).Reason);
    }

    [Fact]
    public void Phonetic_spellings_vary_distinctive_words_only_and_are_capped()
    {
        var variants = PhoneticVariantGenerator.Generate("Shree Lakshmi Hi-Tech Steels & Engineering Private Limited");
        foreach (var v in variants) output.WriteLine($"{v.Value}  ({v.Reason})");
        var values = variants.Select(v => v.Value).ToList();
        Assert.Contains("Shree Laxmi Hi-Tech Steels & Engineering", values);
        Assert.Contains("Shri Lakshmi Hi-Tech Steels & Engineering", values);
        Assert.Contains("Shree Lakshmi Hi-Tech Steels and Engineering", values);
        Assert.Contains("Shree Lakshmi HiTech Steels & Engineering", values);
        Assert.True(variants.Count <= PhoneticVariantGenerator.MaxVariants);
        Assert.DoesNotContain(values, v => v.Contains("Steel ") || v.Contains("Engeneering"));   // generic words are never varied
    }

    [Fact]
    public void A_name_of_generic_words_only_gets_no_phonetic_spellings() =>
        Assert.Empty(PhoneticVariantGenerator.Generate("India Infra Projects Limited"));

    [Fact]
    public void Portal_searches_get_latin_full_names_only()
    {
        var keywords = Suggest("Bharat Petroleum Corporation Limited", new CompanyNameSuggestions([new HindiSpelling("भारत पेट्रोलियम", "bhaarat")], []));
        var portal = LitigationKeywordSuggester.ForPortals(keywords);
        Assert.DoesNotContain("BPCL", portal);
        Assert.DoesNotContain("भारत पेट्रोलियम", portal);
        Assert.Equal("Bharat Petroleum Corporation Limited", portal[0]);
        Assert.True(portal.Count <= 6);
        Assert.Contains("भारत पेट्रोलियम", LitigationKeywordSuggester.ForPortals(keywords, portalHoldsHindiNames: true, max: 30));
    }

    [Fact]
    public void Keywords_are_capped_and_the_registered_and_former_names_always_survive()
    {
        var former = Enumerable.Range(1, 10).Select(i => $"Old Name Number {i} Limited").ToList();
        var keywords = Suggest("Shree Lakshmi Hi-Tech Steels & Engineering Private Limited", former: former);
        Assert.True(keywords.Count <= LitigationKeywordSuggester.MaxKeywords);
        Assert.Equal("Shree Lakshmi Hi-Tech Steels & Engineering Private Limited", keywords[0].Value);
        Assert.All(former, f => Assert.Contains(f, keywords.Select(k => k.Value)));
    }
}
