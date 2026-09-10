using MCAROC_Analysis.Models.Dossier;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Renders a <see cref="DossierModel"/> as the client "Due Diligence Dossier" PDF — the visual
/// spec is <c>E:\Downloads\kaveri-infra-due-diligence-dossier.pdf</c> minus the risk score. Cover →
/// Contents → Snapshot → Section 1 (Executive Summary) → Annexures A–E.</summary>
public partial class DossierPdfComposer(DossierModel model, DossierVariant variant, byte[]? logo = null) : IDocument
{
    private DossierCover Cover => model.Cover;

    /// <summary>The canonical Cubictree entity line — cover footer + document metadata.</summary>
    internal const string AttributionLine =
        "Cubictree (a Gaba Projects Private Limited company)  •  CIN: U45201DL2012PTC239728  •  www.cubictree.com";

    private const string PreparedBy = "Cubictree (a Gaba Projects Private Limited company)";

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"{Cover.CompanyName} — Due Diligence Dossier",
        Author = PreparedBy,
        Subject = variant switch
        {
            DossierVariant.Executive => "Executive dossier",
            DossierVariant.FullSource => "Full source dossier",
            _ => "Source record dossier"
        }
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        // Cover: no running header, no page number — just the confidentiality note as a footer.
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginHorizontal(DossierTheme.PageMarginCm, Unit.Centimetre);
            page.MarginBottom(18);
            page.DefaultTextStyle(BaseText);
            page.Content().Element(ComposeCover);
            page.Footer().BorderTop(0.75f).BorderColor(DossierTheme.Line).PaddingTop(10).Column(foot =>
            {
                foot.Item().Text("Strictly Private & Confidential — prepared exclusively for the addressed institution's internal credit assessment. Not for onward circulation or use as a substitute for independent verification.")
                    .FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft).LineHeight(1.45f);
                foot.Item().PaddingTop(4).Text($"© {Cover.ReportDate:yyyy}   •   {AttributionLine}. All rights reserved.")
                    .FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
            });
        });

        // Body: running header + "Page X of Y" footer on every page.
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginVertical(DossierTheme.PageMarginCm, Unit.Centimetre);
            page.MarginHorizontal(DossierTheme.PageMarginCm, Unit.Centimetre);
            page.DefaultTextStyle(BaseText);

            page.Header().Element(RunningHeader);
            page.Footer().Element(RunningFooter);
            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Item().Section("contents").Element(ComposeContents);
                col.Item().PageBreak();
                col.Item().Section("snapshot").Element(ComposeSnapshot);

                if (variant != DossierVariant.SourceRecord)
                {
                    col.Item().PageBreak();
                    col.Item().Section("section-1").Element(ComposeSection1);
                }

                if (variant == DossierVariant.Executive)
                {
                    col.Item().PageBreak();
                    col.Item().Element(ComposeAnnexures);
                }
            });
        });

        // Full source / Source record: the verbatim Layer-0 record replaces the typed annexures.
        // Landscape — the widest MCA sheets carry ~18 columns and must not be shrunk or clipped.
        if (variant != DossierVariant.Executive)
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.MarginVertical(1.6f, Unit.Centimetre);
                page.MarginHorizontal(1.6f, Unit.Centimetre);
                page.DefaultTextStyle(BaseText);

                page.Header().Element(RunningHeader);
                page.Footer().Element(RunningFooter);
                page.Content().PaddingVertical(10).Section("annexure-source").Element(ComposeSourceRecords);
            });
        }
    }

    // ── shared text / element helpers ──────────────────────────────────────

    private static TextStyle BaseText(TextStyle t) => t
        .FontFamily(DossierTheme.Sans).FontSize(DossierTheme.Body)
        .FontColor(DossierTheme.Ink).LineHeight(1.4f);

    private void RunningHeader(IContainer c) => c.PaddingBottom(6).BorderBottom(0.75f).BorderColor(DossierTheme.Line)
        .Row(row =>
        {
            row.RelativeItem().Text(Cover.CompanyName).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft);
            row.RelativeItem().AlignRight().Text($"CIN {Cover.Cin ?? "—"}")
                .FontFamily(DossierTheme.Mono).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
        });

    private void RunningFooter(IContainer c) => c.PaddingTop(6).Row(row =>
    {
        row.RelativeItem().Text("Strictly Private & Confidential").FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
        row.RelativeItem().AlignRight().Text(t =>
        {
            t.DefaultTextStyle(x => x.FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint));
            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" of ");
            t.TotalPages();
        });
    });

    private void Kicker(IContainer c, string text) => c.PaddingBottom(2).Text(text.ToUpperInvariant())
        .FontFamily(DossierTheme.Sans).FontSize(DossierTheme.Kicker).FontColor(DossierTheme.Maroon)
        .LetterSpacing(0.08f);

    private void SectionTitle(IContainer c, string text) => c.PaddingBottom(4).Text(text)
        .FontFamily(DossierTheme.Display).FontSize(DossierTheme.SectionTitle).FontColor(DossierTheme.Ink);

    private void Lead(IContainer c, string text) => c.PaddingBottom(14).Text(text)
        .FontSize(DossierTheme.Body).FontColor(DossierTheme.InkSoft).LineHeight(1.5f);

    private void SubHead(IContainer c, string text) => c.PaddingTop(18).PaddingBottom(6)
        .Text(text).FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading).FontColor(DossierTheme.Ink);

    internal static string Money(decimal? v) => v is null ? "—" : $"₹{v.Value:N2} Cr";
    internal static string D(DateOnly? d) => d?.ToString("dd MMM, yyyy") ?? "-";

    private static string SevColour(Data.Entities.FindingSeverity s) => s switch
    {
        Data.Entities.FindingSeverity.Critical => DossierTheme.Maroon,
        Data.Entities.FindingSeverity.Review => DossierTheme.Amber,
        Data.Entities.FindingSeverity.Watch => DossierTheme.InkSoft,
        _ => DossierTheme.Sage
    };

    private static string SevWash(Data.Entities.FindingSeverity s) => s switch
    {
        Data.Entities.FindingSeverity.Critical => DossierTheme.MaroonWash,
        Data.Entities.FindingSeverity.Review => DossierTheme.AmberWash,
        _ => DossierTheme.PaperRaised
    };

    /// <summary>A stat tile — kicker label above a large serif value.</summary>
    private void StatTile(IContainer c, string label, string value, bool accent = false) =>
        c.Border(0.75f).BorderColor(DossierTheme.Line).BorderTop(2f)
            .BorderColor(accent ? DossierTheme.Maroon : DossierTheme.Line)
            .Padding(11).Column(col =>
        {
            col.Item().Text(value).FontFamily(DossierTheme.Display).FontSize(19)
                .FontColor(accent ? DossierTheme.MaroonDeep : DossierTheme.Ink);
            col.Item().PaddingTop(3).Text(label.ToUpperInvariant())
                .FontSize(DossierTheme.Kicker).FontColor(DossierTheme.InkFaint).LetterSpacing(0.05f);
        });

    /// <summary>A dark-header key/value mini-table.</summary>
    private void KvBlock(IContainer c, string title, IEnumerable<(string Key, string Value)> rows) =>
        c.Border(0.75f).BorderColor(DossierTheme.Line).Column(col =>
        {
            col.Item().Background(DossierTheme.Ink).PaddingVertical(6).PaddingHorizontal(11)
                .Text(title).FontFamily(DossierTheme.Display).FontSize(11.5f).FontColor("#FFFFFF");
            foreach (var (k, v) in rows)
                col.Item().BorderBottom(0.5f).BorderColor(DossierTheme.LineSoft)
                    .PaddingVertical(5).PaddingHorizontal(11).Row(r =>
                {
                    r.RelativeItem().Text(k).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft);
                    r.ConstantItem(130).AlignRight().Text(v).FontSize(DossierTheme.Small).SemiBold();
                });
        });

    // ── Cover ──────────────────────────────────────────────────────────────

    private void ComposeCover(IContainer container)
    {
        container.Column(col =>
        {
            if (logo is { Length: > 0 })
                col.Item().PaddingTop(1.6f, Unit.Centimetre).Width(46).Image(logo);
            else
                col.Item().PaddingTop(1.6f, Unit.Centimetre)
                    .Width(48).Height(48).Background(DossierTheme.Maroon).AlignCenter().AlignMiddle()
                    .Text("CT").FontFamily(DossierTheme.Display).FontSize(16).Bold().FontColor("#FFFFFF");

            col.Item().PaddingTop(4.4f, Unit.Centimetre).Element(c => Kicker(c, "Due Diligence Dossier"));
            col.Item().PaddingBottom(30).Text(Cover.CompanyName)
                .FontFamily(DossierTheme.Display).FontSize(DossierTheme.CoverTitle).FontColor(DossierTheme.Ink);

            col.Item().Element(c => CoverField(c, "CIN", Cover.Cin ?? "—", mono: true));
            col.Item().Element(c => CoverField(c, "PAN", Cover.Pan ?? "—", mono: true));
            col.Item().Element(c => CoverField(c, "Report date", Cover.ReportDate.ToString("dd MMMM yyyy")));
            col.Item().Element(c => CoverField(c, "Prepared for", string.IsNullOrWhiteSpace(Cover.ClientName) ? "[Client Institution Name]" : Cover.ClientName));
            col.Item().Element(c => CoverField(c, "Prepared by", PreparedBy));
        });
    }

    private void CoverField(IContainer c, string label, string value, bool mono = false) => c.PaddingBottom(5).Row(row =>
    {
        row.ConstantItem(110).Text(label).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
        var v = row.RelativeItem().Text(value).FontSize(DossierTheme.Body).FontColor(DossierTheme.Ink);
        if (mono) v.FontFamily(DossierTheme.Mono);
    });

    // ── Contents ───────────────────────────────────────────────────────────

    private void ComposeContents(IContainer container)
    {
        var sourceOnly = variant == DossierVariant.SourceRecord;
        var raw = variant != DossierVariant.Executive;

        container.Column(col =>
        {
            col.Item().Element(c => Kicker(c, "Quick View"));
            col.Item().Element(c => SectionTitle(c, "Contents"));
            col.Item().Element(c => Lead(c, sourceOnly
                ? "This dossier pairs a one-page Snapshot with the complete source record — every non-blank " +
                  "row of every worksheet of both MCA workbooks, reproduced exactly as extracted, with no synthesis."
                : raw
                    ? "This dossier pairs a one-page Snapshot and a synthesised Executive Summary with the complete " +
                      "verbatim source record — every non-blank row of every worksheet of both MCA workbooks."
                    : "This dossier pairs a one-page Snapshot and a synthesised Executive Summary with concise " +
                      "source-record annexures — every flag references the annexure and record it was drawn from."));

            ContentsLine(col, "Snapshot", "snapshot");
            if (!sourceOnly)
                ContentsLine(col, "1. Executive Summary", "section-1");

            if (raw)
            {
                col.Item().PaddingTop(10).PaddingBottom(4).Text("Annexure — source data")
                    .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
                ContentsLine(col, "Source Records — every workbook row, verbatim", "annexure-source");
            }
            else
            {
                col.Item().PaddingTop(10).PaddingBottom(4).Text("Annexures — source data")
                    .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
                ContentsLine(col, "Annexure A — Corporate", "annexure-a");
                ContentsLine(col, "Annexure B — Financials", "annexure-b");
                ContentsLine(col, "Annexure C — Charges & Security", "annexure-c");
                ContentsLine(col, "Annexure D — Compliance", "annexure-d");
                ContentsLine(col, "Annexure E — Litigation", "annexure-e");
            }

            col.Item().PaddingTop(18).Background(DossierTheme.PaperRaised).Border(0.75f).BorderColor(DossierTheme.Line)
                .BorderLeft(2.5f).BorderColor(DossierTheme.Maroon).Padding(14).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft).LineHeight(1.5f));
                t.Span("How to read this dossier: ").Bold();
                if (sourceOnly)
                    t.Span("Snapshot is a 10-second scan of the numbers that matter, each a direct pull from the " +
                        "record that follows. The Source Records annexure is the complete system of record — every " +
                        "row of every worksheet is reproduced exactly as extracted, with no interpretation applied.");
                else if (raw)
                    t.Span("Snapshot is a 10-second scan of the numbers that matter. Section 1 is a synthesised " +
                        "view — it draws conclusions across the source record and is not itself a source record. " +
                        "The Source Records annexure reproduces every workbook row verbatim; treat it as the system of record.");
                else
                    t.Span("Snapshot is a 10-second scan of the numbers that matter. Section 1 is a synthesised " +
                        "view — it draws conclusions across every annexure and is not itself a source record. Each flag " +
                        "cites the annexure and item it was drawn from; treat the annexures as the system of record.");
            });
        });
    }

    private void ContentsLine(ColumnDescriptor col, string label, string section) =>
        col.Item().PaddingVertical(5).BorderBottom(0.5f).BorderColor(DossierTheme.LineSoft).Row(row =>
        {
            row.RelativeItem().Text(label).FontSize(DossierTheme.Body);
            row.ConstantItem(36).AlignRight().Text(t => t.BeginPageNumberOfSection(section)
                .FontSize(DossierTheme.Body).FontColor(DossierTheme.InkFaint));
        });
}
