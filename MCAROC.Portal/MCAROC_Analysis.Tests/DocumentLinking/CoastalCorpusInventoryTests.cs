using System.Security.Cryptography;
using System.Text.Json;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public class CoastalCorpusInventoryTests
{
    private const string ZipPath = @"E:\Downloads\Coastal data\COASTAL PROJECTS LIMITED Documents.zip";

    private static readonly string[] ExpectedArchivePaths =
    [
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70908_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70912_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70913_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70914_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70915_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70916_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70917_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70922_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70926_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70927_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70928_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70929_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70931_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70932_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70933_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Incorporation and Other Documents/70935_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
        "COASTAL PROJECTS LIMITED Documents/Incorporation and Other Documents/70936_COASTAL_PROJECTS_U45203OR1995PLC003982.zip"
    ];

    [SkippableFact]
    public void OuterArchiveSha256_MatchesKnownGood()
    {
        Skip.If(!File.Exists(ZipPath), "Coastal fixture ZIP not present on this machine.");

        using var stream = File.OpenRead(ZipPath);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);
        var hashHex = Convert.ToHexString(hashBytes);

        Assert.Equal("1DABC81A46C44C68AA864DEB5A91E446663CDA61B30CA903BD2CC593DD87C1FA", hashHex, ignoreCase: true);
    }

    [SkippableFact]
    public void InventoryPass_ArchivePathsControlTotalsAndCanonicalInvariants()
    {
        Skip.If(!File.Exists(ZipPath), "Coastal fixture ZIP not present on this machine.");

        using var stream = File.OpenRead(ZipPath);
        var result = CoastalCorpusInventoryService.BuildManifest(stream);

        // 1. Nested archive paths check
        Assert.Equal(19, result.NestedArchiveEntries.Count);
        Assert.Equal(ExpectedArchivePaths, result.NestedArchiveEntries);

        // 2. Control totals check
        var manifest = result.PdfManifestEntries;
        Assert.Equal(814, manifest.Count);
        Assert.Equal(645, manifest.Count(e => e.IsCanonical));
        Assert.Equal(169, manifest.Count(e => !e.IsCanonical));

        // 3. Canonical-relation invariants
        var byHash = manifest.GroupBy(e => e.Sha256Hex).ToList();

        // Invariant 1: Exactly one canonical entry per hash group
        foreach (var group in byHash)
        {
            Assert.Single(group, e => e.IsCanonical);
        }

        // Invariant 2: Canonical entries are self-referential
        foreach (var e in manifest.Where(e => e.IsCanonical))
        {
            Assert.Equal(e.OuterEntryFullPath, e.CanonicalOuterEntryFullPath);
            Assert.Equal(e.NestedEntryRelativePath, e.CanonicalNestedEntryRelativePath);
        }

        // Invariant 3: Every duplicate points to an existing canonical with the same SHA-256
        var canonicalIndex = manifest
            .Where(e => e.IsCanonical)
            .ToDictionary(e => (e.OuterEntryFullPath, e.NestedEntryRelativePath));

        foreach (var e in manifest.Where(e => !e.IsCanonical))
        {
            var key = (e.CanonicalOuterEntryFullPath, e.CanonicalNestedEntryRelativePath);
            Assert.True(canonicalIndex.TryGetValue(key, out var canon),
                $"Duplicate entry {e.NestedEntryRelativePath} has no matching canonical.");
            Assert.Equal(e.Sha256Hex, canon!.Sha256Hex);
        }

        // Invariant 4: CanonicalSha256Hex == Sha256Hex on every entry
        foreach (var e in manifest)
        {
            Assert.Equal(e.Sha256Hex, e.CanonicalSha256Hex);
        }

        // Invariant 5: NestedZipFileName == Path.GetFileName(OuterEntryFullPath) on every entry
        foreach (var e in manifest)
        {
            Assert.Equal(Path.GetFileName(e.OuterEntryFullPath), e.NestedZipFileName);
        }
    }

    [SkippableFact]
    public void SerializedResult_ByteForByteEqual_OnTwoIndependentRuns()
    {
        Skip.If(!File.Exists(ZipPath), "Coastal fixture ZIP not present on this machine.");

        string json1;
        using (var stream1 = File.OpenRead(ZipPath))
        {
            var res1 = CoastalCorpusInventoryService.BuildManifest(stream1);
            json1 = JsonSerializer.Serialize(res1);
        }

        string json2;
        using (var stream2 = File.OpenRead(ZipPath))
        {
            var res2 = CoastalCorpusInventoryService.BuildManifest(stream2);
            json2 = JsonSerializer.Serialize(res2);
        }

        Assert.Equal(json1, json2);
    }
}
