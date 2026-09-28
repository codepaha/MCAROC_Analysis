using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MCAROC_Analysis.Services.McaFilings;
using NPOI.POIFS.FileSystem;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class BorrowerRequestDocumentReader(PdfTextExtractor pdfExtractor, IConfiguration configuration)
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public async Task<string> ReadAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaxBytes) throw new PreLoginReportException("Upload a request document up to 10 MB.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".pdf" or ".png" or ".jpg" or ".jpeg" or ".docx" or ".doc" or ".eml" or ".txt"))
            throw new PreLoginReportException("Use PDF, PNG, JPG, Word (.doc/.docx), email (.eml), or text (.txt).");
        var folder = Path.Combine(Path.GetTempPath(), "mcaroc-borrower-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "request" + extension);
            await using (var target = File.Create(path))
            {
                await using var source = file.OpenReadStream();
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) throw new PreLoginReportException("Upload a request document up to 10 MB.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            string text;
            switch (extension)
            {
                case ".pdf":
                    using (var pdf = PdfDocument.Open(path))
                    {
                        if (pdf.IsEncrypted || pdf.NumberOfPages > 10) throw new PreLoginReportException("Use an unencrypted request PDF with at most 10 pages.");
                    }
                    var extracted = await pdfExtractor.ExtractAsync(path, folder, ct);
                    if (extracted.Error is not null) throw new PreLoginReportException("This PDF could not be read. Use an unencrypted PDF or a screenshot.");
                    // PdfPig Page.Text omits line boundaries; use content order for native text, keeping
                    // the existing page-by-page OCR fallback for scanned pages.
                    using (var pdf = PdfDocument.Open(path))
                    {
                        var pages = new List<string>();
                        for (var number = 1; number <= pdf.NumberOfPages; number++)
                        {
                            var native = ContentOrderTextExtractor.GetText(pdf.GetPage(number));
                            var ocr = Regex.Match(extracted.FullText, $@"--- Page {number} \(OCR\) ---\s*([\s\S]*?)(?=--- Page \d+ \(|\z)").Groups[1].Value;
                            if (extracted.FullText.Contains($"--- Page {number} (OCR) ---") && string.IsNullOrWhiteSpace(ocr))
                                throw new PreLoginReportException("A scanned page could not be recognized. Check Tesseract is configured, or upload a clearer scan.");
                            pages.Add(ocr.Length > 0 ? ocr : native);
                        }
                        text = string.Join("\n", pages);
                    }
                    break;
                case ".docx": text = ReadDocx(path); break;
                case ".doc": text = ReadLegacyWord(path); break;
                case ".eml": text = await ReadEmailWithAttachmentsAsync(await File.ReadAllTextAsync(path, ct), folder, ct); break;
                case ".txt": text = await File.ReadAllTextAsync(path, ct); break;
                default:
                    var image = await SixLabors.ImageSharp.Image.IdentifyAsync(path, ct);
                    if (image is null || (long)image.Width * image.Height > 25_000_000)
                        throw new PreLoginReportException("Use a screenshot up to 25 megapixels.");
                    text = await OcrImageAsync(path, ct);
                    break;
            }
            if (string.IsNullOrWhiteSpace(text)) throw new PreLoginReportException("No readable request text was found. Upload a clearer document or paste the request text.");
            if (text.Length > 100_000) throw new PreLoginReportException("The request contains too much text; upload only the request form.");
            return text;
        }
        catch (OperationCanceledException) { throw; }
        catch (PreLoginReportException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new PreLoginReportException("The document could not be read. Use an unencrypted PDF, screenshot, Word document, email, or plain text.");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private async Task<string> OcrImageAsync(string path, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = configuration["McaFilings:TesseractExePath"] ?? @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        }};
        process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("stdout");
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add("eng");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            await error;
            if (process.ExitCode != 0) throw new PreLoginReportException("Screenshot OCR failed. Upload a clearer image or paste the request text.");
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PreLoginReportException("Screenshot OCR timed out. Upload a smaller image or paste the request text."); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }

    private static string ReadDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Sum(e => e.Length) > 50 * 1024 * 1024) throw new PreLoginReportException("The Word document expands beyond the allowed size.");
        var document = zip.GetEntry("word/document.xml") ?? throw new PreLoginReportException("This is not a valid Word document.");
        using var stream = document.Open();
        var xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join("\n", xml.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))));
    }

    // Word 97-2003 piece table. Only the main-document text is read; macros are never executed.
    public static string ReadLegacyWord(string path)
    {
        using var file = File.OpenRead(path);
        var compound = new NPOIFSFileSystem(file);
        try
        {
        byte[] Read(string name)
        {
            using var stream = compound.CreateDocumentInputStream(name);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            if (output.Length > MaxBytes) throw new PreLoginReportException("The legacy Word document is too large.");
            return output.ToArray();
        }
        var word = Read("WordDocument");
        if (word.Length < 0x1AA || BitConverter.ToUInt16(word, 0) != 0xA5EC || BitConverter.ToUInt16(word, 2) < 0xC1)
            throw new PreLoginReportException("Use a Word 97-2003 document, or export it to PDF/.docx.");
        var flags = BitConverter.ToUInt16(word, 0xA);
        if ((flags & 0x8100) != 0) throw new PreLoginReportException("Password-protected Word documents cannot be read.");
        var table = Read((flags & 0x0200) != 0 ? "1Table" : "0Table");
        var offset = BitConverter.ToInt32(word, 0x1A2);
        var length = BitConverter.ToInt32(word, 0x1A6);
        if (offset < 0 || length < 5 || offset > table.Length - length) throw new PreLoginReportException("The Word text table is invalid.");
        var end = offset + length;
        while (offset < end && table[offset] == 1) offset += 3 + BitConverter.ToUInt16(table, offset + 1);
        if (offset + 5 > end || table[offset] != 2) throw new PreLoginReportException("This Word document needs to be exported to PDF/.docx.");
        var pieceBytes = BitConverter.ToInt32(table, offset + 1);
        offset += 5;
        if (pieceBytes < 4 || (pieceBytes - 4) % 12 != 0 || pieceBytes > end - offset) throw new PreLoginReportException("The Word text table is invalid.");
        var count = (pieceBytes - 4) / 12;
        var mainCharacters = BitConverter.ToInt32(word, 0x4C);
        if (mainCharacters is < 0 or > 100_000) throw new PreLoginReportException("The Word request contains too much text.");
        var result = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var first = BitConverter.ToInt32(table, offset + i * 4);
            var last = BitConverter.ToInt32(table, offset + (i + 1) * 4);
            var chars = Math.Min(last, mainCharacters) - first;
            if (chars <= 0) continue;
            var fc = BitConverter.ToUInt32(table, offset + (count + 1) * 4 + i * 8 + 2);
            var compressed = (fc & 0x40000000) != 0;
            var start = (int)(fc & 0x3FFFFFFF) / (compressed ? 2 : 1);
            var bytes = chars * (compressed ? 1 : 2);
            if (start < 0 || bytes < 0 || start > word.Length - bytes) throw new PreLoginReportException("The Word text table is invalid.");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            result.Append((compressed ? Encoding.GetEncoding(1252) : Encoding.Unicode).GetString(word, start, bytes));
        }
        return Regex.Replace(result.ToString().Replace('\r', '\n').Replace('\a', '\n'), @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
        }
        finally { compound.Close(); }
    }

    public static string ReadEmail(string email)
    {
        // Standard .eml body text is data; HTML is stripped, never rendered or executed.
        var split = Regex.Split(email, @"\r?\n\r?\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (split.Length < 2) return email;
        var headers = split[0];
        var body = email[(headers.Length + (email.Contains("\r\n") ? 4 : 2))..];
        var boundary = Regex.Match(headers, "boundary=\"?([^\";\\r\\n]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        if (boundary.Length > 0)
        {
            var parts = body.Split("--" + boundary).Where(p => Regex.IsMatch(p, @"Content-Type:\s*text/(plain|html)", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(p, @"Content-Disposition:\s*attachment", RegexOptions.IgnoreCase)).ToList();
            var plain = parts.FirstOrDefault(p => p.Contains("text/plain", StringComparison.OrdinalIgnoreCase));
            return string.Join("\n", (plain is null ? parts.Take(1) : [plain]).Select(ReadEmail));
        }
        if (headers.Contains("base64", StringComparison.OrdinalIgnoreCase)) body = Encoding.UTF8.GetString(Convert.FromBase64String(body.Trim()));
        else if (headers.Contains("quoted-printable", StringComparison.OrdinalIgnoreCase))
        {
            body = Regex.Replace(body, @"=\r?\n", "");
            body = Regex.Replace(body, @"=([A-Fa-f0-9]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }
        if (headers.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            body = Regex.Replace(body, @"<(?:script|style)\b[^>]*>[\s\S]*?</(?:script|style)>", "", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"</(?:p|div|tr)>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            body = WebUtility.HtmlDecode(Regex.Replace(body, @"<[^>]*>", " "));
        }
        return body;
    }

    private async Task<string> ReadEmailWithAttachmentsAsync(string email, string folder, CancellationToken ct)
    {
        var body = ReadEmail(email);
        var attachmentTexts = new List<string>();
        var remaining = 10;
        async Task Visit(string part, int depth)
        {
            if (depth > 5) throw new PreLoginReportException("The email has too many nested parts. Upload the request attachment directly.");
            var divider = Regex.Match(part, @"\r?\n\r?\n");
            if (!divider.Success) return;
            var headers = Regex.Replace(part[..divider.Index], @"\r?\n[ \t]+", " ");
            var content = part[(divider.Index + divider.Length)..];
            var boundary = Regex.Match(headers, "boundary=\"?([^\";\\r\\n]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            if (boundary.Length > 0)
            {
                foreach (var child in content.Split("--" + boundary).Skip(1))
                    if (!child.StartsWith("--")) await Visit(child.TrimStart('\r', '\n'), depth + 1);
                return;
            }
            var name = Regex.Match(headers, "(?:filename|name)=\"?([^\";\\r\\n]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension is not (".pdf" or ".png" or ".jpg" or ".jpeg" or ".doc" or ".docx" or ".txt")) return;
            if (--remaining < 0) throw new PreLoginReportException("Use an email with at most 10 request attachments.");
            // Attachments are never opened by desktop apps or written under their supplied filenames.
            if (!headers.Contains("base64", StringComparison.OrdinalIgnoreCase))
                throw new PreLoginReportException("This email attachment encoding is unsupported. Upload the request attachment directly.");
            var bytes = Convert.FromBase64String(content.Trim());
            if (bytes.Length > MaxBytes) throw new PreLoginReportException("An email attachment exceeds 10 MB.");
            using var stream = new MemoryStream(bytes);
            attachmentTexts.Add(await ReadAsync(new FormFile(stream, 0, bytes.Length, "attachment", "attachment" + extension), ct));
        }
        await Visit(email, 0);
        return body + "\n" + string.Join("\n", attachmentTexts);
    }
}
