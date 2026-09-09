namespace MCAROC_Analysis.Services;

public record FileValidationResult(bool IsValid, string? Error);

/// <summary>Validates uploaded workbooks before they reach the ingestion pipeline: extension, real file
/// signature (not just the browser-supplied MIME type), size limits, and that a workbook actually opens
/// with at least one sheet.</summary>
public class FileValidationService(Services.Excel.IExcelSheetReader sheetReader)
{
    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB

    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]; // legacy .xls
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04]; // .xlsx (a zip container)

    public FileValidationResult ValidateUpload(string fileName, long fileSize, Stream contentStream)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".xls" or ".xlsx"))
            return new FileValidationResult(false, "Only .xls or .xlsx files are accepted.");

        if (fileSize <= 0)
            return new FileValidationResult(false, "The uploaded file is empty.");

        if (fileSize > MaxFileSizeBytes)
            return new FileValidationResult(false, $"File exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB limit.");

        var header = new byte[8];
        var read = contentStream.Read(header, 0, header.Length);
        contentStream.Seek(0, SeekOrigin.Begin);

        var looksLikeOle = read >= 8 && header.AsSpan(0, 8).SequenceEqual(OleSignature);
        var looksLikeZip = read >= 4 && header.AsSpan(0, 4).SequenceEqual(ZipSignature);
        if (!looksLikeOle && !looksLikeZip)
            return new FileValidationResult(false, "File content does not match a valid Excel workbook signature.");

        return new FileValidationResult(true, null);
    }

    /// <summary>Confirms the saved file actually opens as a workbook with at least one sheet.</summary>
    public FileValidationResult ValidateOpens(string savedFilePath)
    {
        try
        {
            var sheets = sheetReader.ReadWorkbook(savedFilePath);
            return sheets.Count == 0
                ? new FileValidationResult(false, "Workbook opened but contains no worksheets.")
                : new FileValidationResult(true, null);
        }
        catch (Exception ex)
        {
            return new FileValidationResult(false, $"Workbook could not be opened: {ex.Message}");
        }
    }
}
