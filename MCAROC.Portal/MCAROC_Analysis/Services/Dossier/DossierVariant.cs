namespace MCAROC_Analysis.Services.Dossier;

/// <summary>The client PDF flavours. <see cref="Executive"/> is the ~20-page orientation dossier
/// (cover, contents, snapshot, Section 1, concise de-duplicated annexures A–E). <see cref="FullSource"/>
/// keeps the Section 1 synthesis but replaces the typed annexures with the verbatim Layer-0 source
/// record — every non-blank row of every worksheet, unabridged (up to 200+ pages).
/// <see cref="SourceRecord"/> is <see cref="FullSource"/> with Section 1 (the synthesised analysis)
/// removed — a pure source record for clients who want no interpretation at all.</summary>
public enum DossierVariant
{
    Executive,
    FullSource,
    SourceRecord
}
