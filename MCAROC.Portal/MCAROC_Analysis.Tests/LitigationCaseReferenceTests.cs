using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#343: matching the case a question names to the request's structured case records. The forms here are
/// the real ones — users write "TP 255/2019", the Coastal order sheets say "TP No. 255/CTB/2019", the litigation data
/// stores numbers several ways ("TP(IB) 255/CTB/2019", "1" + CaseYear, "OS/123/2025/").</summary>
public class LitigationCaseReferenceTests
{
    private static LitigationCase Case(long id, string? number, string? type = null, string? year = null) =>
        new() { LitigationCaseId = id, CaseNumber = number, CaseType = type, CaseYear = year };

    [Theory]
    [InlineData("What is the current status of TP 255/2019?", "TP", "255", "2019")]
    [InlineData("Summarise TP No. 255/CTB/2019", "TP", "255", "2019")]
    [InlineData("What is IA No. 45 of 2021 about?", "IA", "45", "2021")]
    [InlineData("What is Company Appeal (AT) (Ins) No. 935 of 2023 about?", "COMPANYAPPEALATINS", "935", "2023")]
    [InlineData("status of CP(IB) 593/KB/2017", "CPIB", "593", "2017")]
    [InlineData("what about 1046/2023", "", "1046", "2023")]
    public void ExtractsTypeSerialAndYear(string question, string letters, string serial, string year)
    {
        var r = Assert.Single(LitigationCaseReference.Extract(question));

        Assert.Equal(new LitigationCaseReference(letters, serial, year), r);
    }

    [Fact]
    public void NoCaseReference_InAnOrdinaryQuestion()
    {
        Assert.Empty(LitigationCaseReference.Extract("Who is the liquidator and what did the order of 2023 say?"));
    }

    [Theory]
    [InlineData("TP(IB) 255/CTB/2019", null, null, "TPIB", "255", "2019")]
    [InlineData("1", "OS", "2020", "OS", "1", "2020")]
    [InlineData("OS/123/2025/", "OS", null, "OS", "123", "2025")]
    [InlineData("W.P.(C) No. 001046 - / 2023", "WP", "2023", "WPC", "1046", "2023")]
    public void ReadsTheStoredCasesOwnReference(string number, string? type, string? year, string letters, string serial, string expectedYear)
    {
        Assert.Equal(new LitigationCaseReference(letters, serial, expectedYear), LitigationCaseReference.Of(Case(1, number, type, year)));
    }

    [Fact]
    public void UsersShortForm_FindsTheStoredCase_NotADifferentTypeWithTheSameNumber()
    {
        var tp = Case(1, "TP(IB) 255/CTB/2019");
        var ia = Case(2, "IA(IB) 255/CB/2019");
        var other = Case(3, "TP 93/CTB/2019");

        var found = LitigationCaseReference.FindReferencedCases("What is the current status of TP 255/2019?", [tp, ia, other]);

        Assert.Equal([tp], found);
    }

    [Fact]
    public void AbbreviatedType_StillMatches_WhenSerialAndYearIdentifyOneCase()
    {
        var appeal = Case(1, "Company Appeal (AT)(Ins) 935/2023");

        Assert.Equal([appeal], LitigationCaseReference.FindReferencedCases("What did NCLAT say in CA 935/2023?", [appeal, Case(2, "TP 93/2019")]));
    }

    [Fact]
    public void AmbiguousSerialAndYear_WithContradictingType_MatchesNothing()
    {
        var found = LitigationCaseReference.FindReferencedCases("status of CA 255/2019",
            [Case(1, "TP(IB) 255/CTB/2019"), Case(2, "IA(IB) 255/CB/2019")]);

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("09-01-2025", 2025, 1, 9)]
    [InlineData("20/07/2026", 2026, 7, 20)]
    [InlineData("2026-05-06", 2026, 5, 6)]
    [InlineData("6 May 2026", 2026, 5, 6)]
    public void ParsesOrderDates(string value, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), LitigationCaseReference.ParseDate(value));
    }

    [Fact]
    public void UnparseableDate_IsNull()
    {
        Assert.Null(LitigationCaseReference.ParseDate("sometime in 2020"));
        Assert.Null(LitigationCaseReference.ParseDate(null));
    }

    [Fact]
    public void CaseFact_CarriesStatusHearingsPartiesAndNewestOrdersFirst()
    {
        var c = Case(7, "TP(IB) 255/CTB/2019");
        c.Court = "NCLT Cuttack";
        c.CaseStatus = "Pending";
        c.NextHearingDate = "24-08-2026";
        c.PetitionersJson = """["State Bank of India"]""";
        c.RespondentsJson = """[{"name":"Coastal Projects Ltd"}]""";
        c.LastSeenUtc = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);
        c.Orders =
        [
            new() { LitigationCaseOrderId = 1, OrderDate = "12-09-2023", OrderType = "Order" },
            new() { LitigationCaseOrderId = 2, OrderDate = "20-07-2026", OrderType = "Order" },
            new() { LitigationCaseOrderId = 3, OrderDate = "06-05-2026" }
        ];
        var sources = new List<RetrievedSource>();

        RetrievalContextBuilder.AddLitigationCaseSources(sources, [c]);

        var fact = Assert.Single(sources);
        Assert.Equal("C1", fact.Tag);
        Assert.Equal(SourceType.LitigationCase, fact.Type);
        Assert.Equal(7, fact.LitigationCaseId);
        Assert.Contains("Status: Pending.", fact.Text);
        Assert.Contains("Next hearing: 24-08-2026.", fact.Text);
        Assert.Contains("Petitioners: State Bank of India.", fact.Text);
        Assert.Contains("Respondents: Coastal Projects Ltd.", fact.Text);
        Assert.Contains("3 order(s) on record; newest first: 20-07-2026 (Order); 06-05-2026; 12-09-2023 (Order)", fact.Text);
        Assert.Contains("refreshed 01-10-2026 (IST)", fact.Text);
    }

    [Fact]
    public void LitigationChunkLabel_ShowsTheOrderDate()
    {
        var label = RetrievalContextBuilder.LitigationChunkLabel(new LitigationOrderChunk
            { CaseNumber = "TP 255/2019", Court = "NCLT Cuttack", OrderDate = "20-07-2026", PageNumber = 2 });

        Assert.Equal("TP 255/2019 (NCLT Cuttack) · Order 20-07-2026 · Page 2", label);
    }

    [Theory]
    [InlineData("What is the current status of TP 255/2019?", true)]
    [InlineData("Summarise the last three orders", true)]
    [InlineData("When is the next hearing?", true)]
    [InlineData("What is IA 5793/2024 about?", false)]
    public void DetectsRecencyQuestions(string question, bool expected)
    {
        Assert.Equal(expected, QuestionHintExtractor.Extract(question, [], []).AsksForRecency);
    }
}
