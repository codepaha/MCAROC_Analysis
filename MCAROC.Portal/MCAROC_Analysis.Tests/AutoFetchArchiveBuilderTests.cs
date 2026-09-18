using System.IO.Compression;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.McaFilings;

namespace MCAROC_Analysis.Tests;

/// <summary>The packaged archive must be exactly what the MCA filings pipeline unpacks: outer zip →
/// "{Section}/{docId}_{COMPANY}_{CIN}.zip" → PDFs, safe by ArchiveSafetyValidator's rules and with a
/// nested-zip name FilingIdentityParser reads the company/CIN back out of.</summary>
public class AutoFetchArchiveBuilderTests : IDisposable
{
    private const string Cin = "U45203OR1995PLC003982";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "autofetch-archive-tests-" + Guid.NewGuid().ToString("N"));

    public AutoFetchArchiveBuilderTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    private string Pdf(string relative)
    {
        var path = Path.Combine(_tempDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, "%PDF-1.4\n%stub\n"u8.ToArray());
        return path;
    }

    [Fact]
    public void Builds_the_nested_zip_layout_the_filings_pipeline_expects()
    {
        var filings = new[]
        {
            new ArchiveFiling("Charge Documents", "2ee2d6813e1267e581cde7701d36eb46v1",
            [
                ("Form CHG-1.pdf", Pdf("c1/main.pdf")),
                ("666589981-DEED-OF-MORTGAGE.pdf", Pdf("c1/att1.pdf")),
                ("666589981-DEED-OF-MORTGAGE.pdf", Pdf("c1/att2.pdf")), // duplicate name → suffixed, not lost
                ("missing.pdf", Path.Combine(_tempDir, "c1", "nope.pdf")), // never downloaded → skipped
            ]),
            new ArchiveFiling("Financial Documents", "079987a1e2c7a9e35f776383fadc87a2v1", [("Form PAS-3.pdf", Pdf("a1/main.pdf"))]),
            new ArchiveFiling("Scanned Documents", "deadbeef", []), // nothing on disk → no nested zip at all
        };
        var outer = Path.Combine(_tempDir, "out", "filings.zip");

        AutoFetchArchiveBuilder.Build(outer, "Coastal Projects Limited", Cin, filings);

        Assert.True(ArchiveSafetyValidator.ValidateOuterArchive(outer, ArchiveSafetyLimits.Default).IsValid);
        using var archive = ZipFile.OpenRead(outer);
        var names = archive.Entries.Select(e => e.FullName).OrderBy(x => x).ToList();
        Assert.Equal(
        [
            "Charge Documents/2ee2d6813e1267e581cde7701d36eb46v1_COASTAL_PROJECTS_LIMITED_U45203OR1995PLC003982.zip",
            "Financial Documents/079987a1e2c7a9e35f776383fadc87a2v1_COASTAL_PROJECTS_LIMITED_U45203OR1995PLC003982.zip",
        ], names);

        var identity = FilingIdentityParser.Parse(Path.GetFileNameWithoutExtension(names[0]));
        Assert.Equal("2ee2d6813e1267e581cde7701d36eb46v1", identity.Srn);
        Assert.Equal("COASTAL PROJECTS LIMITED", identity.CompanyName);
        Assert.Equal(Cin, identity.Cin);

        using var nestedStream = archive.Entries.First(e => e.FullName.StartsWith("Charge")).Open();
        using var buffer = new MemoryStream();
        nestedStream.CopyTo(buffer);
        buffer.Position = 0;
        using var nested = new ZipArchive(buffer, ZipArchiveMode.Read);
        Assert.Equal(
        [
            "666589981-DEED-OF-MORTGAGE (2).pdf",
            "666589981-DEED-OF-MORTGAGE.pdf",
            "Form CHG-1.pdf",
        ], nested.Entries.Select(e => e.FullName).OrderBy(x => x).ToList());
        Assert.All(nested.Entries, e => Assert.True(ArchiveSafetyValidator.IsPathSafe(e.FullName)));
    }

    [Theory]
    [InlineData("Lodha Developers Limited", "LODHA_DEVELOPERS_LIMITED")]
    [InlineData("  A&B (India) Pvt. Ltd ", "A_B_INDIA_PVT_LTD")]
    [InlineData("", "COMPANY")]
    public void Company_token_is_uppercase_alphanumeric_words_joined_by_underscores(string name, string expected) =>
        Assert.Equal(expected, AutoFetchArchiveBuilder.CompanyToken(name));

    [Theory]
    [InlineData("Form CHG-1", "Form CHG-1.pdf")]
    [InlineData("../evil/Form 8.pdf", "evil Form 8.pdf")]
    [InlineData("  ", "document.pdf")]
    [InlineData("Instruments of creation or modification of charge.PDF", "Instruments of creation or modification of charge.PDF")]
    public void Entry_names_keep_the_classifier_keywords_but_drop_path_characters(string raw, string expected) =>
        Assert.Equal(expected, AutoFetchArchiveBuilder.SanitizeEntryName(raw));

    [Fact]
    public void Entry_name_length_is_capped_with_pdf_extension_kept()
    {
        var name = AutoFetchArchiveBuilder.SanitizeEntryName(new string('x', 400));
        Assert.True(name.Length <= 150);
        Assert.EndsWith(".pdf", name);
    }
}
