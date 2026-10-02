using System.Diagnostics;
using System.Text;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Services.McaFilings;

/// <param name="XfaFields">#364: the PDF's filled XFA form fields when it has any — read while the PDF is open anyway, so
/// the charge-form reader never has to reopen it. Null for an ordinary PDF.</param>
public record PdfExtractionResult(
    string FullText, int PageCount, int NativePageCount, int OcrPageCount,
    TextExtractionMethod Method, FilingDocumentProcessingStatus Status, string? Error,
    IReadOnlyList<XfaField>? XfaFields = null);

/// <summary>Extracts text page-by-page (not whole-document fallback), since a single filing document can
/// mix native-text and scanned pages: native text via PdfPig first; only pages below the character
/// threshold are rendered to an image (PDFtoImage/pdfium) and OCR'd (the Tesseract binary already
/// installed at the configured path). The threshold is intentionally a constructor parameter, not a
/// hardcoded constant — it was flagged in the plan as something to tune against real samples, not guess.</summary>
public class PdfTextExtractor(ILogger<PdfTextExtractor> logger, string tesseractExePath, int minCharsPerPageForNativeText = 80)
{
    public virtual async Task<PdfExtractionResult> ExtractAsync(string pdfPath, string tempDir, CancellationToken ct)
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

            // #369: an XFA e-form (Form 8, CHG-1, PAS-3, MGT-14...) shows only Adobe's "Please wait..." placeholder as page
            // text, and that placeholder is long enough to skip OCR. The filed values are in the form's XFA data, so a
            // placeholder page is replaced by them (once — the form data is the whole form); real pages are kept as they are.
            IReadOnlyList<XfaField>? xfaFields = null;
            var xfaUsed = false;
            var unreadableFormPages = 0;

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

                if (XfaFormReader.IsPlaceholderText(pageText))
                {
                    // The placeholder is never content. With readable form data the page becomes the form's fields;
                    // without it (no packet, or one that can't be read) the page contributes nothing.
                    if ((xfaFields ??= XfaFormReader.ReadFields(document) ?? []) is { Count: > 0 })
                    {
                        if (!xfaUsed)
                        {
                            sb.AppendLine($"--- Page {pageNumber} (native) ---");
                            sb.Append(XfaFormReader.ToText(xfaFields));
                            xfaUsed = true;
                        }
                        nativeCount++;
                    }
                    else
                    {
                        unreadableFormPages++;
                        logger.LogWarning("Page {Page} of {Path} is an XFA form placeholder but the form data could not be read", pageNumber, pdfPath);
                    }
                }
                else if (pageText.Length >= minCharsPerPageForNativeText)
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

            // Nothing but unreadable XFA placeholders: there is no filing content to pass on — never report it as extracted.
            if (unreadableFormPages > 0 && nativeCount == 0 && ocrCount == 0)
                return new PdfExtractionResult("", pageCount, 0, 0, TextExtractionMethod.None, FilingDocumentProcessingStatus.UnsupportedPdf,
                    "XFA e-form whose form data could not be read; the PDF only shows Adobe's placeholder page.");

            var method = xfaUsed ? TextExtractionMethod.Xfa
                : ocrCount == 0 ? TextExtractionMethod.Native
                : nativeCount == 0 ? TextExtractionMethod.Ocr
                : TextExtractionMethod.Mixed;

            // Many forms render visible text and still carry their XFA data; keep the fields for exact consumers (#364).
            xfaFields ??= XfaFormReader.ReadFields(document) ?? [];
            return new PdfExtractionResult(sb.ToString(), pageCount, nativeCount, ocrCount, method, FilingDocumentProcessingStatus.TextExtracted, null,
                xfaFields.Count > 0 ? xfaFields : null);
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
