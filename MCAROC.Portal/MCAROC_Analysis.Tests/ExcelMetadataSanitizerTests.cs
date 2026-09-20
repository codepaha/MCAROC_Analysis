using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Excel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class ExcelMetadataSanitizerTests
{
    static ExcelMetadataSanitizerTests() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    [Fact]
    public void IsProbeMetadataLabel_IdentifiesExpectedLabels()
    {
        Assert.True(ExcelMetadataSanitizer.IsProbeMetadataLabel("Printed at"));
        Assert.True(ExcelMetadataSanitizer.IsProbeMetadataLabel("  printed at  "));
        Assert.True(ExcelMetadataSanitizer.IsProbeMetadataLabel("Probed at"));
        Assert.True(ExcelMetadataSanitizer.IsProbeMetadataLabel("MCA Master Data updated at"));
        Assert.True(ExcelMetadataSanitizer.IsProbeMetadataLabel("MCA Master Data Updated at"));
        Assert.True(ExcelMetadataSanitizer.IsProbeMetadataLabel("Documents collected from MCA at"));

        Assert.False(ExcelMetadataSanitizer.IsProbeMetadataLabel("Legal Name"));
        Assert.False(ExcelMetadataSanitizer.IsProbeMetadataLabel("CIN"));
        Assert.False(ExcelMetadataSanitizer.IsProbeMetadataLabel("PAN"));
        Assert.False(ExcelMetadataSanitizer.IsProbeMetadataLabel("Authorised Capital (Crore)"));
        Assert.False(ExcelMetadataSanitizer.IsProbeMetadataLabel(""));
        Assert.False(ExcelMetadataSanitizer.IsProbeMetadataLabel(null));
    }

    [Fact]
    public void SanitizeWorkbook_CoastalSample_BlanksMetadataAndHidesRowsWithoutShifting()
    {
        var samplePath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982.xls";
        if (!File.Exists(samplePath)) return;

        var tempFile = Path.Combine(Path.GetTempPath(), $"test_sample_{Guid.NewGuid():N}.xls");
        File.Copy(samplePath, tempFile, overwrite: true);

        try
        {
            using (var inStream = File.OpenRead(tempFile))
            {
                var wbBefore = WorkbookFactory.Create(inStream);
                var sheetBefore = wbBefore.GetSheetAt(0);
                Assert.Equal("About the Company", sheetBefore.SheetName);
                Assert.Equal("Printed at", sheetBefore.GetRow(3)?.GetCell(0)?.ToString()?.Trim());
                Assert.Equal("Probed at", sheetBefore.GetRow(4)?.GetCell(0)?.ToString()?.Trim());
                Assert.Equal("MCA Master Data updated at", sheetBefore.GetRow(5)?.GetCell(0)?.ToString()?.Trim());
                Assert.Equal("Documents collected from MCA at", sheetBefore.GetRow(6)?.GetCell(0)?.ToString()?.Trim());
                Assert.Equal("Authorised Capital (Crore)", sheetBefore.GetRow(8)?.GetCell(0)?.ToString()?.Trim());
                Assert.Equal("REGISTERED ADDRESS:", sheetBefore.GetRow(14)?.GetCell(0)?.ToString()?.Trim());
            }

            // Perform sanitization
            var modified = ExcelMetadataSanitizer.SanitizeWorkbook(tempFile);
            Assert.True(modified);

            // Re-open and verify non-destructive zero-height and blank cells
            using (var inStream = File.OpenRead(tempFile))
            {
                var wbAfter = WorkbookFactory.Create(inStream);
                Assert.Equal(26, wbAfter.NumberOfSheets);

                var sheetAfter = wbAfter.GetSheetAt(0);
                // Rows 3, 4, 5, 6 must have ZeroHeight == true and blank cells
                for (int r = 3; r <= 6; r++)
                {
                    var row = sheetAfter.GetRow(r);
                    Assert.NotNull(row);
                    Assert.True(row.ZeroHeight);
                    for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
                    {
                        var cell = row.GetCell(c);
                        if (cell != null)
                        {
                            Assert.Equal(CellType.Blank, cell.CellType);
                        }
                    }
                }

                // Row coordinates of subsequent rows must NOT have shifted
                var row8 = sheetAfter.GetRow(8);
                Assert.NotNull(row8);
                Assert.Equal("Authorised Capital (Crore)", row8.GetCell(0)?.ToString()?.Trim());

                var row14 = sheetAfter.GetRow(14);
                Assert.NotNull(row14);
                Assert.Equal("REGISTERED ADDRESS:", row14.GetCell(0)?.ToString()?.Trim());
                Assert.False(row14.ZeroHeight);
            }

            // Verify file validation opens cleanly
            var valService = new FileValidationService(new ExcelSheetReader());
            var valResult = valService.ValidateOpens(tempFile);
            Assert.True(valResult.IsValid, valResult.Error);

            // Idempotency check: running sanitize again returns false
            var secondRun = ExcelMetadataSanitizer.SanitizeWorkbook(tempFile);
            Assert.False(secondRun);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void SanitizeWorkbook_ChargeSample_BlanksMetadataAndHidesRowsWithoutShifting()
    {
        var samplePath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982-charge.xls";
        if (!File.Exists(samplePath)) return;

        var tempFile = Path.Combine(Path.GetTempPath(), $"test_charge_{Guid.NewGuid():N}.xls");
        File.Copy(samplePath, tempFile, overwrite: true);

        try
        {
            using (var inStream = File.OpenRead(tempFile))
            {
                var wbBefore = WorkbookFactory.Create(inStream);
                var sheetBefore = wbBefore.GetSheetAt(0);
                Assert.Equal("About the Company", sheetBefore.SheetName);
                Assert.Equal("Printed at", sheetBefore.GetRow(3)?.GetCell(0)?.ToString()?.Trim());
                Assert.Equal("MCA Master Data Updated at", sheetBefore.GetRow(5)?.GetCell(0)?.ToString()?.Trim());
            }

            var modified = ExcelMetadataSanitizer.SanitizeWorkbook(tempFile);
            Assert.True(modified);

            using (var inStream = File.OpenRead(tempFile))
            {
                var wbAfter = WorkbookFactory.Create(inStream);
                var sheetAfter = wbAfter.GetSheetAt(0);

                for (int r = 3; r <= 6; r++)
                {
                    var row = sheetAfter.GetRow(r);
                    Assert.NotNull(row);
                    Assert.True(row.ZeroHeight);
                }

                // Row coordinates preserved
                Assert.Equal("Authorised Capital (Crore)", sheetAfter.GetRow(8)?.GetCell(0)?.ToString()?.Trim());
                Assert.Equal("REGISTERED ADDRESS:", sheetAfter.GetRow(14)?.GetCell(0)?.ToString()?.Trim());
            }

            var valService = new FileValidationService(new ExcelSheetReader());
            var valResult = valService.ValidateOpens(tempFile);
            Assert.True(valResult.IsValid, valResult.Error);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void SanitizeWorkbook_StopsAtSectionHeader_DoesNotSanitizeAfterHeaderOrOtherSheets()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_synthetic_{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var wb = new XSSFWorkbook())
            {
                // Sheet matching SheetAliases.CompanyProfile ("Company Master")
                var sheet1 = wb.CreateSheet("Company Master");
                var r0 = sheet1.CreateRow(0);
                r0.CreateCell(0).SetCellValue("Legal Name");
                r0.CreateCell(1).SetCellValue("Acme Corp");

                var r1 = sheet1.CreateRow(1);
                r1.CreateCell(0).SetCellValue("Printed at");
                r1.CreateCell(1).SetCellValue("15 Sep 2026");

                var r2 = sheet1.CreateRow(2);
                r2.CreateCell(0).SetCellValue("REGISTERED ADDRESS:");

                var r3 = sheet1.CreateRow(3);
                r3.CreateCell(0).SetCellValue("Printed at"); // After header! Must NOT be sanitized
                r3.CreateCell(1).SetCellValue("Do not touch");

                // Another sheet
                var sheet2 = wb.CreateSheet("Financials");
                var s2r0 = sheet2.CreateRow(0);
                s2r0.CreateCell(0).SetCellValue("Printed at"); // Other sheet! Must NOT be sanitized
                s2r0.CreateCell(1).SetCellValue("Other sheet value");

                using var fs = File.Create(tempFile);
                wb.Write(fs);
            }

            var modified = ExcelMetadataSanitizer.SanitizeWorkbook(tempFile);
            Assert.True(modified);

            using (var inStream = File.OpenRead(tempFile))
            {
                var wbAfter = WorkbookFactory.Create(inStream);
                var sheet1 = wbAfter.GetSheet("Company Master");
                var r1 = sheet1.GetRow(1);
                Assert.True(r1.ZeroHeight);
                Assert.True(r1.GetCell(0) == null || r1.GetCell(0)!.CellType == CellType.Blank);

                var r3 = sheet1.GetRow(3);
                Assert.False(r3.ZeroHeight);
                Assert.Equal("Printed at", r3.GetCell(0).StringCellValue);
                Assert.Equal("Do not touch", r3.GetCell(1).StringCellValue);

                var sheet2 = wbAfter.GetSheet("Financials");
                var s2r0 = sheet2.GetRow(0);
                Assert.False(s2r0.ZeroHeight);
                Assert.Equal("Printed at", s2r0.GetCell(0).StringCellValue);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
