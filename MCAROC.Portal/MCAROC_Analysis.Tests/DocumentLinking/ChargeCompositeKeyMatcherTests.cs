using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public class ChargeCompositeKeyMatcherTests
{
    private readonly ChargeCompositeKeyMatcher _matcher = new();

    [Fact]
    public void Match_Distinguishes_EventDate_From_FilingDate_When_Both_Differ()
    {
        // Arrange
        // Grounded in Coastal pilot: EventDate = 26 Dec 2016, FilingDate = 2 Mar 2017
        var charge = new RocCharge
        {
            ChargeId = 101,
            RocChargeNumber = "101265730",
            LatestChargeHolderNormalized = "STATE BANK OF INDIA",
            CurrentAmount = 50000000m
        };

        var chargeEvent = new RocChargeEvent
        {
            ChargeEventId = 201,
            RocChargeId = 101,
            EventType = ChargeEventType.Modification,
            EventDate = new DateOnly(2016, 12, 26),
            FilingDate = new DateOnly(2017, 3, 2),
            ChargeAmount = 50000000m,
            HolderNameNormalized = "STATE BANK OF INDIA"
        };

        var candidateMatchingEventDate = new ChargeDocumentCandidate
        {
            ChargeId = "101265730",
            EventType = ChargeEventType.Modification,
            EventDate = new DateOnly(2016, 12, 26),
            FilingDate = null, // Document only recorded instrument execution date
            Amount = 50000000m,
            HolderName = "STATE BANK OF INDIA"
        };

        var candidateMatchingFilingDate = new ChargeDocumentCandidate
        {
            ChargeId = "101265730",
            EventType = ChargeEventType.Modification,
            EventDate = null,
            FilingDate = new DateOnly(2017, 3, 2), // Document only recorded ROC filing date
            Amount = 50000000m,
            HolderName = "STATE BANK OF INDIA"
        };

        var candidateMatchingBothDates = new ChargeDocumentCandidate
        {
            ChargeId = "101265730",
            EventType = ChargeEventType.Modification,
            EventDate = new DateOnly(2016, 12, 26),
            FilingDate = new DateOnly(2017, 3, 2),
            Amount = 50000000m,
            HolderName = "STATE BANK OF INDIA"
        };

        // Act
        var resultEventDate = _matcher.Match(candidateMatchingEventDate, [charge], [chargeEvent]);
        var resultFilingDate = _matcher.Match(candidateMatchingFilingDate, [charge], [chargeEvent]);
        var resultBothDates = _matcher.Match(candidateMatchingBothDates, [charge], [chargeEvent]);

        // Assert
        Assert.True(resultEventDate.IsMatched);
        Assert.Equal(ChargeDateMatchMode.EventDateMatched, resultEventDate.DateMatchMode);
        Assert.Equal(201, resultEventDate.TargetChargeEventId);
        Assert.Contains("EventDateMatched", resultEventDate.EvidenceJson);

        Assert.True(resultFilingDate.IsMatched);
        Assert.Equal(ChargeDateMatchMode.FilingDateMatched, resultFilingDate.DateMatchMode);
        Assert.Equal(201, resultFilingDate.TargetChargeEventId);
        Assert.Contains("FilingDateMatched", resultFilingDate.EvidenceJson);

        Assert.True(resultBothDates.IsMatched);
        Assert.Equal(ChargeDateMatchMode.BothDatesMatched, resultBothDates.DateMatchMode);
        Assert.Equal(201, resultBothDates.TargetChargeEventId);
        Assert.Equal(DocumentLinkConfidence.High, resultBothDates.Confidence);
    }

    [Fact]
    public void Match_Strictly_Ignores_Srn_On_Candidate_Charge_Side()
    {
        // Arrange: RocCharge and RocChargeEvent have no SRN.
        // Document candidate may carry an SRN from parent McaFiling, but matcher must ignore it completely.
        var charge = new RocCharge
        {
            ChargeId = 102,
            RocChargeNumber = "100999888",
            LatestChargeHolderNormalized = "PUNJAB NATIONAL BANK",
            CurrentAmount = 10000000m
        };

        var chargeEvent = new RocChargeEvent
        {
            ChargeEventId = 202,
            RocChargeId = 102,
            EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2015, 5, 10),
            FilingDate = new DateOnly(2015, 6, 1),
            ChargeAmount = 10000000m,
            HolderNameNormalized = "PUNJAB NATIONAL BANK"
        };

        var candidateWithSrn = new ChargeDocumentCandidate
        {
            ChargeId = "100999888",
            EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2015, 5, 10),
            DocumentSrn = "G12345678", // Arbitrary filing SRN
            Amount = 10000000m,
            HolderName = "PUNJAB NATIONAL BANK"
        };

        var candidateWithoutSrn = new ChargeDocumentCandidate
        {
            ChargeId = "100999888",
            EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2015, 5, 10),
            DocumentSrn = null,
            Amount = 10000000m,
            HolderName = "PUNJAB NATIONAL BANK"
        };

        // Act
        var resultWith = _matcher.Match(candidateWithSrn, [charge], [chargeEvent]);
        var resultWithout = _matcher.Match(candidateWithoutSrn, [charge], [chargeEvent]);

        // Assert: Results are identical regardless of whether SRN is present or absent
        Assert.True(resultWith.IsMatched);
        Assert.True(resultWithout.IsMatched);
        Assert.Equal(resultWith.TargetChargeEventId, resultWithout.TargetChargeEventId);
        Assert.Equal(resultWith.DateMatchMode, resultWithout.DateMatchMode);
    }

    [Fact]
    public void Match_Returns_Unmatched_When_Dates_And_Corroboration_Do_Not_Match()
    {
        // Arrange
        var charge = new RocCharge
        {
            ChargeId = 103,
            RocChargeNumber = "10555444",
            LatestChargeHolderNormalized = "BANK OF BARODA",
            CurrentAmount = 2500000m
        };

        var chargeEvent = new RocChargeEvent
        {
            ChargeEventId = 203,
            RocChargeId = 103,
            EventType = ChargeEventType.Satisfaction,
            EventDate = new DateOnly(2018, 1, 15),
            FilingDate = new DateOnly(2018, 2, 20),
            ChargeAmount = 2500000m,
            HolderNameNormalized = "BANK OF BARODA"
        };

        var candidateMismatchedDate = new ChargeDocumentCandidate
        {
            ChargeId = "10555444",
            EventType = ChargeEventType.Satisfaction,
            EventDate = new DateOnly(2019, 1, 1), // Non-matching date
            FilingDate = new DateOnly(2019, 2, 1)
        };

        // Act
        var result = _matcher.Match(candidateMismatchedDate, [charge], [chargeEvent]);

        // Assert
        Assert.False(result.IsMatched);
        Assert.Contains("No matching RocChargeEvent found", result.FailureReason);
    }
}
