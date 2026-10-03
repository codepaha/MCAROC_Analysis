using System.Security.Cryptography;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>One file embedded in a PDF: its name inside the PDF and the SHA-256 of its bytes (upper-case hex, the same form
/// as <c>McaFilingDocument.FileHash</c>).</summary>
public sealed record EmbeddedFileHash(string Name, string Sha256);

/// <summary>#377: MCA e-forms carry their attachments (instrument of charge, deeds, satisfaction letters...) embedded inside
/// the form PDF, and the export also ships each attachment as its own PDF. The two are byte-identical, so an attachment is
/// tied to its form by hash — exactly, without reading names or guessing.</summary>
public static class EmbeddedFiles
{
    /// <summary>The hashes of the document's embedded files; empty when it has none. Never throws: a damaged file tree is
    /// treated as "no embedded files".</summary>
    public static IReadOnlyList<EmbeddedFileHash> Read(PdfDocument document)
    {
        try
        {
            if (!document.Advanced.TryGetEmbeddedFiles(out var files) || files is null) return [];
            return files.Select(f => new EmbeddedFileHash(f.Name ?? "", Convert.ToHexString(SHA256.HashData(f.Bytes.ToArray())))).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Where a document's embedded-file hashes are kept — beside its extracted text, so the link to its attachments
    /// survives the PDF being retired (#376).</summary>
    public static string SidecarPath(string extractedTextPath) =>
        Path.Combine(Path.GetDirectoryName(extractedTextPath) ?? "", Path.GetFileNameWithoutExtension(extractedTextPath) + ".embedded.json");

    /// <summary>Writes the hashes beside the text. An empty list is written too: it records that the PDF was checked.</summary>
    public static async Task WriteSidecarAsync(string extractedTextPath, IReadOnlyList<EmbeddedFileHash> files, CancellationToken ct)
    {
        var path = SidecarPath(extractedTextPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(files), ct);
    }

    /// <summary>The hashes saved beside the text, or null when none were saved (never checked).</summary>
    public static async Task<IReadOnlyList<EmbeddedFileHash>?> ReadSidecarAsync(string extractedTextPath, CancellationToken ct)
    {
        var path = SidecarPath(extractedTextPath);
        if (!File.Exists(path)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<EmbeddedFileHash>>(await File.ReadAllTextAsync(path, ct)) ?? []; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
