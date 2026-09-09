using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Snapshot / Section 1 / Annexures — filled in over Phase 7 commits A5–A8. Stubs keep the
/// document composable and page-numbered from A4.</summary>
public partial class DossierPdfComposer
{
    private void ComposeSnapshot(IContainer container) => Stub(container, "Quick View", "Snapshot");

    private void ComposeSection1(IContainer container) => Stub(container, "Section 1", "Executive Summary");

    private void ComposeAnnexures(IContainer container) => container.Column(col =>
    {
        foreach (var (key, kicker, title) in new[]
        {
            ("annexure-a", "Annexure A", "Corporate"),
            ("annexure-b", "Annexure B", "Financials"),
            ("annexure-c", "Annexure C", "Charges & Security"),
            ("annexure-d", "Annexure D", "Compliance"),
            ("annexure-e", "Annexure E", "Litigation"),
        })
        {
            col.Item().Section(key).Element(c => Stub(c, kicker, title));
            if (key != "annexure-e") col.Item().PageBreak();
        }
    });

    private void Stub(IContainer container, string kicker, string title) => container.Column(col =>
    {
        col.Item().Element(c => Kicker(c, kicker));
        col.Item().Element(c => SectionTitle(c, title));
        col.Item().PaddingTop(6).Text("(content to follow)").FontColor(DossierTheme.InkFaint);
    });
}
