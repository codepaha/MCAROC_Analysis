namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Immutable inventory result containing both ordered nested-archive entries and PDF manifest entries.
/// </summary>
public sealed record CoastalInventoryResult(
    IReadOnlyList<string> NestedArchiveEntries,
    IReadOnlyList<CoastalManifestEntry> PdfManifestEntries
);
