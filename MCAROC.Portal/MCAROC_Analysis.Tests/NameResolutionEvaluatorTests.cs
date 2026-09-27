using MCAROC_Analysis.Services.CompanyMaster;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #293's offline harness: the scoring must be right before any threshold is chosen from it.</summary>
public class NameResolutionEvaluatorTests
{
    private static NameResolutionOutcome Outcome(string? expected, string category, params (string Id, double Score)[] candidates) =>
        new(new NameResolutionCase("input", expected, category), candidates.Select(c => new NameCandidate(c.Id, c.Score)).ToList());

    [Fact]
    public void AutoSelect_TopAboveThreshold_Picks() =>
        Assert.Equal("A", NameResolutionEvaluator.AutoSelect([new("A", 0.9), new("B", 0.4)], 0.8));

    [Fact]
    public void AutoSelect_BelowThreshold_Abstains() =>
        Assert.Null(NameResolutionEvaluator.AutoSelect([new("A", 0.7)], 0.8));

    [Fact]
    public void AutoSelect_TiedTop_Abstains() =>
        // Two equally good candidates is exactly the ambiguity that must go to a human.
        Assert.Null(NameResolutionEvaluator.AutoSelect([new("A", 1.0), new("B", 1.0)], 0.5));

    [Fact]
    public void AutoSelect_NoCandidates_Abstains() =>
        Assert.Null(NameResolutionEvaluator.AutoSelect([], 0.1));

    [Fact]
    public void Evaluate_CountsPrecisionRecallAndRetrieval()
    {
        var outcomes = new[]
        {
            Outcome("A", "X", ("A", 1.0)),               // correct auto-select
            Outcome("B", "X", ("C", 1.0), ("B", 0.6)),   // wrong auto-select (B retrieved though)
            Outcome("D", "X", ("D", 0.6)),               // abstains at 0.8
            Outcome("E", "X"),                           // nothing retrieved
        };

        var report = NameResolutionEvaluator.Evaluate("s", outcomes, [0.8]);
        var m = Assert.Single(report.Overall);

        Assert.Equal(4, m.Cases);
        Assert.Equal(2, m.AutoSelected);
        Assert.Equal(1, m.Correct);
        Assert.Equal(0.5, m.Precision, 6);
        Assert.Equal(0.25, m.Recall, 6);
        Assert.Equal(0.75, report.RetrievalRecall, 6); // A, B, D retrieved; E not
    }

    [Fact]
    public void Evaluate_NoCorrectAnswer_AnySelectionIsAFalseAccept()
    {
        // Same-name duplicates: ExpectedIdentifier null means the only right outcome is to abstain.
        var outcomes = new[]
        {
            Outcome(null, "Duplicate", ("A", 1.0), ("B", 0.8)), // auto-selects A → false accept
            Outcome(null, "Duplicate", ("A", 1.0), ("B", 1.0)), // tie → abstains, correct behaviour
        };

        var m = Assert.Single(NameResolutionEvaluator.Evaluate("s", outcomes, [0.5]).Overall);

        Assert.Equal(1, m.AutoSelected);
        Assert.Equal(0, m.Correct);
        Assert.Equal(0.0, m.Precision);
        Assert.Equal(0.0, m.Recall); // no answerable cases
    }

    [Fact]
    public void Evaluate_ReportsPerCategory()
    {
        var outcomes = new[] { Outcome("A", "Typo", ("A", 1.0)), Outcome("B", "WordOrder", ("C", 1.0)) };

        var report = NameResolutionEvaluator.Evaluate("s", outcomes, [0.5]);

        Assert.Equal(1.0, report.ByCategory["Typo"][0].Precision);
        Assert.Equal(0.0, report.ByCategory["WordOrder"][0].Precision);
    }

    [Theory]
    [InlineData(0, 0, 0.0, 1.0)]
    [InlineData(3, 3, 0.4385030, 1.0)]      // 3/3 is nowhere near "100%" — the CI says so
    [InlineData(995, 1000, 0.98838, 0.99786)]
    public void WilsonInterval_KnownValues(int successes, int trials, double low, double high)
    {
        var (l, h) = NameResolutionEvaluator.WilsonInterval(successes, trials);

        Assert.Equal(low, l, 3);
        Assert.Equal(high, h, 3);
    }

    [Fact]
    public void FormatReport_ContainsEveryThresholdRow()
    {
        var report = NameResolutionEvaluator.Evaluate("Strategy X", [Outcome("A", "Labelled", ("A", 1.0))]);

        var text = NameResolutionEvaluator.FormatReport(report);

        Assert.Contains("## Strategy X", text);
        Assert.Contains("### Labelled", text);
        foreach (var t in NameResolutionEvaluator.DefaultThresholds)
            Assert.Contains($"| {t:0.00} |", text);
    }
}

public class SyntheticNameCaseGeneratorTests
{
    private static readonly (string, string)[] Rows =
    [
        ("U12345MH2010PTC000001", "SHARMA AND SONS TRADING PRIVATE LIMITED"),
        ("L99999DL1990PLC000002", "THE INDIAN HOTELS COMPANY LIMITED"),
        ("AAA-1234", "SUNRISE ADVISORS LLP"),
    ];

    [Fact]
    public void Deterministic_ForASeed() =>
        Assert.Equal(SyntheticNameCaseGenerator.Generate(Rows, seed: 7), SyntheticNameCaseGenerator.Generate(Rows, seed: 7));

    [Fact]
    public void EveryCase_ExpectsItsSourceRow()
    {
        var ids = Rows.Select(r => r.Item1).ToHashSet();
        Assert.All(SyntheticNameCaseGenerator.Generate(Rows), c => Assert.Contains(c.ExpectedIdentifier!, ids));
    }

    [Theory]
    [InlineData(SyntheticNameCaseGenerator.CaseAndPunctuation)]
    [InlineData(SyntheticNameCaseGenerator.SuffixVariant)]
    [InlineData(SyntheticNameCaseGenerator.Ampersand)]
    public void NormalizationCategories_NormalizeBackToTheSourceName(string category)
    {
        // These three are meant to be fully absorbed by I1's normalizer; if one ever isn't, either the
        // generator or the normalizer has drifted.
        var source = Rows.ToDictionary(r => r.Item1, r => CompanyNameNormalizer.Normalize(r.Item2).NameNormalized);
        var cases = SyntheticNameCaseGenerator.Generate(Rows).Where(c => c.Category == category).ToList();

        Assert.NotEmpty(cases);
        Assert.All(cases, c => Assert.Equal(source[c.ExpectedIdentifier!], CompanyNameNormalizer.Normalize(c.InputName).NameNormalized));
    }

    [Fact]
    public void WordOrder_SwapsTheCoreAndKeepsTheSuffix()
    {
        var wordOrder = SyntheticNameCaseGenerator.Generate(Rows)
            .Where(c => c.Category == SyntheticNameCaseGenerator.WordOrder).Select(c => c.InputName).ToList();

        Assert.Contains("AND SHARMA SONS TRADING PRIVATE LIMITED", wordOrder);
        Assert.Contains("HOTELS INDIAN COMPANY LIMITED", wordOrder); // leading THE is not part of the core
    }

    [Fact]
    public void Typo_DropsOneLetterFromTheLongestCoreWord()
    {
        var typo = SyntheticNameCaseGenerator.Generate(Rows)
            .Single(c => c.Category == SyntheticNameCaseGenerator.Typo && c.ExpectedIdentifier == "AAA-1234").InputName;

        Assert.EndsWith(" LLP", typo);
        Assert.Equal("SUNRISE ADVISORS LLP".Length - 1, typo.Length);
        Assert.NotEqual(CompanyNameNormalizer.Normalize("SUNRISE ADVISORS LLP").NameNormalized, CompanyNameNormalizer.Normalize(typo).NameNormalized);
    }
}
