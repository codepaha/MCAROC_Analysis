using NPOI.SS.UserModel;

namespace MCAROC_Analysis.Services.Excel;

public static class ExcelMetadataSanitizer
{
    private static readonly HashSet<string> ProbeMetadataLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Printed at",
        "Probed at",
        "MCA Master Data updated at",
        "MCA Master Data Updated at",
        "Documents collected from MCA at"
    };

    /// <summary>
    /// Checks if a cell label matches any of the Probe42 timestamp/export metadata labels.
    /// </summary>
    public static bool IsProbeMetadataLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        return ProbeMetadataLabels.Contains(label.Trim());
    }

    private static bool IsSectionHeader(IRow row)
    {
        var cell0 = row.GetCell(0);
        var text = cell0?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        if (text.Equals("REGISTERED ADDRESS:", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("BUSINESS ADDRESS:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.EndsWith(':'))
        {
            for (int c = 1; c < row.LastCellNum; c++)
            {
                var other = row.GetCell(c);
                if (other != null && !string.IsNullOrWhiteSpace(other.ToString()))
                {
                    return false;
                }
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Inspects and sanitizes the given Excel workbook (.xls or .xlsx) by blanking the cells and setting
    /// row.ZeroHeight = true on Probe42 export metadata rows in the CompanyProfile sheet.
    /// Does not shift rows, preserving row coordinates, formulas, merged ranges, and other sheets.
    /// </summary>
    /// <param name="filePath">Full path to the workbook file.</param>
    /// <returns>True if any metadata rows were found and sanitized; false otherwise.</returns>
    public static bool SanitizeWorkbook(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        if (!ext.Equals(".xls", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IWorkbook workbook;
        using (var inStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            workbook = WorkbookFactory.Create(inStream);
        }

        if (workbook.NumberOfSheets == 0)
            return false;

        // Resolve worksheet using SheetAliases.CompanyProfile
        ISheet? sheet = null;
        var normalizedAliases = SheetAliases.CompanyProfile.Select(SheetAliases.Normalize).ToHashSet();
        for (int i = 0; i < workbook.NumberOfSheets; i++)
        {
            var s = workbook.GetSheetAt(i);
            if (normalizedAliases.Contains(SheetAliases.Normalize(s.SheetName)))
            {
                sheet = s;
                break;
            }
        }
        if (sheet == null && workbook.NumberOfSheets > 0)
        {
            sheet = workbook.GetSheetAt(0);
        }

        if (sheet == null)
            return false;

        bool modified = false;

        // Scan from top to bottom
        for (int r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            // Stop scan immediately upon encountering a section header
            if (IsSectionHeader(row))
            {
                break;
            }

            var cell0 = row.GetCell(0);
            var text = cell0?.ToString()?.Trim();
            if (IsProbeMetadataLabel(text))
            {
                var cellsToRemove = new List<ICell>();
                for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
                {
                    var cell = row.GetCell(c);
                    if (cell != null)
                    {
                        cell.SetCellValue((string?)null);
                        cell.SetBlank();
                        cellsToRemove.Add(cell);
                    }
                }
                foreach (var cell in cellsToRemove)
                {
                    row.RemoveCell(cell);
                }
                row.ZeroHeight = true;
                modified = true;
            }
        }

        if (modified)
        {
            var tempPath = filePath + $".{Guid.NewGuid():N}.tmp";
            using (var outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                workbook.Write(outStream);
            }
            File.Move(tempPath, filePath, overwrite: true);
        }

        return modified;
    }
}
