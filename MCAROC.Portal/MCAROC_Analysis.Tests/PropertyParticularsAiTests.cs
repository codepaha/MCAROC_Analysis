using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.PropertyParticulars;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>The Gemini extraction's grounding validator and the reading that chooses between AI and rules — no model,
/// no database. The core guarantee under test: a value the model returns that is not in the source text never
/// survives validation.</summary>
public class PropertyParticularsAiTests
{
    private const string Source =
        "All that piece or parcel of premises admeasuring about 27864.63 sq ft equivalent to 2588.68 sq m of carpet area bearing Unit No. 5c on the " +
        "5th Floor in the building known as Godrej One along with 43 car parking spaces, comprised in New C.T.S No.51/B and Old C.T.S. Nos 52/1 to 17, " +
        "situated at Pirojshanagar, Vikhroli, Taluka Kurla, Mumbai - 400079. Hypothecation of current assets of Godrej Real Estate Private Limited.";

    private const string Grounded = """
        {"properties":[
          {"assetClass":"Immovable","kind":"Premises","owner":null,"unitNumber":"5c","floor":"5th","building":"Godrej One","project":null,
           "areas":[{"value":27864.63,"unit":"SqFt","basis":"Carpet","equivalentValue":2588.68,"equivalentUnit":"SqM"}],"parkingSpaces":43,
           "surveyNumbers":[{"scheme":"CTS","qualifier":"New","numbers":["51/B"]},{"scheme":"CTS","qualifier":"Old","numbers":["52/1","52/17"]}],
           "localities":["Pirojshanagar","Vikhroli"],"village":null,"taluka":"Kurla","district":null,"city":"Mumbai","state":null,"pin":"400079"},
          {"assetClass":"Movable","kind":"CurrentAssets","owner":"Godrej Real Estate Private Limited","areas":[],"surveyNumbers":[],"localities":[]}
        ]}
        """;

    [Fact]
    public void GroundedResponse_IsAccepted_SplitIntoProperties()
    {
        var v = PropertyParticularsAi.Validate(Grounded, Source);

        Assert.True(v.IsAccepted);
        Assert.Empty(v.RejectedFields);
        Assert.Equal(2, v.Result!.Properties.Count);
        var unit = v.Result.Properties[0];
        Assert.Equal("5C", unit.UnitNumber);
        Assert.Equal(43, unit.ParkingSpaces);
        Assert.Equal("400079", unit.Pin);
        Assert.Equal(["52/1", "52/17"], unit.SurveyNumbers[1].Numbers); // expanded range: each digit group is in the source
        Assert.Equal("Godrej Real Estate Private Limited", v.Result.Properties[1].Owner);
    }

    [Fact]
    public void InventedOrConvertedValues_AreDropped_AndRecorded_NeverKept()
    {
        var v = PropertyParticularsAi.Validate("""
            {"properties":[{"assetClass":"Immovable","kind":"Premises","unitNumber":"7B","building":"Godrej Two","city":"Pune","pin":"411001",
              "areas":[{"value":2589,"unit":"SqM","basis":"Carpet"}],"parkingSpaces":44,
              "surveyNumbers":[{"scheme":"CTS","qualifier":"New","numbers":["99/Z","51/B"]}],"localities":["Powai","Vikhroli"]}]}
            """, Source);

        Assert.True(v.IsAccepted); // the property itself is real; only its invented fields go
        var p = Assert.Single(v.Result!.Properties);
        Assert.Null(p.UnitNumber);
        Assert.Null(p.Building);
        Assert.Null(p.City);
        Assert.Null(p.Pin);
        Assert.Empty(p.Areas); // 2589 is a rounded conversion, not what the text says
        Assert.Null(p.ParkingSpaces);
        Assert.Equal(["51/B"], Assert.Single(p.SurveyNumbers).Numbers);
        Assert.Equal(["Vikhroli"], p.Localities);
        Assert.Equal(8, v.RejectedFields.Count);
        Assert.Contains(v.RejectedFields, r => r.Contains("'Pune' not in source"));
    }

    [Theory]
    [InlineData("not json", "Invalid JSON")]
    [InlineData("""{"items":[]}""", "no properties")]
    [InlineData("""{"properties":[{"assetClass":"3","kind":"Premises"}]}""", "survived")]
    [InlineData("""{"properties":[{"assetClass":"Immovable","kind":"Castle"}]}""", "survived")]
    public void UnusableResponses_Fail(string raw, string reason)
    {
        var v = PropertyParticularsAi.Validate(raw, Source);

        Assert.False(v.IsAccepted);
        Assert.Contains(reason, v.FailureReason);
    }

    [Fact]
    public void TooManyProperties_Fail()
    {
        var many = "{\"properties\":[" + string.Join(",", Enumerable.Repeat("""{"assetClass":"Movable","kind":"CurrentAssets"}""", PropertyParticularsAi.MaxProperties + 1)) + "]}";

        Assert.False(PropertyParticularsAi.Validate(many, Source).IsAccepted);
    }

    [Fact]
    public void Hash_IgnoresWhitespaceAndLineBreaks_ButNotWording()
    {
        Assert.Equal(PropertyParticularsAi.HashOf("Unit No. 5c\non the  5th Floor", "Immovable"),
            PropertyParticularsAi.HashOf("Unit No. 5c on the 5th Floor ", "Immovable"));
        Assert.NotEqual(PropertyParticularsAi.HashOf("Unit No. 5c", "Immovable"), PropertyParticularsAi.HashOf("Unit No. 6c", "Immovable"));
        Assert.NotEqual(PropertyParticularsAi.HashOf("Unit No. 5c", "Immovable"), PropertyParticularsAi.HashOf("Unit No. 5c", "Movable"));
    }

    [Fact]
    public void Reading_PrefersACompletedAiExtraction_AndUsesStatedAreas()
    {
        var result = PropertyParticularsAi.Validate(Grounded, Source).Result!;
        var extractions = new Dictionary<string, PropertyParticularsExtraction>
        {
            [PropertyParticularsAi.HashOf(Source, "Immovable")] = new()
            {
                Status = PropertyParticularsExtractionStatus.Completed, ExtractionJson = PropertyParticularsAi.Serialize(result),
                RejectedFieldsJson = """["x"]"""
            }
        };

        var reading = PropertyReading.For(Source, "Immovable", extractions);

        Assert.Equal(PropertyReadingSource.Ai, reading.Source);
        Assert.Equal(2, reading.Items.Count);
        Assert.Equal(1, reading.RejectedFieldCount);
        Assert.Equal(new NormalizedArea(2588.68m, 27864.63m, AreaBasis.Carpet), Assert.Single(reading.Items[0].Areas));
        Assert.Equal(["Unit 5C, 5th floor, Godrej One"], reading.Items[0].UnitLines);
        Assert.Contains("of Godrej Real Estate Private Limited", PropertyReading.Summarize(reading.Items[1]));
    }

    [Theory]
    [InlineData(PropertyParticularsExtractionStatus.Pending)]
    [InlineData(PropertyParticularsExtractionStatus.Failed)]
    public void Reading_FallsBackToRules_WhenThereIsNoCompletedExtraction(PropertyParticularsExtractionStatus status)
    {
        var extractions = new Dictionary<string, PropertyParticularsExtraction>
        {
            [PropertyParticularsAi.HashOf(Source, "Immovable")] = new() { Status = status }
        };

        var reading = PropertyReading.For(Source, "Immovable", extractions);

        Assert.Equal(PropertyReadingSource.Rules, reading.Source);
        Assert.Single(reading.Items);
        Assert.Equal(PropertyReadingSource.Rules, PropertyReading.For(Source, "Immovable", null).Source);
    }
}
