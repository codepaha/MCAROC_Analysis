using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>
/// Regression suite validating the hand-labelled Coastal Projects litigation order outcome test fixture
/// (<c>Fixtures/litigation_order_outcomes_coastal_sample.json</c>) for Epic #195 (#337).
///
/// Verifies schema completeness, 6-forum representation, enum compatibility with <see cref="LitigationOrderOutcome"/>,
/// strict negation semantics (StayVacated vs StayGranted), exact fine amount relationships, and grounded evidence references.
/// </summary>
public sealed class LitigationOrderOutcomeFixtureTests
{
    public sealed class LitigationOrderOutcomeSample
    {
        public int SampleId { get; set; }
        public string CourtForum { get; set; } = string.Empty;
        public string CaseNumber { get; set; } = string.Empty;
        public string OrderFileName { get; set; } = string.Empty;
        public string? OrderDate { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public int PageCount { get; set; }
        public string TextExtractionQuality { get; set; } = string.Empty;
        public int CorpusDuplicateCount { get; set; }
        public List<string> ExpectedOutcomes { get; set; } = [];
        public decimal? ExpectedFineAmount { get; set; }
        public string? ExpectedConfidence { get; set; }
        public string? OperativeExcerpt { get; set; }
        public string? TaxonomyNotes { get; set; }
        public List<EvidenceReferenceSample> EvidenceReferences { get; set; } = [];
    }

    public sealed class EvidenceReferenceSample
    {
        public int PageNumber { get; set; }
        public string Excerpt { get; set; } = string.Empty;
    }

    private static string GetFixturePath()
    {
        var localBin = Path.Combine(AppContext.BaseDirectory, "Fixtures", "litigation_order_outcomes_coastal_sample.json");
        if (File.Exists(localBin)) return localBin;

        var sourceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "litigation_order_outcomes_coastal_sample.json"));
        if (File.Exists(sourceDir)) return sourceDir;

        return localBin;
    }

    private static List<LitigationOrderOutcomeSample> LoadFixture()
    {
        var path = GetFixturePath();
        Assert.True(File.Exists(path), $"Fixture file not found at '{path}'");

        var json = File.ReadAllText(path);
        var samples = JsonSerializer.Deserialize<List<LitigationOrderOutcomeSample>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(samples);
        return samples!;
    }

    [Fact]
    public void Fixture_LoadsSuccessfully_AndHasCompleteCoverageAcrossAllForums()
    {
        var samples = LoadFixture();

        // Exactly 66 curated orders across 6 judicial/administrative forums
        Assert.Equal(66, samples.Count);

        // Assert strictly distinct, sequential 1-based SampleIds
        var sampleIds = samples.Select(s => s.SampleId).ToList();
        Assert.Equal(66, sampleIds.Distinct().Count());
        Assert.Equal(1, sampleIds.Min());
        Assert.Equal(66, sampleIds.Max());

        // Check forum distribution
        var forumCounts = samples.GroupBy(s => s.CourtForum).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(20, forumCounts.GetValueOrDefault("High Court"));
        Assert.Equal(14, forumCounts.GetValueOrDefault("District Court"));
        Assert.Equal(12, forumCounts.GetValueOrDefault("Supreme Court"));
        Assert.Equal(10, forumCounts.GetValueOrDefault("NCLT"));
        Assert.Equal(8, forumCounts.GetValueOrDefault("NCLAT"));
        Assert.Equal(2, forumCounts.GetValueOrDefault("CESTAT"));

        // Validate basic field non-emptiness and integrity
        foreach (var sample in samples)
        {
            Assert.False(string.IsNullOrWhiteSpace(sample.CourtForum), $"Sample {sample.SampleId} missing CourtForum");
            Assert.False(string.IsNullOrWhiteSpace(sample.CaseNumber), $"Sample {sample.SampleId} missing CaseNumber");
            Assert.False(string.IsNullOrWhiteSpace(sample.OrderFileName), $"Sample {sample.SampleId} missing OrderFileName");
            if (sample.TextExtractionQuality != "CrawlerWebArtifact" || sample.SampleId != 66)
            {
                Assert.False(string.IsNullOrWhiteSpace(sample.OrderDate), $"Sample {sample.SampleId} missing OrderDate");
            }
            Assert.True(sample.Sha256.Length == 64, $"Sample {sample.SampleId} Sha256 length is {sample.Sha256.Length}, expected 64");
            Assert.True(sample.PageCount >= 1, $"Sample {sample.SampleId} PageCount is {sample.PageCount}, expected >= 1");
            Assert.False(string.IsNullOrWhiteSpace(sample.TextExtractionQuality), $"Sample {sample.SampleId} missing TextExtractionQuality");
        }
    }

    [Fact]
    public void Fixture_AllOutcomes_MapToValidLitigationOrderOutcomeEnumValues()
    {
        var samples = LoadFixture();
        var allObservedOutcomes = new HashSet<LitigationOrderOutcome>();

        foreach (var sample in samples)
        {
            foreach (var outcomeStr in sample.ExpectedOutcomes)
            {
                var parsed = Enum.TryParse<LitigationOrderOutcome>(outcomeStr, false, out var outcome);
                Assert.True(parsed, $"Sample {sample.SampleId} has unknown outcome '{outcomeStr}'");
                allObservedOutcomes.Add(outcome);
            }

            if (sample.ExpectedConfidence != null)
            {
                var parsedConf = Enum.TryParse<ClassificationConfidence>(sample.ExpectedConfidence, false, out _);
                Assert.True(parsedConf, $"Sample {sample.SampleId} has invalid confidence '{sample.ExpectedConfidence}'");
            }
        }

        // Assert all 9 domain outcomes in the LitigationOrderOutcome taxonomy are exercised in the fixture
        var allDefinedOutcomes = Enum.GetValues<LitigationOrderOutcome>();
        foreach (var definedOutcome in allDefinedOutcomes)
        {
            Assert.Contains(definedOutcome, allObservedOutcomes);
        }

        // Crawler artifacts (CESTAT samples 65, 66) must have null ExpectedConfidence and 0 outcomes
        var crawlerArtifacts = samples.Where(s => s.TextExtractionQuality == "CrawlerWebArtifact").ToList();
        Assert.Equal(2, crawlerArtifacts.Count);
        foreach (var artifact in crawlerArtifacts)
        {
            Assert.Empty(artifact.ExpectedOutcomes);
            Assert.Null(artifact.ExpectedConfidence);
        }
    }

    [Fact]
    public void Fixture_FinePenalty_ConsistentWithExpectedFineAmount()
    {
        var samples = LoadFixture();

        var fineSamples = samples.Where(s => s.ExpectedFineAmount.HasValue).ToList();
        Assert.Equal(9, fineSamples.Count);

        // Every sample with an ExpectedFineAmount must declare FinePenalty
        foreach (var sample in fineSamples)
        {
            Assert.True(sample.ExpectedFineAmount > 0, $"Sample {sample.SampleId} fine amount must be > 0");
            Assert.Contains(nameof(LitigationOrderOutcome.FinePenalty), sample.ExpectedOutcomes);
        }

        // Conversely, every sample declaring FinePenalty must specify ExpectedFineAmount
        var penaltySamples = samples.Where(s => s.ExpectedOutcomes.Contains(nameof(LitigationOrderOutcome.FinePenalty))).ToList();
        Assert.Equal(9, penaltySamples.Count);
        foreach (var sample in penaltySamples)
        {
            Assert.NotNull(sample.ExpectedFineAmount);
        }

        // Specific sample validation across forums:
        // Sample 25 (High Court): ₹500
        Assert.Equal(500m, samples.Single(s => s.SampleId == 25).ExpectedFineAmount);
        // Sample 40 (District Court): ₹1,000
        Assert.Equal(1000m, samples.Single(s => s.SampleId == 40).ExpectedFineAmount);
        // Sample 23 (High Court): ₹5,000
        Assert.Equal(5000m, samples.Single(s => s.SampleId == 23).ExpectedFineAmount);
        // Sample 39 (District Court): ₹10,000
        Assert.Equal(10000m, samples.Single(s => s.SampleId == 39).ExpectedFineAmount);
    }

    [Fact]
    public void Fixture_StrictNegationCases_StayVacatedAndStayGranted_MutuallyExclusive()
    {
        var samples = LoadFixture();

        // Strict negation requirement: StayGranted and StayVacated cannot both be outcomes on the same relief
        foreach (var sample in samples)
        {
            var hasStayGranted = sample.ExpectedOutcomes.Contains(nameof(LitigationOrderOutcome.StayGranted));
            var hasStayVacated = sample.ExpectedOutcomes.Contains(nameof(LitigationOrderOutcome.StayVacated));

            Assert.False(hasStayGranted && hasStayVacated,
                $"Sample {sample.SampleId} contains both StayGranted and StayVacated simultaneously");
        }

        // Assert known StayVacated samples exist (samples 19, 20, 21, 22 in High Court)
        var stayVacatedSamples = samples.Where(s => s.ExpectedOutcomes.Contains(nameof(LitigationOrderOutcome.StayVacated))).ToList();
        Assert.Equal(4, stayVacatedSamples.Count);
        Assert.All(stayVacatedSamples, s => Assert.Equal("High Court", s.CourtForum));

        // Assert known positive StayGranted samples exist (7 samples across High Court, District Court, Supreme Court)
        var stayGrantedSamples = samples.Where(s => s.ExpectedOutcomes.Contains(nameof(LitigationOrderOutcome.StayGranted))).ToList();
        Assert.Equal(7, stayGrantedSamples.Count);
    }

    [Fact]
    public void Fixture_EvidenceReferences_AreValidAndGrounded()
    {
        var samples = LoadFixture();

        foreach (var sample in samples)
        {
            // Valid judicial orders with outcomes must have at least one grounded evidence reference
            if (sample.ExpectedOutcomes.Count > 0)
            {
                Assert.NotEmpty(sample.EvidenceReferences);
                Assert.False(string.IsNullOrWhiteSpace(sample.OperativeExcerpt),
                    $"Sample {sample.SampleId} has outcomes but no OperativeExcerpt");

                foreach (var ev in sample.EvidenceReferences)
                {
                    Assert.InRange(ev.PageNumber, 1, sample.PageCount);
                    Assert.False(string.IsNullOrWhiteSpace(ev.Excerpt),
                        $"Sample {sample.SampleId} has empty evidence excerpt on page {ev.PageNumber}");
                }
            }
            else
            {
                // Crawler artifacts have no judicial outcomes and no evidence references
                Assert.Empty(sample.EvidenceReferences);
            }
        }
    }
}
