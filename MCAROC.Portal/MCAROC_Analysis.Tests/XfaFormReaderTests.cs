using System.Text;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#369: MCA XFA e-forms carry no page text — only Adobe's "Please wait..." placeholder — so their filed values
/// are read from the PDF's XFA datasets packet. Field names below mirror real MCA Form 8 / INC-28 packets.</summary>
public class XfaFormReaderTests
{
    private const string Placeholder =
        "Please wait... If this message is not eventually replaced by the proper contents of the document, your PDF viewer may not be able to display this type of document.";

    // The common layout: xfa:datasets > xfa:data > form fields.
    private const string Form8Packet = """
        <xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/">
          <xfa:data>
            <Form8_Dtls>
              <Form8>
                <CIN>U45203OR1995PLC003982</CIN>
                <ChargeID>10332396</ChargeID>
                <InstrumentDesc>Deed of Hypothecation dated 07/09/2015</InstrumentDesc>
                <NewPropParticlars>Extension of the exclusive security (as detailed in Schedule IV of DOH)</NewPropParticlars>
                <NewPropParticlars>Flat No. 305, B Wing, Sunrise Society</NewPropParticlars>
                <PropOwnCmp>NO</PropOwnCmp>
                <Empty></Empty>
                <ChargeHolderDetails><OptionalName>Example Bank Limited</OptionalName></ChargeHolderDetails>
              </Form8>
            </Form8_Dtls>
          </xfa:data>
        </xfa:datasets>
        """;

    // The INC-28/PAS-3 layout: a schema description (with its own empty <data>) precedes the filled data.
    private const string DescriptionFirstPacket = """
        <xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/">
          <dd:dataDescription xmlns:dd="http://ns.adobe.com/data-description/" dd:name="data">
            <data><ZMCA_NCA_INC_28><CIN/><NAME_COURT_TRIBU/></ZMCA_NCA_INC_28></data>
          </dd:dataDescription>
          <xfa:data>
            <data><ZMCA_NCA_INC_28><CIN>U45203OR1995PLC003982</CIN><NAME_COURT_TRIBU>National Company Law Tribunal</NAME_COURT_TRIBU></ZMCA_NCA_INC_28></data>
          </xfa:data>
        </xfa:datasets>
        """;

    [Fact]
    public void ParseDatasets_ReadsEveryFilledField_InOrder_WithRepeatsAndNoEmpties()
    {
        var fields = XfaFormReader.ParseDatasets(Form8Packet);

        Assert.Equal("10332396", fields.Single(f => f.Name == "ChargeID").Value);
        Assert.Equal("NO", fields.Single(f => f.Name == "PropOwnCmp").Value);
        Assert.Equal(["Extension of the exclusive security (as detailed in Schedule IV of DOH)", "Flat No. 305, B Wing, Sunrise Society"],
            fields.Where(f => f.Name == "NewPropParticlars").Select(f => f.Value));
        Assert.Equal("Form8_Dtls/Form8/ChargeHolderDetails/OptionalName", fields.Single(f => f.Name == "OptionalName").Path);
        Assert.DoesNotContain(fields, f => f.Name == "Empty");
    }

    [Fact]
    public void ParseDatasets_SkipsTheSchemaDescription_AndReadsTheFilledData()
    {
        var fields = XfaFormReader.ParseDatasets(DescriptionFirstPacket);

        Assert.Equal(2, fields.Count);
        Assert.Equal("National Company Law Tribunal", fields.Single(f => f.Name == "NAME_COURT_TRIBU").Value);
    }

    [Fact]
    public void ToText_WritesOneVerbatimLinePerField()
    {
        var text = XfaFormReader.ToText([new XfaField("a/ChargeID", "ChargeID", "10332396"), new XfaField("a/P", "P", "Flat No. 305,\nB Wing")]);

        Assert.Equal($"ChargeID: 10332396{Environment.NewLine}P: Flat No. 305, B Wing{Environment.NewLine}", text);
    }

    [Theory]
    [InlineData(Placeholder, true)]
    [InlineData("Form CHG-1 Charge ID of the charge to be modified 10339546", false)]
    public void IsPlaceholderText_RecognisesOnlyTheAdobePlaceholder(string text, bool expected)
    {
        Assert.Equal(expected, XfaFormReader.IsPlaceholderText(text));
    }

    [Fact]
    public async Task PdfTextExtractor_ReadsAnXfaForm_InsteadOfItsPlaceholderPage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xfa-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, MinimalXfaPdf(Placeholder, Form8Packet));
        try
        {
            var result = await new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance, "").ExtractAsync(path, Path.GetTempPath(), CancellationToken.None);

            Assert.Equal(TextExtractionMethod.Xfa, result.Method);
            Assert.Equal(FilingDocumentProcessingStatus.TextExtracted, result.Status);
            Assert.StartsWith("--- Page 1 (native) ---", result.FullText);
            Assert.Contains("ChargeID: 10332396", result.FullText);
            Assert.Contains("NewPropParticlars: Flat No. 305, B Wing, Sunrise Society", result.FullText);
            Assert.Contains("PropOwnCmp: NO", result.FullText);
            Assert.False(XfaFormReader.IsPlaceholderText(result.FullText));
            Assert.Equal(0, result.OcrPageCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PdfTextExtractor_LeavesAnOrdinaryPdfAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plain-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, MinimalXfaPdf("This is an ordinary charge instrument page describing Flat No. 305, B Wing, Sunrise Society, Pune.", datasets: null));
        try
        {
            var result = await new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance, "").ExtractAsync(path, Path.GetTempPath(), CancellationToken.None);

            Assert.Equal(TextExtractionMethod.Native, result.Method);
            Assert.Contains("Flat No. 305", result.FullText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A one-page PDF whose page shows <paramref name="pageText"/> in Helvetica, with — when
    /// <paramref name="datasets"/> is given — an AcroForm whose /XFA array carries that datasets packet.</summary>
    private static byte[] MinimalXfaPdf(string pageText, string? datasets)
    {
        var escaped = pageText.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var content = $"BT /F1 8 Tf 20 400 Td ({escaped}) Tj ET";
        var objects = new List<string>
        {
            datasets is null ? "<< /Type /Catalog /Pages 2 0 R >>" : "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1200 800] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        if (datasets is not null)
        {
            objects.Add("<< /Fields [] /XFA [(datasets) 7 0 R] >>");
            objects.Add($"<< /Length {Encoding.UTF8.GetByteCount(datasets)} >>\nstream\n{datasets}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.UTF8.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.UTF8.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) pdf.Append($"{o:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.UTF8.GetBytes(pdf.ToString());
    }
}
