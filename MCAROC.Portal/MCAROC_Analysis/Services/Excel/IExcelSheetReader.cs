namespace MCAROC_Analysis.Services.Excel;

public interface IExcelSheetReader
{
    /// <summary>Reads every worksheet in the workbook at the given path (.xls or .xlsx) into raw row grids.</summary>
    IReadOnlyList<SheetData> ReadWorkbook(string filePath);
}
