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
    public void Skips_every_entry_that_does_not_honour_the_code_reason_contract()
    {
        const string json = """
        [
          "a bare string",
          42,
          null,
          [1, 2, 3],
          { "code": "GST_FILING", "reason": "No GST filing history was present." },
          { "code": 7, "reason": "Non-string code — dropped (no rule identity)." },
          { "reason": "A note with no code — dropped (renders as '()')." },
          { "code": "X" }
        ]
        """;

        var notes = DossierAssembler.DeserializeSufficiencyNotes(json);

        // Only the one entry with both a non-empty code and a non-empty reason survives.
        var note = Assert.Single(notes);
        Assert.Equal("GST_FILING", note.Code);
        Assert.Equal("No GST filing history was present.", note.Reason);
    }

    [Theory]
    [InlineData("[ { \"code\": \"X\", \"reason\": \"\" } ]")]              // blank reason
    [InlineData("[ { \"code\": \"X\", \"reason\": \"   \" } ]")]           // whitespace reason
    [InlineData("[ { \"code\": \"X\" } ]")]                                // reason missing
    [InlineData("[ { \"code\": \"X\", \"reason\": null } ]")]              // reason null
    [InlineData("[ { \"reason\": \"Y\" } ]")]                              // code missing
    [InlineData("[ { \"code\": \"\", \"reason\": \"Y\" } ]")]              // blank code
    [InlineData("[ { \"code\": \"   \", \"reason\": \"Y\" } ]")]           // whitespace code
    [InlineData("[ { \"code\": 7, \"reason\": \"Y\" } ]")]                 // non-string code
    [InlineData("[ { \"code\": null, \"reason\": \"Y\" } ]")]              // null code
    public void Rejects_an_entry_missing_a_non_empty_code_or_reason(string json)
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
