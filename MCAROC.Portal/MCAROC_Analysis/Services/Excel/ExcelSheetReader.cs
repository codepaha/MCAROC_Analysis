using System.Data;
using ExcelDataReader;

namespace MCAROC_Analysis.Services.Excel;

public class ExcelSheetReader : IExcelSheetReader
{
    public IReadOnlyList<SheetData> ReadWorkbook(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false }
        });

        var sheets = new List<SheetData>(dataSet.Tables.Count);
        foreach (DataTable table in dataSet.Tables)
        {
            var rows = new List<IReadOnlyList<object?>>(table.Rows.Count);
            foreach (DataRow row in table.Rows)
            {
                var cells = new object?[table.Columns.Count];
                for (var c = 0; c < table.Columns.Count; c++)
                {
                    var value = row[c];
                    cells[c] = value == DBNull.Value ? null : value;
                }
                rows.Add(cells);
            }
            sheets.Add(new SheetData(table.TableName, rows));
        }

        return sheets;
    }
}
