using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationKeywordPlannerTests
{
    [Fact]
    public void ParseJson_reads_the_shape_a_search_job_stores_names_and_sources_intact()
    {
        // Exactly what LitigationSearchJobService writes: lower-case names, the source as text.
        var stored = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { value = "Example Projects Limited", source = nameof(LitigationKeywordSource.LegalName) },
            new { value = "EXAMPLE PROJECTS PRIVATE LIMITED", source = nameof(LitigationKeywordSource.HistoricalLegalName) }
        });

        var keywords = LitigationKeyword.ParseJson(stored);

        Assert.Equal(
            [("Example Projects Limited", LitigationKeywordSource.LegalName), ("EXAMPLE PROJECTS PRIVATE LIMITED", LitigationKeywordSource.HistoricalLegalName)],
            keywords.Select(k => (k.Value, k.Source)).ToList());
    }

    [Theory]
    [InlineData("""[{"Value":"Example Co","Source":0}]""")] // capitalised names, numeric source
    [InlineData("""[{"value":"Example Co","source":"LegalName"}]""")]
    public void ParseJson_accepts_either_casing_and_a_numeric_or_text_source(string json) =>
        Assert.Equal(("Example Co", LitigationKeywordSource.LegalName), Assert.Single(LitigationKeyword.ParseJson(json)) is var k ? (k.Value, k.Source) : default);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""[{"value":"  ","source":"LegalName"}]""")]
    public void ParseJson_gives_nothing_for_missing_unreadable_or_blank_keywords(string? json) =>
        Assert.Empty(LitigationKeyword.ParseJson(json));

    [Fact]
    public void Build_keeps_vetted_abbreviation_phonetic_and_Hindi_aliases_with_their_provenance()
    {
        var keywords = LitigationKeywordPlanner.Build(
            "Bharat Petroleum Corporation Limited",
            approvedAliases:
            [
                ApprovedLitigationAlias.Curated("BPCL"),
                ApprovedLitigationAlias.Curated("B.P.C.L."),
                ApprovedLitigationAlias.Hindi("भारत पेट्रोलियम"),
                ApprovedLitigationAlias.Hindi("भारत पेट्रोलियम कॉर्पोरेशन"),
                ApprovedLitigationAlias.Phonetic("Bharat Petroleum Corp. Ltd.")
            ]);

        Assert.Collection(keywords,
            x => Assert.Equal(new LitigationKeyword("Bharat Petroleum Corporation Limited", LitigationKeywordSource.LegalName), x),
            x => Assert.Equal(new LitigationKeyword("BPCL", LitigationKeywordSource.ApprovedAlias), x),
            x => Assert.Equal(new LitigationKeyword("B.P.C.L.", LitigationKeywordSource.ApprovedAlias), x),
            x => Assert.Equal(new LitigationKeyword("भारत पेट्रोलियम", LitigationKeywordSource.ApprovedHindiAlias), x),
            x => Assert.Equal(new LitigationKeyword("भारत पेट्रोलियम कॉर्पोरेशन", LitigationKeywordSource.ApprovedHindiAlias), x),
            x => Assert.Equal(new LitigationKeyword("Bharat Petroleum Corp. Ltd.", LitigationKeywordSource.ApprovedPhoneticAlias), x),
            x => Assert.Equal(new LitigationKeyword("Bharat Petroleum Corporation", LitigationKeywordSource.GeneratedLegalSuffixVariant), x),
            x => Assert.Equal(new LitigationKeyword("Bharat Petroleum Corporation Ltd", LitigationKeywordSource.GeneratedLegalSuffixVariant), x));
    }

    [Fact]
    public void Build_adds_name_history_and_deduplicates_whitespace_without_losing_first_provenance()
    {
        var keywords = LitigationKeywordPlanner.Build(
            "HDFC Bank Limited",
            ["Housing Development Finance Corporation Bank", " HDFC   Bank Limited "],
            [ApprovedLitigationAlias.Curated("H D F C Bank"), ApprovedLitigationAlias.Curated("H.D.F.C Bank")]);

        Assert.Equal(
            ["HDFC Bank Limited", "Housing Development Finance Corporation Bank", "H D F C Bank", "H.D.F.C Bank", "HDFC Bank", "HDFC Bank Ltd"],
            keywords.Select(x => x.Value));
        Assert.Equal(LitigationKeywordSource.LegalName, keywords[0].Source);
        Assert.Equal(LitigationKeywordSource.HistoricalLegalName, keywords[1].Source);
    }

    [Fact]
    public void Build_does_not_guess_ambiguous_acronyms()
    {
        var keywords = LitigationKeywordPlanner.Build("Hero FinCorp Limited");

        Assert.DoesNotContain(keywords, x => x.Value == "HFCL");
        Assert.Equal(["Hero FinCorp Limited", "Hero FinCorp", "Hero FinCorp Ltd"], keywords.Select(x => x.Value));
    }
}
