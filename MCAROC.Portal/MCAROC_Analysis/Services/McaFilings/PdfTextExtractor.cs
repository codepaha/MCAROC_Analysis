using System.Diagnostics;
using System.Text;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Services.McaFilings;

public record PdfExtractionResult(
    string FullText, int PageCount, int NativePageCount, int OcrPageCount,
    TextExtractionMethod Method, FilingDocumentProcessingStatus Status, string? Error);

/// <summary>Extracts text page-by-page (not whole-document fallback), since a single filing document can
/// mix native-text and scanned pages: native text via PdfPig first; only pages below the character
/// threshold are rendered to an image (PDFtoImage/pdfium) and OCR'd (the Tesseract binary already
/// installed at the configured path). The threshold is intentionally a constructor parameter, not a
/// hardcoded constant — it was flagged in the plan as something to tune against real samples, not guess.</summary>
public class PdfTextExtractor(ILogger<PdfTextExtractor> logger, string tesseractExePath, int minCharsPerPageForNativeText = 80)
{
    public async Task<PdfExtractionResult> ExtractAsync(string pdfPath, string tempDir, CancellationToken ct)
    {
        byte[] pdfBytes;
        try
        {
            pdfBytes = await File.ReadAllBytesAsync(pdfPath, ct);
        }
        catch (Exception ex)
        {
            return new PdfExtractionResult("", 0, 0, 0, TextExtractionMethod.None, FilingDocumentProcessingStatus.CorruptPdf, ex.Message);
        }

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(pdfPath);
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
        {
            return new PdfExtractionResult("", 0, 0, 0, TextExtractionMethod.None, FilingDocumentProcessingStatus.PasswordProtected, "Encrypted PDF.");
        }
        catch (Exception ex)
        {
            return new PdfExtractionResult("", 0, 0, 0, TextExtractionMethod.None, FilingDocumentProcessingStatus.CorruptPdf, ex.Message);
        }

        using (document)
        {
            if (document.IsEncrypted)
                return new PdfExtractionResult("", 0, 0, 0, TextExtractionMethod.None, FilingDocumentProcessingStatus.PasswordProtected, "Encrypted PDF.");

            var pageCount = document.NumberOfPages;
            var sb = new StringBuilder();
            var nativeCount = 0;
            var ocrCount = 0;

            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                ct.ThrowIfCancellationRequested();

                string pageText;
                try
                {
                    pageText = document.GetPage(pageNumber).Text ?? "";
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read native text for page {Page} of {Path}", pageNumber, pdfPath);
                    pageText = "";
                }

                if (pageText.Length >= minCharsPerPageForNativeText)
                {
                    sb.AppendLine($"--- Page {pageNumber} (native) ---");
                    sb.AppendLine(pageText);
                    nativeCount++;
                }
                else
                {
                    var ocrText = await OcrPageAsync(pdfBytes, pageNumber, tempDir, ct);
                    sb.AppendLine($"--- Page {pageNumber} (OCR) ---");
                    sb.AppendLine(ocrText);
                    ocrCount++;
                }
            }

            var method = ocrCount == 0 ? TextExtractionMethod.Native
                : nativeCount == 0 ? TextExtractionMethod.Ocr
                : TextExtractionMethod.Mixed;

            return new PdfExtractionResult(sb.ToString(), pageCount, nativeCount, ocrCount, method, FilingDocumentProcessingStatus.TextExtracted, null);
        }
    }

    private async Task<string> OcrPageAsync(byte[] pdfBytes, int pageNumber, string tempDir, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        var token = Guid.NewGuid().ToString("N");
        var imagePath = Path.Combine(tempDir, $"page-{pageNumber}-{token}.png");
        var outputBase = Path.Combine(tempDir, $"page-{pageNumber}-{token}");

        try
        {
            PDFtoImage.Conversion.SavePng(imagePath, pdfBytes, page: pageNumber - 1); // PDFtoImage is 0-indexed

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = tesseractExePath,
                    Arguments = $"\"{imagePath}\" \"{outputBase}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();

            var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(30), ct);
            if (!exited)
            {
                TryKill(process);
                logger.LogWarning("Tesseract timed out on page {Page}", pageNumber);
                return string.Empty;
            }

            var textPath = outputBase + ".txt";
            return File.Exists(textPath) ? await File.ReadAllTextAsync(textPath, ct) : string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OCR failed for page {Page}", pageNumber);
            return string.Empty;
        }
        finally
        {
            TryDelete(imagePath);
            TryDelete(outputBase + ".txt");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our own timeout, not the caller's cancellation
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
    }
}
