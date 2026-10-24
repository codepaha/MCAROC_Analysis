using System.Text;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>What a downloaded filing document is, read from its first bytes. Filings carry more than PDFs: XBRL financial
/// statements (.xml) and spreadsheets (.xlsx/.xls) are attached too (live 2026-10-16, a large company: 24 XML and 3 XLSX). The tool
/// answers an expired session with JSON or an HTML page, so only those mean "logged out"; treating a valid XML file as a logout
/// re-logged in for every one and hit the tool's login limit.</summary>
public static class DownloadedFileType
{
    private static readonly byte[] Pdf = "%PDF"u8.ToArray();
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>".pdf", ".xml", ".xlsx" (any zip container) or ".xls"; null when the bytes are none of these.</summary>
    public static string? ExtensionOf(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Pdf)) return ".pdf";
        if (head.StartsWith(Zip)) return ".xlsx";
        if (head.StartsWith(Ole)) return ".xls";
        var text = Text(head);
        if ((text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<xbrl", StringComparison.OrdinalIgnoreCase))
            && !LooksLikeHtml(text))
            return ".xml";
        return null;
    }

    /// <summary>The bytes are a JSON reply or a web page: what the tool sends instead of a file when the session has expired.</summary>
    public static bool LooksLikeSessionReply(ReadOnlySpan<byte> head)
    {
        var text = Text(head);
        return text.StartsWith('{') || text.StartsWith('[') || LooksLikeHtml(text);
    }

    /// <summary>The first bytes of a file on disk, enough for <see cref="ExtensionOf"/>; empty when it cannot be read.</summary>
    public static byte[] ReadHead(string path, int count = 512)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[count];
            var n = fs.Read(buf, 0, count);
            return buf[..n];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static bool LooksLikeHtml(string text) =>
        text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || text.Contains("<html", StringComparison.OrdinalIgnoreCase);

    // UTF-8 with any BOM and leading whitespace removed.
    private static string Text(ReadOnlySpan<byte> head) => Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
}
