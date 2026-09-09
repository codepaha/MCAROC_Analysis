namespace MCAROC_Analysis.Services.Dossier;

/// <summary>The two client PDF flavours. <see cref="Executive"/> is the ~20-page orientation dossier
/// (cover, contents, snapshot, Section 1, key-rows-only annexures); <see cref="FullSource"/> carries
/// every de-duplicated source row (up to 200+ pages).</summary>
public enum DossierVariant
{
    Executive,
    FullSource
}
