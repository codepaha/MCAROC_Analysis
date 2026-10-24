using System.Text.RegularExpressions;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

/// <summary>#514: the keyword list for names as the registry writes them, in capitals. Earlier tests used mixed-case names, so on 2026-10-16 an
/// LLP's ordinary words were searched letter by letter ("N A T U R E") and nobody noticed: tests checked case counts, not the list. These pin the
/// exact list (the rule-made part; the AI's Hindi spellings and acronym expansions are covered in LitigationKeywordSuggesterTests) for each
/// E2E matrix profile, and check the rules over more names.</summary>
public sealed partial class RegistryNameKeywordTests
{
    private static IReadOnlyList<LitigationKeyword> Keywords(string legalName, params string[] formerNames) =>
        LitigationKeywordSuggester.Combine(legalName, LitigationKeywordPlanner.Build(legalName, formerNames), NameFormVariants.Generate(legalName),
            new CompanyNameSuggestions([], []), PhoneticVariantGenerator.Generate(legalName));

    private static void AssertList(IReadOnlyList<LitigationKeyword> actual, params (string Value, LitigationKeywordSource Source)[] expected) =>
        Assert.Equal(expected.ToList(), actual.Select(k => (k.Value, k.Source)).ToList());

    /// <summary>Matrix profile 4 (LLP), the 2026-10-16 company: words stay words, and the name is also searched without "LLP".</summary>
    [Fact]
    public void An_llp_is_searched_with_and_without_llp_and_its_words_are_never_spelled_out() =>
        AssertList(Keywords("NATURE EARTH KHARKHODA LLP"),
            ("NATURE EARTH KHARKHODA LLP", LitigationKeywordSource.LegalName),
            ("NATURE EARTH KHARKHODA", LitigationKeywordSource.GeneratedLegalSuffixVariant));

    /// <summary>Matrix profile 6 (renamed): every former name is searched as registered, before any generated form.</summary>
    [Fact]
    public void A_renamed_company_is_searched_under_every_former_name() =>
        AssertList(Keywords("COASTAL PROJECTS LIMITED", "COASTAL PROJECTS PRIVATE LIMITED", "EAST COAST CARRIERS PVT LTD"),
            ("COASTAL PROJECTS LIMITED", LitigationKeywordSource.LegalName),
            ("COASTAL PROJECTS PRIVATE LIMITED", LitigationKeywordSource.HistoricalLegalName),
            ("EAST COAST CARRIERS PVT LTD", LitigationKeywordSource.HistoricalLegalName),
            ("COASTAL PROJECTS", LitigationKeywordSource.GeneratedLegalSuffixVariant),
            ("COASTAL PROJECTS Ltd", LitigationKeywordSource.GeneratedLegalSuffixVariant));

    /// <summary>Matrix profile 7 (a real acronym): spaced and dotted, never respelled as if it were a word ("HDPHC" was generated before #514).</summary>
    [Fact]
    public void A_real_acronym_is_spaced_and_dotted_but_never_respelled() =>
        AssertList(Keywords("HDFC BANK LIMITED"),
            ("HDFC BANK LIMITED", LitigationKeywordSource.LegalName),
            ("HDFC BANK", LitigationKeywordSource.GeneratedLegalSuffixVariant),
            ("HDFC BANK Ltd", LitigationKeywordSource.GeneratedLegalSuffixVariant),
            ("H D F C BANK", LitigationKeywordSource.GeneratedNameForm),
            ("H.D.F.C BANK", LitigationKeywordSource.GeneratedNameForm));

    [Fact]
    public void A_private_limited_company_gets_its_short_legal_forms() =>
        AssertList(Keywords("TRUBOARD PRIVATE LIMITED", "TRUBOARD VT PRIVATE LIMITED"),
            ("TRUBOARD PRIVATE LIMITED", LitigationKeywordSource.LegalName),
            ("TRUBOARD VT PRIVATE LIMITED", LitigationKeywordSource.HistoricalLegalName),
            ("TRUBOARD", LitigationKeywordSource.GeneratedLegalSuffixVariant),
            ("TRUBOARD Ltd", LitigationKeywordSource.GeneratedLegalSuffixVariant),
            ("TRUBOARD Pvt Ltd", LitigationKeywordSource.GeneratedNameForm));

    [Fact]
    public void Honorific_and_common_indian_spellings_vary_one_word_at_a_time() =>
        AssertList(Keywords("SRI LAKSHMI INFRA PROJECTS PVT LTD"),
            ("SRI LAKSHMI INFRA PROJECTS PVT LTD", LitigationKeywordSource.LegalName),
            ("SRI LAKSHMI INFRA PROJECTS", LitigationKeywordSource.GeneratedLegalSuffixVariant),
            ("SRI LAKSHMI INFRA PROJECTS Limited", LitigationKeywordSource.GeneratedLegalSuffixVariant),
            ("SHREE LAKSHMI INFRA PROJECTS", LitigationKeywordSource.GeneratedPhoneticVariant),
            ("SHRI LAKSHMI INFRA PROJECTS", LitigationKeywordSource.GeneratedPhoneticVariant),
            ("SRI LAXMI INFRA PROJECTS", LitigationKeywordSource.GeneratedPhoneticVariant),
            ("SRI LAKSHMEE INFRA PROJECTS", LitigationKeywordSource.GeneratedPhoneticVariant));

    public static TheoryData<string> RegistryNames =>
    [
        "NATURE EARTH KHARKHODA LLP", "A B C TRADING LLP", "GODREJ PROPERTIES LIMITED", "HDFC BANK LIMITED", "NTPC LIMITED", "MRF LIMITED",
        "BHARAT PETROLEUM CORPORATION LIMITED", "ALTIGREEN PROPULSION LABS PRIVATE LIMITED", "LIFEX SCIENCES PRIVATE LIMITED",
        "SUN PHARMA ADVANCED RESEARCH COMPANY LIMITED", "OIL AND NATURAL GAS CORPORATION LIMITED", "THE NEW INDIA ASSURANCE COMPANY LIMITED",
        "SHREE CEMENT LIMITED", "TATA STEEL LIMITED", "COASTAL PROJECTS LIMITED",
    ];

    /// <summary>The owner's rules, over more registry names: no made-up initials (2026-10-16), no ordinary word spelled out letter by letter,
    /// every keyword keeps a distinctive word of the name, and nothing outnumbers the cap.</summary>
    [Theory]
    [MemberData(nameof(RegistryNames))]
    public void Registry_names_follow_the_keyword_rules(string legalName)
    {
        var keywords = Keywords(legalName);
        var words = PhoneticVariantGenerator.WithoutLegalSuffix(legalName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(words.Where(w => w.Length > 1 && !w.Equals("AND", StringComparison.OrdinalIgnoreCase)).Select(w => w[0]));

        Assert.Equal(legalName, keywords[0].Value);
        Assert.InRange(keywords.Count, 1, LitigationKeywordSuggester.MaxKeywords);
        Assert.DoesNotContain(keywords, k => k.Source == LitigationKeywordSource.GeneratedAcronym);
        Assert.DoesNotContain(keywords, k => words.Length > 1 && k.Value.Split(' ').Contains(initials, StringComparer.OrdinalIgnoreCase)
            && !words.Contains(initials, StringComparer.OrdinalIgnoreCase));

        // Spelled out letter by letter ("N A T U R E", "N.A.T.U.R.E"): only a word that is an acronym of the name may be.
        foreach (var k in keywords)
        {
            foreach (Match m in SpelledOut().Matches(k.Value))
            {
                var word = Regex.Replace(m.Value, @"[\s.]", "");
                Assert.True(NameFormVariants.IsAcronymWord(word, allCapitals: true),
                    $"\"{word}\" spelled out in \"{k.Value}\" for {legalName}");
            }
        }
    }

    [Fact]
    public void A_name_written_with_initials_is_left_as_it_is() =>
        AssertList(Keywords("A B C TRADING LLP"),
            ("A B C TRADING LLP", LitigationKeywordSource.LegalName),
            ("A B C TRADING", LitigationKeywordSource.GeneratedLegalSuffixVariant));

    [GeneratedRegex(@"\b(?:[A-Z][ .]){2,}[A-Z]\b")]
    private static partial Regex SpelledOut();
}
