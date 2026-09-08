using MCAROC_Analysis.Services.Excel;

namespace MCAROC_Analysis.Tests;

/// <summary>Returns canned SheetData regardless of the file path, so integration tests can exercise the
/// full IngestionOrchestrator without writing real Excel binaries to disk.</summary>
public class FakeExcelSheetReader(Dictionary<string, IReadOnlyList<SheetData>> workbooksByPath) : IExcelSheetReader
{
    public IReadOnlyList<SheetData> ReadWorkbook(string filePath) => workbooksByPath[filePath];
}
