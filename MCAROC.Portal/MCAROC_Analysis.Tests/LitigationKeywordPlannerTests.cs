using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationKeywordPlannerTests
{
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
