using System.Text.Json;
using MCAROC_Analysis.Services.Excel;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class SourceRowRecorderTests
{
    private static readonly DateTime At = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Records_every_non_blank_row_verbatim_and_skips_reader_padding()
    {
        var wb = new[]
        {
            Sheet("Directors",
                Row("NAME", "DIN", "DESIGNATION"),
                Row("RAMESH VENKATARAMAN", 8234561.0, "Managing Director"),
                Row(null, null, null),                 // reader padding
                Row("PRIYA SUBRAMANIAM", "07981245", "Director")),
        };

        var rows = SourceRowRecorder.Record(wb, "RocReport", requestId: 7, ingestionRunId: 3,
            sourceDocumentId: 11, extractedAt: At);

        Assert.Equal(3, rows.Count); // header + 2 data rows; the all-null row is dropped
        Assert.Equal([1, 2, 4], rows.Select(r => r.RowNumber).ToArray()); // 1-based, padding row 3 skipped
        Assert.All(rows, r =>
        {
            Assert.Equal("RocReport", r.WorkbookRole);
            Assert.Equal("Directors", r.SheetName);
            Assert.Equal(0, r.SheetIndex);
            Assert.Equal(7, r.RequestId);
            Assert.Equal(3, r.IngestionRunId);
            Assert.Equal(11, r.SourceDocumentId);
            Assert.Equal(At, r.ExtractedAt);
        });

        var cells = JsonSerializer.Deserialize<string?[]>(rows[1].CellsJson)!;
        Assert.Equal("RAMESH VENKATARAMAN", cells[0]);
        Assert.Equal("Managing Director", cells[2]);
    }

    [Fact]
    public void Row_hash_is_stable_for_identical_rows_and_differs_otherwise()
    {
        var wb = new[]
        {
            Sheet("Legal History",
                Row("A", "B"),
                Row("XYZ Bank", "Pending"),
                Row("XYZ Bank", "Pending"),   // byte-identical duplicate
                Row("XYZ Bank", "Disposed")),
        };

        var rows = SourceRowRecorder.Record(wb, "RocReport", 1, 1, 1, At);

        Assert.Equal(rows[1].RowHash, rows[2].RowHash);
        Assert.NotEqual(rows[1].RowHash, rows[3].RowHash);
        Assert.Equal(64, rows[1].RowHash.Length);
    }

    [Fact]
    public void Row_hash_is_over_the_exact_cell_content_no_trimming_or_null_empty_coalescing()
    {
        var wb = new[]
        {
            Sheet("S",
                Row("bank", "amt"),
                Row("XYZ Bank", "10"),
                Row(" XYZ Bank ", "10"),        // whitespace differs
                Row("XYZ Bank", null),          // null cell
                Row("XYZ Bank", ""),            // empty-string cell
                Row("A|B", "x")),               // contains the historical separator char
        };

        var rows = SourceRowRecorder.Record(wb, "RocReport", 1, 1, 1, At);

        Assert.NotEqual(rows[1].RowHash, rows[2].RowHash); // " XYZ Bank " ≠ "XYZ Bank"
        Assert.NotEqual(rows[3].RowHash, rows[4].RowHash); // null ≠ ""
        // Hash is exactly SHA-256(CellsJson).
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rows[5].CellsJson)));
        Assert.Equal(expected, rows[5].RowHash);
    }

    [Fact]
    public void Sheet_index_tracks_position_across_a_multi_sheet_workbook()
    {
        var wb = new[]
        {
            Sheet("About the Company", Row("x")),
            Sheet("Directors", Row("y")),
            Sheet("Charges", Row("z")),
        };

        var rows = SourceRowRecorder.Record(wb, "RocReport", 1, 1, 1, At);

        Assert.Equal([0, 1, 2], rows.Select(r => r.SheetIndex).ToArray());
        Assert.Equal(["About the Company", "Directors", "Charges"], rows.Select(r => r.SheetName).ToArray());
    }
}
