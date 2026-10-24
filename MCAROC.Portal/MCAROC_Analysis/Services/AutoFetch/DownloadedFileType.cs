using System.Text;
using System.Xml;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>What a downloaded filing document is, read from its first bytes. Filings carry more than PDFs: XBRL financial
/// statements (.xml) and spreadsheets (.xlsx/.xls) are attached too (live 2026-10-16, a large company: 24 XML and 3 XLSX). The tool
/// answers an expired session with JSON or an HTML page, so only those mean "logged out"; treating a valid XML file as a logout
/// re-logged in for every one and hit the tool's login limit.</summary>
public static class DownloadedFileType
{
    /// <summary>How much of a file is read to judge it: enough for a long XML prolog (declaration, comments) before the root.</summary>
    public const int HeadBytes = 64 * 1024;

    private static readonly byte[] Pdf = "%PDF"u8.ToArray();
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private enum Markup { None, Xml, Html }

    /// <summary>".pdf", ".xml", ".xlsx" (any zip container) or ".xls"; null when the bytes are none of these.</summary>
    public static string? ExtensionOf(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Pdf)) return ".pdf";
        if (head.StartsWith(Zip)) return ".xlsx";
        if (head.StartsWith(Ole)) return ".xls";
        return MarkupOf(head) == Markup.Xml ? ".xml" : null;
    }

    /// <summary>The bytes are a JSON reply or a web page: what the tool sends instead of a file when the session has expired.</summary>
    public static bool LooksLikeSessionReply(ReadOnlySpan<byte> head)
    {
        var text = Decode(head);
        return text.StartsWith('{') || text.StartsWith('[') || MarkupOf(head) == Markup.Html;
    }

    /// <summary><see cref="ExtensionOf"/> for a file on disk; a PDF (nearly every filing document) is known from its first bytes.</summary>
    public static string? ExtensionOfFile(string path)
    {
        var start = ReadHead(path, Pdf.Length);
        return start.AsSpan().StartsWith(Pdf) ? ".pdf" : ExtensionOf(ReadHead(path));
    }

    /// <summary>The first <see cref="HeadBytes"/> of a file on disk, enough for <see cref="ExtensionOf"/>; empty when it cannot be read.</summary>
    public static byte[] ReadHead(string path, int count = HeadBytes)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[count];
            var n = fs.ReadAtLeast(buf, count, throwOnEndOfStream: false);
            return buf[..n];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>Judged by the document's root element, not by text anywhere in it: an XBRL file may mention "&lt;html&gt;" in a
    /// comment or a text block. An HTML page (an "html" root, XHTML included, or an HTML doctype that is not well-formed XML) is Html;
    /// any other well-formed root is Xml, whatever its namespace prefix or encoding. The reader never resolves a DTD or entity.</summary>
    private static Markup MarkupOf(ReadOnlySpan<byte> head)
    {
        var text = Decode(head);
        if (!text.StartsWith('<')) return Markup.None;
        if (text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)) return Markup.Html;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
        try
        {
            // The bytes, not the decoded text: the reader picks the encoding from the BOM / declaration as a parser should.
            using var reader = XmlReader.Create(new MemoryStream(head.ToArray()), settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                    return reader.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase) ? Markup.Html : Markup.Xml;
            }
        }
        catch (XmlException) { /* not well-formed before its root, or cut off before one: not a recognisable XML file */ }
        return Markup.None;
    }

    // Text for the cheap checks: the BOM's (or a BOM-less UTF-16 "<"'s) encoding, else UTF-8; BOM and leading whitespace removed.
    private static string Decode(ReadOnlySpan<byte> head)
    {
        var encoding = head switch
        {
            [0xFF, 0xFE, ..] or [0x3C, 0x00, ..] => Encoding.Unicode,
            [0xFE, 0xFF, ..] or [0x00, 0x3C, ..] => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8
        };
        var sample = head[..Math.Min(head.Length, 1024)];
        return encoding.GetString(sample).TrimStart('﻿', ' ', '\t', '\r', '\n');
    }
}
