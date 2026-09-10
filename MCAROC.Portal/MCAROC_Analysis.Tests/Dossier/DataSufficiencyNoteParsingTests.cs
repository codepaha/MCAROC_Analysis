using MCAROC_Analysis.Services.Dossier;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>`AnalysisRun.DataSufficiencyNotesJson` is written by the rule engine, but a partially
/// corrupt payload must never take down dossier assembly (D12). Every malformed shape is skipped;
/// whatever valid <c>{code, reason}</c> objects remain are still returned.</summary>
public class DataSufficiencyNoteParsingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"code\":\"X\",\"reason\":\"Y\"}")]   // object root, not an array
    [InlineData("\"a bare string\"")]
    [InlineData("42")]
    [InlineData("[]")]
    public void Returns_empty_for_missing_unparseable_or_non_array_input(string? json)
    {
        Assert.Empty(DossierAssembler.DeserializeSufficiencyNotes(json));
    }

    [Fact]
    public void Skips_non_object_array_entries_and_keeps_the_valid_ones()
    {
        const string json = """
        [
          "a bare string",
          42,
          null,
          [1, 2, 3],
          { "code": "GST_FILING", "reason": "No GST filing history was present." },
          { "code": 7, "reason": "Numeric code is ignored, reason kept." },
          { "reason": "A note with no code is still valid." }
        ]
        """;

        var notes = DossierAssembler.DeserializeSufficiencyNotes(json);

        Assert.Equal(3, notes.Count);
        Assert.Equal("GST_FILING", notes[0].Code);
        Assert.Equal("No GST filing history was present.", notes[0].Reason);
        Assert.Equal("", notes[1].Code);                                   // non-string code → empty
        Assert.Equal("Numeric code is ignored, reason kept.", notes[1].Reason);
        Assert.Equal("", notes[2].Code);
    }

    [Theory]
    [InlineData("[ { \"code\": \"X\", \"reason\": \"\" } ]")]              // blank reason
    [InlineData("[ { \"code\": \"X\", \"reason\": \"   \" } ]")]           // whitespace reason
    [InlineData("[ { \"code\": \"X\" } ]")]                                // reason missing
    [InlineData("[ { \"code\": \"X\", \"reason\": null } ]")]              // reason null
    public void Rejects_an_entry_with_no_usable_reason(string json)
    {
        Assert.Empty(DossierAssembler.DeserializeSufficiencyNotes(json));
    }

    [Fact]
    public void Trims_surrounding_whitespace_on_code_and_reason()
    {
        var notes = DossierAssembler.DeserializeSufficiencyNotes(
            "[ { \"code\": \"  FIN_LEV  \", \"reason\": \"  only one FY on record  \" } ]");

        var note = Assert.Single(notes);
        Assert.Equal("FIN_LEV", note.Code);
        Assert.Equal("only one FY on record", note.Reason);
    }
}
