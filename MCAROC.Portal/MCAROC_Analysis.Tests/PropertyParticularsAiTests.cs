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
    private const string UnitClause =
        "All that piece or parcel of premises admeasuring about 27864.63 sq ft equivalent to 2588.68 sq m of carpet area bearing Unit No. 5c on the " +
        "5th Floor in the building known as Godrej One along with 43 car parking spaces, comprised in New C.T.S No.51/B and Old C.T.S. Nos 52/1 to 17, " +
        "situated at Pirojshanagar, Vikhroli, Taluka Kurla, Mumbai - 400079.";

    private const string AssetsClause = "Hypothecation of current assets of Godrej Real Estate Private Limited.";

    private const string Source = UnitClause + " " + AssetsClause;

    private static readonly string Grounded = $$"""
        {"properties":[
          {"sourceText":"{{UnitClause}}","assetClass":"Immovable","kind":"Premises","owner":null,"unitNumber":"5c","floor":"5th","building":"Godrej One","project":null,
           "areas":[{"value":27864.63,"unit":"SqFt","basis":"Carpet","equivalentValue":2588.68,"equivalentUnit":"SqM"}],"parkingSpaces":43,
           "surveyNumbers":[{"scheme":"CTS","qualifier":"New","numbers":["51/B"]},{"scheme":"CTS","qualifier":"Old","numbers":["52/1","52/17"]}],
           "localities":["Pirojshanagar","Vikhroli"],"village":null,"taluka":"Kurla","district":null,"city":"Mumbai","state":null,"pin":"400079"},
          {"sourceText":"{{AssetsClause}}","assetClass":"Movable","kind":"CurrentAssets","owner":"Godrej Real Estate Private Limited","areas":[],"surveyNumbers":[],"localities":[]}
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
        Assert.Equal(["52/1", "52/17"], unit.SurveyNumbers[1].Numbers); // "52/1 to 17" is an explicitly stated range
        Assert.Equal(new AiArea(27864.63m, AiAreaUnit.SqFt, AreaBasis.Carpet, 2588.68m, AiAreaUnit.SqM), Assert.Single(unit.Areas));
        Assert.Equal(AssetsClause, v.Result.Properties[1].SourceText);
        Assert.Equal("Godrej Real Estate Private Limited", v.Result.Properties[1].Owner);
    }

    [Fact]
    public void InventedOrConvertedValues_AreDropped_AndRecorded_NeverKept()
    {
        var v = PropertyParticularsAi.Validate($$"""
            {"properties":[{"sourceText":"{{UnitClause}}","assetClass":"Immovable","kind":"Premises","unitNumber":"7B","building":"Godrej Two","city":"Pune","pin":"411001",
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

    // ── Review blocker on PR #333: the exact executed counterexample, and property-to-property swaps ─────────

    private const string ReviewSource = "Mortgage of CTS 52/1 with 17 parking spaces and a carpet area of 100 sq m.";

    [Fact]
    public void ReviewCounterexample_AsSent_IsRejectedWithARecordedReason()
    {
        // The reviewer's exact response: no clause quoted, an invented suffix, the right number in the wrong unit, and a
        // parking count borrowed from the area.
        var v = PropertyParticularsAi.Validate("""
            {"properties":[{"assetClass":"Immovable","kind":"Premises","surveyNumbers":[{"scheme":"CTS","numbers":["52/17XYZ"]}],"areas":[{"value":100,"unit":"Acre","basis":"Carpet"}],"parkingSpaces":100}]}
            """, ReviewSource);

        Assert.False(v.IsAccepted);
        Assert.Null(v.Result);
        Assert.Contains(v.RejectedFields, r => r.Contains("sourceText is not a verbatim quote"));
    }

    [Fact]
    public void ReviewCounterexample_WithItsClauseQuoted_DropsEachFabricatedField_AndKeepsTheStatedOnes()
    {
        var v = PropertyParticularsAi.Validate($$"""
            {"properties":[{"sourceText":"{{ReviewSource}}","assetClass":"Immovable","kind":"Premises",
              "surveyNumbers":[{"scheme":"CTS","numbers":["52/17XYZ","52/17","52/1"]}],
              "areas":[{"value":100,"unit":"Acre","basis":"Carpet"},{"value":100,"unit":"SqM","basis":"Carpet"}],
              "parkingSpaces":100}]}
            """, ReviewSource);

        var p = Assert.Single(v.Result!.Properties);
        Assert.Equal(["52/1"], Assert.Single(p.SurveyNumbers).Numbers);           // suffix and unstated range both rejected
        Assert.Equal(new AiArea(100m, AiAreaUnit.SqM, AreaBasis.Carpet, null, null), Assert.Single(p.Areas)); // only the stated unit
        Assert.Null(p.ParkingSpaces);                                           // 100 is an area, not a parking count
        Assert.Contains(v.RejectedFields, r => r.Contains("'52/17XYZ'"));
        Assert.Contains(v.RejectedFields, r => r.Contains("'52/17'"));
        Assert.Contains(v.RejectedFields, r => r.Contains("100 Acre"));
        Assert.Contains(v.RejectedFields, r => r.Contains("parkingSpaces: 100"));
        Assert.Equal(4, v.RejectedFields.Count);
    }

    [Fact]
    public void StatedParkingCount_IsAccepted_ButANumberFromAnotherFieldIsNot()
    {
        var v = PropertyParticularsAi.Validate($$"""
            {"properties":[{"sourceText":"{{ReviewSource}}","assetClass":"Immovable","kind":"Premises","parkingSpaces":17}]}
            """, ReviewSource);
        Assert.Equal(17, Assert.Single(v.Result!.Properties).ParkingSpaces);

        // "B-43" is a space number, not a count of 43.
        const string spaces = "Unit No. 5 along with car parking spaces bearing Nos. B-43 to B-49.";
        var borrowed = PropertyParticularsAi.Validate($$"""
            {"properties":[{"sourceText":"{{spaces}}","assetClass":"Immovable","kind":"Premises","parkingSpaces":43}]}
            """, spaces);
        Assert.Null(Assert.Single(borrowed.Result!.Properties).ParkingSpaces);
    }

    private const string TwoUnitsA = "Unit No. 5C with 10 car parking spaces and a carpet area of 100 sq m;";
    private const string TwoUnitsB = "and Unit No. 7D with 20 car parking spaces and a carpet area of 200 sq ft.";
    private const string TwoUnits = TwoUnitsA + " " + TwoUnitsB;

    [Fact]
    public void TwoPropertiesInOneParagraph_CannotTradeUnitsAreasOrParking()
    {
        // Property A quotes its own clause but claims B's unit, B's parking count and B's area (in B's unit).
        var v = PropertyParticularsAi.Validate($$"""
            {"properties":[
              {"sourceText":"{{TwoUnitsA}}","assetClass":"Immovable","kind":"Premises","unitNumber":"7D","parkingSpaces":20,
               "areas":[{"value":200,"unit":"SqFt","basis":"Carpet"}]},
              {"sourceText":"{{TwoUnitsB}}","assetClass":"Immovable","kind":"Premises","unitNumber":"7D","parkingSpaces":20,
               "areas":[{"value":200,"unit":"SqFt","basis":"Carpet"}]}]}
            """, TwoUnits);

        var (a, b) = (v.Result!.Properties[0], v.Result.Properties[1]);
        Assert.Null(a.UnitNumber);
        Assert.Null(a.ParkingSpaces);
        Assert.Empty(a.Areas);
        Assert.Equal("7D", b.UnitNumber);
        Assert.Equal(20, b.ParkingSpaces);
        Assert.Equal(200m, Assert.Single(b.Areas).Value);
        Assert.Equal(3, v.RejectedFields.Count);
        Assert.All(v.RejectedFields, r => Assert.Contains("properties[0]", r));
    }

    [Fact]
    public void A_Property_Quoting_Another_Propertys_Clause_IsDropped()
    {
        // Quoting the whole paragraph for the second property would let it ground anything — overlapping clauses are refused.
        var v = PropertyParticularsAi.Validate($$"""
            {"properties":[
              {"sourceText":"{{TwoUnitsA}}","assetClass":"Immovable","kind":"Premises","unitNumber":"5C"},
              {"sourceText":"{{TwoUnits}}","assetClass":"Immovable","kind":"Premises","unitNumber":"5C","parkingSpaces":10}]}
            """, TwoUnits);

        Assert.Equal("5C", Assert.Single(v.Result!.Properties).UnitNumber);
        Assert.Contains(v.RejectedFields, r => r.Contains("properties[1]: sourceText overlaps"));
    }

    [Theory]
    [InlineData("Plot 75 Church Road, Juhu", "5C")]      // tail of "75" + head of "Church" is not unit "5C"
    [InlineData("Unit No. 5 c on the ground floor", "5C")] // genuine: "5 c" written with a space
    public void UnitNumbers_MustAlignToWholeWords(string clause, string unit)
    {
        var v = PropertyParticularsAi.Validate($$"""
            {"properties":[{"sourceText":"{{clause}}","assetClass":"Immovable","kind":"Premises","unitNumber":"{{unit}}"}]}
            """, clause);

        var expectGrounded = clause.StartsWith("Unit", StringComparison.Ordinal);
        Assert.Equal(expectGrounded ? "5C" : null, Assert.Single(v.Result!.Properties).UnitNumber);
    }

    [Fact]
    public void EquivalentArea_MustBeStatedInItsOwnUnit()
    {
        var v = PropertyParticularsAi.Validate($$"""
            {"properties":[{"sourceText":"{{UnitClause}}","assetClass":"Immovable","kind":"Premises",
              "areas":[{"value":27864.63,"unit":"SqFt","basis":"Carpet","equivalentValue":2588.68,"equivalentUnit":"Acre"}]}]}
            """, Source);

        var area = Assert.Single(Assert.Single(v.Result!.Properties).Areas);
        Assert.Null(area.EquivalentValue);
        Assert.Contains(v.RejectedFields, r => r.Contains("equivalent 2588.68 Acre"));
    }
}
