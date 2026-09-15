namespace MCAROC_Analysis.Services.Dossier;

/// <summary>The client PDF flavour. <see cref="Executive"/> is the single combined dossier — cover,
/// contents, snapshot, Section 1, and the typed annexures A–E. The former FullSource/SourceRecord
/// variants (a verbatim Layer-0 worksheet-cell dump, up to 200+ pages) were removed — the portal's
/// per-domain tabs (Corporate/Financials/Charges/Compliance/Litigation) already give well-formatted,
/// section-wise access to the same underlying data, which is what that raw dump existed for.</summary>
public enum DossierVariant
{
    Executive
}
