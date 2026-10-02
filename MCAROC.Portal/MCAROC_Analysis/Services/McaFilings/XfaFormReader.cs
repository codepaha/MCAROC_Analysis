using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Parser.Parts;
using UglyToad.PdfPig.Tokens;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>One filled field of an XFA form, as filed: its element path inside the form data, its element name and its
/// value. A field filled several times (e.g. a repeated "NewPropParticlars" line) appears once per value, in order.</summary>
public sealed record XfaField(string Path, string Name, string Value);

/// <summary>#369: MCA e-forms filed as XFA dynamic forms (Adobe LiveCycle — Form 8, CHG-1, PAS-3, MGT-14, XBRL...) carry no
/// page text: a text extractor only finds Adobe's "Please wait..." placeholder. Everything the company filed is in the
/// PDF's AcroForm <c>/XFA</c> array, in its <c>datasets</c> XML packet. This reads that packet's filled leaf elements
/// exactly as written — no interpretation.</summary>
public static class XfaFormReader
{
    /// <summary>The placeholder page text an XFA form shows to a viewer that cannot render it.</summary>
    public static bool IsPlaceholderText(string pageText) =>
        pageText.Contains("Please wait", StringComparison.OrdinalIgnoreCase)
        && pageText.Contains("PDF viewer may not be able to display", StringComparison.OrdinalIgnoreCase);

    /// <summary>The filled fields of the document's XFA form data, or null when it has none (an ordinary PDF). Never throws:
    /// a damaged or unexpected XFA structure is treated as "no form data".</summary>
    public static IReadOnlyList<XfaField>? ReadFields(PdfDocument document)
    {
        try
        {
            var datasets = ReadDatasetsPacket(document);
            return datasets is null ? null : ParseDatasets(datasets);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? ReadDatasetsPacket(PdfDocument document)
    {
        var scanner = document.Structure.TokenScanner;
        var catalog = document.Structure.Catalog.CatalogDictionary;
        if (!catalog.TryGet(NameToken.AcroForm, out var acroToken)) return null;
        var acro = DirectObjectFinder.Get<DictionaryToken>(acroToken, scanner);
        if (acro is null || !acro.TryGet(NameToken.Create("XFA"), out var xfaToken)) return null;

        var resolved = xfaToken is IndirectReferenceToken r ? scanner.Get(r.Data)?.Data : xfaToken;
        if (resolved is StreamToken single)
            return Decode(single, scanner);
        if (resolved is not ArrayToken parts) return null;

        // [name0 stream0 name1 stream1 ...] — the packet named "datasets" holds the filled data.
        for (var i = 0; i + 1 < parts.Data.Count; i += 2)
        {
            var name = parts.Data[i] switch { StringToken s => s.Data, NameToken n => n.Data, _ => null };
            if (!string.Equals(name, "datasets", StringComparison.Ordinal)) continue;
            var stream = DirectObjectFinder.Get<StreamToken>(parts.Data[i + 1], scanner);
            return stream is null ? null : Decode(stream, scanner);
        }
        return null;
    }

    private static string Decode(StreamToken stream, UglyToad.PdfPig.Tokenization.Scanner.IPdfTokenScanner scanner)
    {
        var bytes = stream.Decode(DefaultFilterProvider.Instance);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Every leaf element with a non-empty value under <c>xfa:data</c>, in document order. Namespaces are
    /// dropped from the path (MCA forms use none inside the data).</summary>
    public static IReadOnlyList<XfaField> ParseDatasets(string datasetsXml)
    {
        var fields = new List<XfaField>();
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(datasetsXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var data = FindData(doc.DocumentElement);
        if (data is null) return fields;
        foreach (var child in data.ChildNodes.OfType<XmlElement>()) Walk(child, ""); // paths start at the form, not "data"
        return fields;

        void Walk(XmlElement e, string path)
        {
            var here = path.Length == 0 ? e.LocalName : $"{path}/{e.LocalName}";
            var children = e.ChildNodes.OfType<XmlElement>().ToList();
            if (children.Count == 0)
            {
                var value = e.InnerText.Trim();
                if (value.Length > 0) fields.Add(new XfaField(here, e.LocalName, value));
                return;
            }
            foreach (var child in children) Walk(child, here);
        }
    }

    private const string XfaDataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";

    /// <summary>The filled data element: <c>xfa:data</c> when present, else the first <c>data</c> element that is not part
    /// of a <c>dd:dataDescription</c> — some MCA forms (INC-28, PAS-3) open the packet with that schema description, whose
    /// own <c>data</c> element holds only empty field declarations.</summary>
    private static XmlElement? FindData(XmlElement? root)
    {
        if (root is null) return null;
        var all = root.GetElementsByTagName("*").OfType<XmlElement>().Prepend(root).ToList();
        return all.FirstOrDefault(e => e.LocalName == "data" && e.NamespaceURI == XfaDataNamespace)
            ?? all.FirstOrDefault(e => e.LocalName == "data" && !InsideDataDescription(e));
    }

    private static bool InsideDataDescription(XmlElement e)
    {
        for (var n = e.ParentNode; n is not null; n = n.ParentNode)
            if (n.LocalName == "dataDescription") return true;
        return false;
    }

    /// <summary>#364: where a document's XFA fields are kept — beside its extracted text file.</summary>
    public static string SidecarPath(string extractedTextPath) =>
        Path.Combine(Path.GetDirectoryName(extractedTextPath) ?? "", Path.GetFileNameWithoutExtension(extractedTextPath) + ".xfa.json");

    /// <summary>Writes the fields beside the text. An empty list is written too: it records that the PDF was checked and
    /// has no form data, so it is never re-read.</summary>
    public static async Task WriteSidecarAsync(string extractedTextPath, IReadOnlyList<XfaField> fields, CancellationToken ct)
    {
        var path = SidecarPath(extractedTextPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(fields), ct);
    }

    /// <summary>The fields saved beside the text, or null when none were saved (never checked).</summary>
    public static async Task<IReadOnlyList<XfaField>?> ReadSidecarAsync(string extractedTextPath, CancellationToken ct)
    {
        var path = SidecarPath(extractedTextPath);
        if (!File.Exists(path)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<XfaField>>(await File.ReadAllTextAsync(path, ct)) ?? []; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The fields as text for everything that reads a document's text (chat chunks, classification, Gemini
    /// extraction): one "Name: value" line per field, under a single page marker. Values are kept verbatim; only line
    /// breaks inside a value are folded to spaces so each field stays one line.</summary>
    public static string ToText(IReadOnlyList<XfaField> fields)
    {
        var sb = new StringBuilder();
        foreach (var f in fields)
            sb.Append(f.Name).Append(": ").AppendLine(f.Value.ReplaceLineEndings(" "));
        return sb.ToString();
    }
}
