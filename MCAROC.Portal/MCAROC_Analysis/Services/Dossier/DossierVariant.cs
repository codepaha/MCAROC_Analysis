namespace MCAROC_Analysis.Services.Dossier;

/// <summary>The client PDF flavours. <see cref="Executive"/> is the ~20-page orientation dossier
/// (cover, contents, snapshot, Section 1, concise de-duplicated annexures A–E). <see cref="FullSource"/>
/// keeps the Section 1 synthesis but replaces the typed annexures with the verbatim Layer-0 source
/// record — every non-blank row of every worksheet, unabridged (up to 200+ pages).
/// <see cref="SourceRecord"/> is <see cref="FullSource"/> with the synthesised Section 1 narrative
/// removed. It still opens with the one-page Snapshot — headline figures and the deterministic
/// rule-engine Review Priority (High/Medium/Low), which is a classification, not an AI opinion — so
/// it is a "source record plus a factual cover page", not a zero-derivation dump.</summary>
public enum DossierVariant
{
    Executive,
    FullSource,
    SourceRecord
}
