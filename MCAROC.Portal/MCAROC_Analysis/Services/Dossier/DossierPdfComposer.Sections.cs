using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Snapshot + Section 1. Annexures A–E filled in A7–A8.</summary>
public partial class DossierPdfComposer
{
    // ── Snapshot ───────────────────────────────────────────────────────────

    private void ComposeSnapshot(IContainer container) => container.Column(col =>
    {
        col.Item().Element(c => Kicker(c, "Quick View"));
        col.Item().Element(c => SectionTitle(c, "Snapshot"));
        col.Item().Element(c => Lead(c,
            "Apart from the Review Priority — a deterministic rule-engine classification — every figure on this page " +
            "is a direct pull from a source record, with no synthesis or interpretation." +
            (variant == DossierVariant.SourceRecord ? "" : " For the reasoning across the annexures, see Section 1.")));

        var f = model.Financials;
        var ch = model.Charges;
        var lit = model.Litigation;
        var priority = model.ExecSummary.ReviewPriority?.ToString().ToUpperInvariant() ?? "NOT ASSESSED";
        var insolvency = lit.All.Count(l =>
            (l.CaseCategory ?? "").Contains("insolv", StringComparison.OrdinalIgnoreCase) && DossierComputations.IsPendingLitigation(l));

        col.Item().PaddingTop(4).Row(row =>
        {
            row.RelativeItem().Element(c => StatTile(c, "Review priority", priority, accent: true));
            row.ConstantItem(10);
            row.RelativeItem().Element(c => StatTile(c, $"Total equity, FY{f.LatestYear}", NumOrDash(f.Latest?.NetWorth)));
            row.ConstantItem(10);
            row.RelativeItem().Element(c => StatTile(c, "Open charge amount, ₹ Cr", $"{ch.TotalOpenAmount:N0}"));
            row.ConstantItem(10);
            row.RelativeItem().Element(c => StatTile(c, "Active insolvency cases", insolvency.ToString()));
        });

        col.Item().PaddingTop(16).Row(row =>
        {
            row.RelativeItem().Element(c => KvBlock(c, "Company identity", new[]
            {
                ("Legal name", model.Cover.CompanyName),
                ("CIN", model.Cover.Cin ?? "—"),
                ("PAN", model.Cover.Pan ?? "—"),
                ("Incorporated", D(model.Cover.IncorporationDate)),
                ("ROC status", model.Cover.Status ?? "—"),
            }));
            row.ConstantItem(12);
            row.RelativeItem().Element(c => KvBlock(c, "Corporate", new[]
            {
                ("Directors on record", model.Corporate.Directors.Count.ToString()),
                ("Active directors", model.Corporate.ActiveDirectorCount.ToString()),
                ("Non-DIN officers", model.Corporate.Officers.Count.ToString()),
                ("Related corporates", model.Corporate.RelatedCorporates.Count.ToString()),
                ("Last allotment", D(model.Corporate.SecurityAllotments.FirstOrDefault()?.AllotmentDate)),
            }));
        });

        col.Item().PaddingTop(12).Row(row =>
        {
            row.RelativeItem().Element(c => KvBlock(c, "Charges & security", new[]
            {
                ("Open charges", ch.OpenCount.ToString()),
                ("Satisfied charges", ch.SatisfiedCount.ToString()),
                ("Total open charge amount", Money(ch.TotalOpenAmount)),
                ("Charge holders", ch.HolderCount.ToString()),
            }));
            row.ConstantItem(12);
            row.RelativeItem().Element(c => KvBlock(c, $"Financials (Standalone, FY{f.LatestYear})", new[]
            {
                ("Total Equity", NumOrDash(f.Latest?.NetWorth)),
                ("Revenue", NumOrDash(f.Latest?.Revenue)),
                ("PAT", NumOrDash(f.Latest?.Pat)),
                ("Total Debt", NumOrDash(f.Latest?.TotalDebt)),
                ("Consolidated Total Equity", NumOrDash(f.Consolidated.OrderBy(x => x.FinancialYear).LastOrDefault()?.NetWorth)),
            }));
        });

        col.Item().PaddingTop(12).Row(row =>
        {
            var compliance = model.Compliance;
            row.RelativeItem().Element(c => KvBlock(c, "Compliance", new[]
            {
                ("MCA / regulatory records", compliance.Records.Count.ToString()),
                ("Suit-filed groups", compliance.SuitFiledSummary.Count.ToString()),
                ("GST registrations", compliance.Gst.Count.ToString()),
                ("EPFO months on record", compliance.Epfo.Count.ToString()),
                ("MSME dues rows", compliance.Msme.Count.ToString()),
            }));
            row.ConstantItem(12);
            row.RelativeItem().Element(c => KvBlock(c, "Litigation", new[]
            {
                ("Total cases", lit.All.Count.ToString()),
                ("Pending", lit.PendingCount.ToString()),
                ("Disposed", lit.DisposedCount.ToString()),
                ("Filed against company", lit.FiledAgainstCount.ToString()),
                ("Role not determined", lit.NotDeterminedCount.ToString()),
            }));
        });
    });

    private static string NumOrDash(decimal? v) => v is null ? "—" : $"₹{v.Value:N1} Cr";

    // ── Section 1 — Executive Summary ──────────────────────────────────────

    private void ComposeSection1(IContainer container) => container.Column(col =>
    {
        var es = model.ExecSummary;

        col.Item().Element(c => Kicker(c, "Section 1"));
        col.Item().Element(c => SectionTitle(c, "Executive Summary"));
        col.Item().Element(c => Lead(c,
            "A synthesised read across Corporate, Financials, Charges & Security, Compliance and " +
            "Litigation, generated by Cubictree's deterministic rule engine. Figures in ₹ Crore unless stated."));

        // Flag-count strip (replaces the sample's 0–100 score bar).
        col.Item().Background(DossierTheme.PaperRaised).Border(0.75f).BorderColor(DossierTheme.Line)
            .BorderLeft(2.5f).BorderColor(DossierTheme.Maroon).Padding(11).Text(t =>
        {
            t.DefaultTextStyle(x => x.FontSize(DossierTheme.Body));
            t.Span($"{es.CriticalCount} Critical").FontColor(DossierTheme.MaroonDeep).SemiBold();
            t.Span("   ·   ");
            t.Span($"{es.ReviewCount} Review").FontColor(DossierTheme.Amber).SemiBold();
            t.Span("   ·   ");
            t.Span($"{es.WatchCount} Watch").FontColor(DossierTheme.InkSoft);
            t.Span("   ·   ");
            t.Span($"{es.PositiveCount} Positive").FontColor(DossierTheme.Sage);
            t.Span("        Review Priority: ");
            t.Span(es.ReviewPriority?.ToString() ?? "Not assessed").SemiBold();
        });

        // Key risk flags — Critical + Review findings.
        var flags = es.FindingsInDisplayOrder
            .Where(x => x.Severity is FindingSeverity.Critical or FindingSeverity.Review).ToList();
        if (flags.Count > 0)
        {
            col.Item().Element(c => SubHead(c, "Key risk flags"));
            foreach (var flag in flags)
                col.Item().PaddingBottom(9).Element(c => FlagCard(c, flag));
        }

        // Additional observations — Watch + Positive.
        var obs = es.FindingsInDisplayOrder
            .Where(x => x.Severity is FindingSeverity.Watch or FindingSeverity.Positive).ToList();
        if (obs.Count > 0)
        {
            col.Item().Element(c => SubHead(c, "Additional observations"));
            foreach (var o in obs)
                col.Item().PaddingBottom(5).Row(r =>
                {
                    r.ConstantItem(14).Text("•").FontColor(DossierTheme.Maroon);
                    r.RelativeItem().Text(t =>
                    {
                        t.Span(o.Title + ". ").SemiBold().FontSize(DossierTheme.Small);
                        t.Span(o.SummaryText).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft);
                        t.Span($"  ({AnnexureRef(o.Section)})").FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
                    });
                });
        }

        // 5-slot structured summary as labelled statements (not one paragraph).
        if (es.Structured is { } s)
        {
            col.Item().Element(c => SubHead(c, "Cross-section read"));
            LabelledStatement(col, "Business performance", s.BusinessPerformance);
            LabelledStatement(col, "Financial position", s.FinancialPosition);
            LabelledStatement(col, "Borrowing & security", s.BorrowingSecurity);
            LabelledStatement(col, "Governance & compliance", s.GovernanceCompliance);
            if (s.KeyReviewItems.Count > 0)
            {
                col.Item().PaddingTop(6).Text("Key review items").SemiBold().FontSize(DossierTheme.Small);
                foreach (var item in s.KeyReviewItems)
                    col.Item().PaddingLeft(14).PaddingTop(2).Text("• " + item).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft);
            }
        }

        // Key indicators trend table.
        var std = model.Financials.Standalone.OrderBy(x => x.FinancialYear).ToList();
        if (std.Count >= 2)
        {
            col.Item().Element(c => SubHead(c, "Key indicators"));
            col.Item().Element(c => IndicatorTable(c, std));
        }

        // Methodology note.
        col.Item().PaddingTop(14).Text(t =>
        {
            t.DefaultTextStyle(x => x.FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint).LineHeight(1.5f));
            t.Span("Methodology note. ").Italic().SemiBold();
            t.Span("Flags are generated by rule-based conditions applied to the indexed source records " +
                "(e.g. Case Category = \"Insolvency\" AND Case Status = \"Pending\"; Total Equity < ₹0 Cr). " +
                "Cash-flow figures whose year alignment is inferred are excluded from automated conclusions. " +
                "This summary reflects the state of the source records as of the \"MCA data updated\" date on the cover.");
        });
    });

    private void FlagCard(IContainer c, AnalysisFinding f) => c
        .Background(SevWash(f.Severity)).Border(0.75f).BorderColor(DossierTheme.Line)
        .BorderLeft(2.5f).BorderColor(SevColour(f.Severity)).Padding(11).Column(col =>
    {
        col.Item().Row(r =>
        {
            r.RelativeItem().Text(f.Title).FontFamily(DossierTheme.Display).FontSize(12.5f).FontColor(DossierTheme.Ink);
            r.ConstantItem(64).AlignRight().Element(p => Pill(p, f.Severity.ToString(), SevColour(f.Severity)));
        });
        col.Item().PaddingTop(3).Text(f.SummaryText).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft).LineHeight(1.45f);
        if (!string.IsNullOrWhiteSpace(f.WhyThisMatters))
            col.Item().PaddingTop(3).Text(f.WhyThisMatters).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft).LineHeight(1.45f);
        col.Item().PaddingTop(5).Text(t => t.SectionLink($"See {AnnexureRef(f.Section)}", AnnexureSection(f.Section))
            .FontFamily(DossierTheme.Mono).FontSize(DossierTheme.Small).FontColor(DossierTheme.MaroonDeep));
    });

    private void Pill(IContainer c, string text, string colour) =>
        c.Background(colour).PaddingVertical(2).PaddingHorizontal(7)
            .Text(text).FontSize(9f).FontColor("#FFFFFF");

    private void LabelledStatement(ColumnDescriptor col, string label, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        col.Item().PaddingBottom(6).Text(t =>
        {
            t.Span(label + ": ").SemiBold().FontFamily(DossierTheme.Display).FontSize(DossierTheme.Body);
            t.Span(text).FontSize(DossierTheme.Body).FontColor(DossierTheme.InkSoft).LineHeight(1.5f);
        });
    }

    private void IndicatorTable(IContainer c, List<FinancialYearData> stdAsc)
    {
        var years = stdAsc.TakeLast(3).ToList();
        (string Label, Func<FinancialYearData, decimal?> V)[] rows =
        [
            ("Total Equity (₹ Cr)", f => f.NetWorth),
            ("Revenue (₹ Cr)", f => f.Revenue),
            ("PAT (₹ Cr)", f => f.Pat),
            ("Total Debt (₹ Cr)", f => f.TotalDebt),
        ];

        c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.RelativeColumn(2.2f);
                foreach (var _ in years) cd.RelativeColumn();
                cd.RelativeColumn(1.3f);
            });
            table.Header(h =>
            {
                HeaderCell(h.Cell(), "Indicator");
                foreach (var y in years) HeaderCell(h.Cell(), $"FY{y.FinancialYear}");
                HeaderCell(h.Cell(), "Trend");
            });
            foreach (var (label, sel) in rows)
            {
                BodyCell(table.Cell(), label);
                foreach (var y in years) BodyCell(table.Cell(), sel(y)?.ToString("N1") ?? "-", right: true);
                BodyCell(table.Cell(), Trend(years.Select(sel).ToList()));
            }
        });
    }

    private static string Trend(List<decimal?> series)
    {
        var vals = series.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (vals.Count < 2) return "—";
        var delta = vals[^1] - vals[0];
        if (Math.Abs(delta) < Math.Max(0.01m, Math.Abs(vals[0]) * 0.02m)) return "Stable";
        return delta > 0 ? "↑ Rising" : "↓ Declining";
    }

    private void HeaderCell(IContainer c, string text) => c
        .BorderBottom(0.75f).BorderColor(DossierTheme.Line).PaddingVertical(5).PaddingHorizontal(6)
        .Text(text).FontSize(DossierTheme.TableHeader).FontColor(DossierTheme.InkFaint);

    private void BodyCell(IContainer c, string text, bool right = false)
    {
        var cell = c.BorderBottom(0.5f).BorderColor(DossierTheme.LineSoft).PaddingVertical(4).PaddingHorizontal(6);
        var t = (right ? cell.AlignRight() : cell).Text(text).FontSize(DossierTheme.TableCell);
        if (right) t.FontFamily(DossierTheme.Mono);
    }

    private static string AnnexureRef(FindingSection s) => s switch
    {
        FindingSection.CompanyProfile or FindingSection.Directors or FindingSection.DirectorNetwork or FindingSection.Ownership => "Annexure A — Corporate",
        FindingSection.Financial => "Annexure B — Financials",
        FindingSection.Charges => "Annexure C — Charges & Security",
        FindingSection.Msme or FindingSection.Gst or FindingSection.Epfo or FindingSection.Auditor => "Annexure D — Compliance",
        FindingSection.Litigation => "Annexure E — Litigation",
        _ => "the annexures"
    };

    private static string AnnexureSection(FindingSection s) => s switch
    {
        FindingSection.CompanyProfile or FindingSection.Directors or FindingSection.DirectorNetwork or FindingSection.Ownership => "annexure-a",
        FindingSection.Financial => "annexure-b",
        FindingSection.Charges => "annexure-c",
        FindingSection.Msme or FindingSection.Gst or FindingSection.Epfo or FindingSection.Auditor => "annexure-d",
        FindingSection.Litigation => "annexure-e",
        _ => "contents"
    };

}
