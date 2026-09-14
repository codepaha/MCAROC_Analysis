namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Immutable manifest entry representing a single PDF within a nested ZIP archive in the Coastal corpus.
/// </summary>
public sealed record CoastalManifestEntry
{
    public required string OuterEntryFullPath { get; init; }
    public required string NestedZipFileName { get; init; }
    public required string NestedEntryRelativePath { get; init; }
    public required long UncompressedByteLength { get; init; }
    public required string Sha256Hex { get; init; }
    public required bool IsCanonical { get; init; }
    public required string CanonicalOuterEntryFullPath { get; init; }
    public required string CanonicalNestedEntryRelativePath { get; init; }
    public required string CanonicalSha256Hex { get; init; }
}
