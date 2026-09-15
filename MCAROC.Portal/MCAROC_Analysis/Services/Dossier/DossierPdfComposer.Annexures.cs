using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Sections 2-6 — the full source record, every row rendered (no per-table cap — the PDF is the
/// one downloadable artifact a reviewer can rely on for complete data since the FullSource/SourceRecord
/// variants were removed). #197: the "Annexure A-E" lettering is gone from what renders — each section
/// now carries a plain name and a number continuing from Section 1 (Executive Summary), plus an "At a
/// glance" interpretive line before its tables. The method names below (AnnexureA..E) are unchanged
/// internally to keep this diff about content, not identifiers; anchor ids (annexure-a..e) are likewise
/// unchanged.</summary>
public partial class DossierPdfComposer
{
    // Order: Financial Profile, Borrowing & Security, Directors & Governance, Statutory Compliance,
    // Litigation. Litigation is placed last deliberately — a litigation-specific redesign is planned
    // separately, and this keeps that future work from forcing a renumber of every section after it.
    private void ComposeAnnexures(IContainer container) => container.Column(col =>
    {
        col.Item().Section("annexure-b").Element(AnnexureB);
        col.Item().PageBreak();
        col.Item().Section("annexure-c").Element(AnnexureC);
        col.Item().PageBreak();
        col.Item().Section("annexure-a").Element(AnnexureA);
        col.Item().PageBreak();
        col.Item().Section("annexure-d").Element(AnnexureD);
        col.Item().PageBreak();
        col.Item().Section("annexure-e").Element(AnnexureE);
        if (HasCoverageGap)
        {
            col.Item().PageBreak();
            col.Item().Section("annexure-f").Element(AnnexureF);
        }
    });

    // ── generic table ─────────────────────────────────────────────────────

    private sealed record Col<T>(string Header, float Weight, Func<T, string> Cell, bool Right = false);

    private void AnnexureHead(ColumnDescriptor col, string kicker, string title, string scope)
    {
        col.Item().Element(c => Kicker(c, kicker));
        col.Item().Element(c => SectionTitle(c, title));
        col.Item().Element(c => Lead(c, scope));
    }

    /// <summary>A short, computed "so what" line before a section's tables — the interpretation an
    /// analyst would otherwise have to derive themselves from the raw rows below.</summary>
    private void AtAGlance(ColumnDescriptor col, string text) =>
        col.Item().PaddingBottom(14).Background(DossierTheme.PaperRaised).Border(0.75f).BorderColor(DossierTheme.Line)
            .BorderLeft(2.5f).BorderColor(DossierTheme.Maroon).Padding(11).Text(t =>
        {
            t.DefaultTextStyle(x => x.FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft).LineHeight(1.5f));
            t.Span("At a glance: ").Bold().FontColor(DossierTheme.Ink);
            t.Span(text);
        });

    /// <summary>Looks up one already-computed <see cref="MetricResult"/> by its group and label, so a
    /// section's "At a glance" line reuses the same proven computation the (retired) Key Indicators
    /// block used to render wholesale, rather than re-deriving it and risking a subtly different number.</summary>
    private static MetricResult? FindMetric(IReadOnlyList<MetricGroup> groups, string groupTitle, string label) =>
        groups.FirstOrDefault(g => g.Title == groupTitle)?.Metrics.FirstOrDefault(m => m.Label == label);

    private void Item<T>(ColumnDescriptor col, ref int n, string caption,
        IReadOnlyList<T> rows, params Col<T>[] cols) =>
        Item(col, ref n, caption, rows, "No records on file.", cols);

    /// <summary>Overload that names which source sheet(s) feed this table, so an empty result says
    /// exactly why — "not provided in this upload" (the reviewer needs to go get it) versus the neutral
    /// default (present and genuinely empty) — right where the reviewer is already looking, instead of a
    /// separate global list of internal sheet names an addressee-side reader has no context for.</summary>
    private void Item<T>(ColumnDescriptor col, ref int n, string caption,
        IReadOnlyList<T> rows, IReadOnlyList<string>[] sheets, params Col<T>[] cols) =>
        Item(col, ref n, caption, rows, model.SourceCoverage.EmptyState("No records on file.", sheets), cols);

    private void Item<T>(ColumnDescriptor col, ref int n, string caption,
        IReadOnlyList<T> rows, string emptyText, params Col<T>[] cols)
    {
        n++;

        col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — {caption}")
            .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);

        if (rows.Count == 0)
        {
            col.Item().Text(emptyText).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
            return;
        }

        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cd => { foreach (var c in cols) cd.RelativeColumn(c.Weight); });
            table.Header(h => { foreach (var c in cols) HeaderCell(h.Cell(), c.Header, c.Right); });
            for (var ri = 0; ri < rows.Count; ri++)
            {
                var r = rows[ri];
                var shaded = ri % 2 == 1;
                foreach (var c in cols)
                    BodyCell(table.Cell(), c.Cell(r), c.Right && c.Cell(r) is not "-" and not "", shaded);
            }
        });
    }

    // ── A. Corporate ──────────────────────────────────────────────────────

    private void AnnexureA(IContainer container) => container.Column(col =>
    {
        var c = model.Corporate;
        AnnexureHead(col, "Section 4", "Directors & Governance",
            "Full source record — the directors register, officers, related corporates, shareholding, " +
            "capital history and other directorships, matching the columns captured on the MCA extract.");

        var tenure = FindMetric(model.Metrics, "Directors", "Average board tenure");
        var longest = FindMetric(model.Metrics, "Directors", "Longest-serving director");
        AtAGlance(col, string.Join(" ", new[]
        {
            $"{c.ActiveDirectorCount} of {c.Directors.Count} director(s) on record are active.",
            tenure?.HasValue == true ? $"Average board tenure is {tenure.DisplayValue()}." : null,
            longest?.HasValue == true ? $"Longest-serving director: {longest.Period}, {longest.DisplayValue()} on the board." : null,
        }.Where(s => s is not null)));

        var n = 0;
        Item(col, ref n, "Directors register", c.Directors, [SheetAliases.Directors],
            new Col<Director>("Name", 2.2f, d => d.NameRaw),
            new Col<Director>("DIN", 1.1f, d => d.Din),
            new Col<Director>("Present designation", 1.7f, d => d.Designation ?? "-"),
            new Col<Director>("Appointed", 1.1f, d => D(d.OriginalAppointmentDate), true),
            new Col<Director>("Cessation", 1.1f, d => D(d.CessationDate), true));

        if (c.Officers.Count > 0)
            Item(col, ref n, "Officers without a DIN (company secretary / KMP)", c.Officers,
                new Col<CompanyOfficer>("Name", 2.4f, o => o.NameRaw),
                new Col<CompanyOfficer>("Designation", 1.8f, o => o.Designation ?? "-"),
                new Col<CompanyOfficer>("Appointed", 1.2f, o => D(o.OriginalAppointmentDate), true),
                new Col<CompanyOfficer>("Cessation", 1.2f, o => D(o.CessationDate), true));

        Item(col, ref n, "Related corporates", c.RelatedCorporates, [SheetAliases.RelatedCorporates],
            new Col<RelatedCorporate>("Entity", 2.4f, r => r.EntityNameRaw),
            new Col<RelatedCorporate>("Relationship", 1.3f, r => r.RelationshipType.ToString()),
            new Col<RelatedCorporate>("Holding %", 1f, r => r.HoldingPercent?.ToString("0.##") ?? "-", true),
            new Col<RelatedCorporate>("Status", 1.2f, r => r.CompanyStatus ?? "-"),
            new Col<RelatedCorporate>("Location", 1.4f, r => r.Location ?? "-"));

        Item(col, ref n, "Shareholding above 5%", c.Shareholders,
            [SheetAliases.DirectorShareholding, SheetAliases.MajorShareholding],
            new Col<Shareholding>("FY", 0.7f, s => s.FinancialYear.ToString()),
            new Col<Shareholding>("Shareholder", 2.4f, s => s.ShareholderNameRaw),
            new Col<Shareholding>("Type", 1.1f, s => s.ShareholderType ?? "-"),
            new Col<Shareholding>("Promoter", 0.9f, s => s.IsPromoter ? "Yes" : "-"),
            new Col<Shareholding>("% held", 1f, s => s.HoldingPercentage?.ToString("0.##") ?? "-", true));

        Item(col, ref n, "Securities allotment", c.SecurityAllotments, [SheetAliases.SecuritiesAllotment],
            new Col<SecurityAllotment>("Date", 1.1f, a => D(a.AllotmentDate), true),
            new Col<SecurityAllotment>("Type", 1.2f, a => a.AllotmentType ?? "-"),
            new Col<SecurityAllotment>("Instrument", 2.2f, a => a.InstrumentType ?? "-"),
            new Col<SecurityAllotment>("Amount ₹Cr", 1.1f, a => a.AmountCrore?.ToString("0.##") ?? "-", true),
            new Col<SecurityAllotment>("Securities", 1.2f, a => a.NumberOfSecurities?.ToString("N0") ?? "-", true));

        Item(col, ref n, "Designation history at this company", c.DesignationHistory,
            [SheetAliases.DirectorAssociationHistory],
            new Col<DirectorAssignmentHistory>("Director", 2.2f, h => h.DirectorNameRaw),
            new Col<DirectorAssignmentHistory>("Designation", 1.8f, h => h.Designation ?? "-"),
            new Col<DirectorAssignmentHistory>("Appointed", 1.2f, h => D(h.AppointmentDate), true),
            new Col<DirectorAssignmentHistory>("Ceased", 1.2f, h => D(h.CessationDate), true));

        Item(col, ref n, "Other directorships", c.OtherDirectorships, [SheetAliases.OtherDirectorships],
            new Col<DirectorAssociation>("Director", 2f, a => a.DirectorNameRaw),
            new Col<DirectorAssociation>("Company", 2.4f, a => a.ConnectedCompanyRaw),
            new Col<DirectorAssociation>("CIN", 1.6f, a => a.ConnectedCin ?? "-"),
            new Col<DirectorAssociation>("Status", 1.2f, a => a.CompanyStatus ?? "-"));
    });

    // ── B. Financials ─────────────────────────────────────────────────────

    private void AnnexureB(IContainer container) => container.Column(col =>
    {
        var f = model.Financials;
        AnnexureHead(col, "Section 2", "Financial Profile",
            "Standalone and consolidated financial data as filed, the ratios, the auditor's comments " +
            "and the peer comparison.");

        var latest = f.Latest;
        var currentRatio = latest?.CurrentAssets is { } ca && latest.CurrentLiabilities is { } cl and not 0m
            ? ca / cl : (decimal?)null;
        string? revenueTrend = f.RevenueYoYPercent switch
        {
            null => null,
            > 0 => $" ({f.RevenueYoYPercent:0.#}% year-on-year growth)",
            < 0 => $" ({Math.Abs(f.RevenueYoYPercent.Value):0.#}% year-on-year decline)",
            _ => " (flat year-on-year)"
        };
        AtAGlance(col, string.Join(" ", new[]
        {
            latest?.Revenue is { } rev ? $"FY{f.LatestYear} revenue was ₹{rev:N1} Cr{revenueTrend}." : null,
            latest?.Pat is { } pat ? $"PAT was ₹{pat:N1} Cr." : null,
            currentRatio is { } cr ? $"Current ratio: {cr:0.00}x." : null,
        }.Where(s => s is not null)));

        var n = 0;
        n++;
        col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — Standalone financial data (₹ Crore)")
            .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
        FinancialStatementTable(col, f.Standalone,
            model.SourceCoverage.EmptyState("No financial data extracted.", [SheetAliases.StandaloneFinancialData]));

        if (f.Consolidated.Count > 0)
        {
            n++;
            col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — Consolidated financial data (₹ Crore)")
                .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
            FinancialStatementTable(col, f.Consolidated);
        }

        // #152/#197: the source-reported ratios (catalogue A1.x) get their own multi-year block —
        // "render as-is with a multi-year sparkline; do not recompute" — rather than sitting in the flat
        // "Additional line items" catch-all below, which is what #152 flagged as insufficient. Excluded
        // from that catch-all's rows so the same ratio never appears in both places.
        var ratioFacts = f.Facts.Where(x => x.Section == FinancialStatementSection.Ratios).ToList();
        if (ratioFacts.Count > 0)
        {
            n++;
            col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — Ratios, as reported")
                .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
            col.Item().PaddingBottom(6).Text(
                "Rendered exactly as filed across every year on record — not recomputed.")
                .FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
            RatiosTable(col, ratioFacts);
        }

        n++;
        col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — Additional line items (not in the summary above)")
            .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
        var extraFacts = f.Facts.Where(x => x.Section != FinancialStatementSection.Ratios).ToList();
        if (extraFacts.Count == 0)
            col.Item().Text("No additional line items.").FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
        else
            AdditionalLineItemsTables(col, extraFacts);

        Item(col, ref n, "Auditor's comments", f.AuditorObservations,
            [SheetAliases.Auditors, SheetAliases.AuditorsConsolidated],
            new Col<AuditorObservation>("FY", 0.7f, a => a.FinancialYear.ToString()),
            new Col<AuditorObservation>("Basis", 1f, a => a.Basis.ToString()),
            new Col<AuditorObservation>("Qualified / adverse", 1.4f, a => a.HasQualificationOrAdverseRemark ? "Yes" : "No"),
            new Col<AuditorObservation>("Comment", 4f, AuditorComment));

        Item(col, ref n, "Peer comparison", f.PeerComparison, [SheetAliases.PeerComparison],
            new Col<PeerComparisonMetric>("Metric", 2.6f, p => p.MetricName),
            new Col<PeerComparisonMetric>("FY", 0.7f, p => p.FinancialYear.ToString()),
            new Col<PeerComparisonMetric>("Company", 1.1f, p => p.CompanyValue?.ToString("0.##") ?? "-", true),
            new Col<PeerComparisonMetric>("Peer median", 1.2f, p => p.PeerMedianValue?.ToString("0.##") ?? "-", true),
            new Col<PeerComparisonMetric>("Position", 1.1f, p => p.Position.ToString()));
    });

    /// <summary>Years per pivoted table (here and in <see cref="RatiosTable"/>) — a company with a long
    /// filing history (10+ years is real, not hypothetical: seen on live data) would otherwise squeeze
    /// every year into one table, narrowing each value column until multi-digit figures wrap mid-number.
    /// Chosen so a table still fits one A4 page width at the theme's table font size with a readable
    /// column: on the actual PDF, 6 columns keep values like "-186.20" on one line; 8+ start wrapping.</summary>
    private const int MaxYearColumnsPerTable = 6;

    private void FinancialStatementTable(ColumnDescriptor col, IReadOnlyList<FinancialYearData> rows, string emptyText = "No financial data extracted.")
    {
        if (rows.Count == 0)
        {
            col.Item().Text(emptyText).FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
            return;
        }
        var years = rows.OrderBy(r => r.FinancialYear).ToList();
        (string Label, Func<FinancialYearData, decimal?> V)[] lines =
        [
            ("Share Capital", x => x.ShareCapital), ("Total Equity / Net Worth", x => x.NetWorth),
            ("Long-term Borrowings", x => x.LongTermBorrowings), ("Short-term Borrowings", x => x.ShortTermBorrowings),
            ("Total Debt", x => x.TotalDebt), ("Trade Payables", x => x.TradePayables),
            ("Current Assets", x => x.CurrentAssets), ("Current Liabilities", x => x.CurrentLiabilities),
            ("Inventory", x => x.Inventory), ("Trade Receivables", x => x.TradeReceivables),
            ("Cash & Bank", x => x.CashAndBank),
            ("Revenue", x => x.Revenue), ("Other Income", x => x.OtherIncome), ("EBITDA", x => x.Ebitda),
            ("EBIT", x => x.Ebit), ("Finance Cost", x => x.FinanceCost), ("PBT", x => x.Pbt), ("PAT", x => x.Pat),
            ("Cash flow — operating", x => x.Cfo), ("Cash flow — investing", x => x.Cfi), ("Cash flow — financing", x => x.Cff),
        ];

        // Decided once against the full year range, so the same row set appears in every year-chunk table
        // below — a line item present in an early year but blank in a later chunk still shows its "-"s
        // there, rather than the row silently disappearing from just that chunk.
        var activeLines = lines.Where(l => years.Any(y => l.V(y) is not null)).ToList();
        var yearChunks = years.Chunk(MaxYearColumnsPerTable).ToList();

        for (var ci = 0; ci < yearChunks.Count; ci++)
        {
            var chunk = yearChunks[ci];
            col.Item().PaddingTop(ci == 0 ? 0 : 10).Table(table =>
            {
                table.ColumnsDefinition(cd => { cd.RelativeColumn(2.4f); foreach (var _ in chunk) cd.RelativeColumn(); });
                table.Header(h =>
                {
                    HeaderCell(h.Cell(), "Line item");
                    foreach (var y in chunk) HeaderCell(h.Cell(), $"FY{y.FinancialYear}", right: true);
                });
                for (var ri = 0; ri < activeLines.Count; ri++)
                {
                    var (label, sel) = activeLines[ri];
                    var shaded = ri % 2 == 1;
                    BodyCell(table.Cell(), label, shaded: shaded);
                    foreach (var y in chunk) BodyCell(table.Cell(), sel(y)?.ToString("N2") ?? "-", right: true, shaded: shaded);
                }
            });
        }

        if (years.Any(y => y.CashFlowYearInferred))
            col.Item().PaddingTop(3).Text("Cash-flow rows: the source section carries no year header — years inferred by column position, excluded from automated analysis.")
                .FontSize(DossierTheme.Small).FontColor(DossierTheme.Amber);
    }

    /// <summary>The A1.x source-reported ratios, pivoted label × year like <see cref="FinancialStatementTable"/> — one row per ratio, one column per year on record, rendered exactly as extracted (numeric where it parsed, the raw text otherwise).</summary>
    private void RatiosTable(ColumnDescriptor col, IReadOnlyList<FinancialFact> ratioFacts)
    {
        var years = ratioFacts.Where(f => f.FinancialYear is not null)
            .Select(f => f.FinancialYear!.Value).Distinct().OrderBy(y => y).ToList();
        var labels = ratioFacts.Select(f => f.Label).Distinct().ToList();
        var byLabelYear = ratioFacts.Where(f => f.FinancialYear is not null)
            .GroupBy(f => (f.Label, Year: f.FinancialYear!.Value))
            .ToDictionary(g => g.Key, g => g.First());

        var yearChunks = years.Chunk(MaxYearColumnsPerTable).ToList();
        for (var ci = 0; ci < yearChunks.Count; ci++)
        {
            var chunk = yearChunks[ci];
            col.Item().PaddingTop(ci == 0 ? 0 : 10).Table(table =>
            {
                table.ColumnsDefinition(cd => { cd.RelativeColumn(2.4f); foreach (var _ in chunk) cd.RelativeColumn(); });
                table.Header(h =>
                {
                    HeaderCell(h.Cell(), "Ratio");
                    foreach (var y in chunk) HeaderCell(h.Cell(), $"FY{y}", right: true);
                });
                for (var ri = 0; ri < labels.Count; ri++)
                {
                    var label = labels[ri];
                    var shaded = ri % 2 == 1;
                    BodyCell(table.Cell(), label, shaded: shaded);
                    foreach (var y in chunk)
                    {
                        var value = byLabelYear.TryGetValue((label, y), out var fact)
                            ? fact.NumericValue?.ToString("0.##") ?? fact.RawValue
                            : "-";
                        BodyCell(table.Cell(), value, right: true, shaded: shaded);
                    }
                }
            });
        }

        if (ratioFacts.Any(f => f.YearInferred))
            col.Item().PaddingTop(3).Text("Some ratio rows: the source section carries no year header — years inferred by column position, excluded from automated analysis.")
                .FontSize(DossierTheme.Small).FontColor(DossierTheme.Amber);
    }

    private static readonly (FinancialBasis Basis, FinancialStatementSection Section, string Title)[] AdditionalLineItemGroups =
    [
        (FinancialBasis.Standalone, FinancialStatementSection.BalanceSheet, "Balance sheet (Standalone)"),
        (FinancialBasis.Standalone, FinancialStatementSection.ProfitAndLoss, "Profit & loss (Standalone)"),
        (FinancialBasis.Standalone, FinancialStatementSection.CashFlow, "Cash flow (Standalone)"),
        (FinancialBasis.Standalone, FinancialStatementSection.Other, "Other (Standalone)"),
        (FinancialBasis.Consolidated, FinancialStatementSection.BalanceSheet, "Balance sheet (Consolidated)"),
        (FinancialBasis.Consolidated, FinancialStatementSection.ProfitAndLoss, "Profit & loss (Consolidated)"),
        (FinancialBasis.Consolidated, FinancialStatementSection.CashFlow, "Cash flow (Consolidated)"),
        (FinancialBasis.Consolidated, FinancialStatementSection.Other, "Other (Consolidated)"),
    ];

    /// <summary>Pivots the catch-all FinancialFact rows (every value-bearing line item × year not mapped to
    /// a typed <see cref="FinancialYearData"/> column) into label × year grids, grouped by basis and
    /// statement section and year-chunked like <see cref="FinancialStatementTable"/> — previously one flat
    /// row per (label, year) pair, which on a company with a long filing history produced an unusable
    /// several-hundred-row table (22 labels × 12 years for the balance sheet alone, on real data) instead
    /// of the pivoted grid the source workbook itself uses. A handful of rows genuinely carry no year at
    /// all (the source cash-flow section had no year header and even column-position inference couldn't
    /// resolve one) — those are never silently dropped, just listed separately per group.</summary>
    private void AdditionalLineItemsTables(ColumnDescriptor col, IReadOnlyList<FinancialFact> facts)
    {
        var first = true;
        foreach (var (basis, section, title) in AdditionalLineItemGroups)
        {
            var group = facts.Where(x => x.Basis == basis && x.Section == section).ToList();
            if (group.Count == 0) continue;

            col.Item().PaddingTop(first ? 0 : 14).Text(title)
                .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Body).SemiBold();
            first = false;

            var dated = group.Where(x => x.FinancialYear is not null).ToList();
            if (dated.Count > 0)
            {
                var years = dated.Select(x => x.FinancialYear!.Value).Distinct().OrderBy(y => y).ToList();
                var labels = dated.Select(x => x.Label).Distinct().ToList();
                var byLabelYear = dated.GroupBy(x => (x.Label, Year: x.FinancialYear!.Value))
                    .ToDictionary(g => g.Key, g => g.First());

                var yearChunks = years.Chunk(MaxYearColumnsPerTable).ToList();
                for (var ci = 0; ci < yearChunks.Count; ci++)
                {
                    var chunk = yearChunks[ci];
                    col.Item().PaddingTop(ci == 0 ? 4 : 10).Table(table =>
                    {
                        table.ColumnsDefinition(cd => { cd.RelativeColumn(2.4f); foreach (var _ in chunk) cd.RelativeColumn(); });
                        table.Header(h =>
                        {
                            HeaderCell(h.Cell(), "Line item");
                            foreach (var y in chunk) HeaderCell(h.Cell(), $"FY{y}", right: true);
                        });
                        for (var ri = 0; ri < labels.Count; ri++)
                        {
                            var label = labels[ri];
                            var shaded = ri % 2 == 1;
                            BodyCell(table.Cell(), label, shaded: shaded);
                            foreach (var y in chunk)
                            {
                                var value = byLabelYear.TryGetValue((label, y), out var fact)
                                    ? fact.NumericValue?.ToString("N2") ?? fact.RawValue
                                    : "-";
                                BodyCell(table.Cell(), value, right: true, shaded: shaded);
                            }
                        }
                    });
                }
            }

            var undated = group.Where(x => x.FinancialYear is null).ToList();
            if (undated.Count > 0)
            {
                col.Item().PaddingTop(6).Text("Year could not be determined for the following (source section has no year header):")
                    .FontSize(DossierTheme.Small).FontColor(DossierTheme.Amber);
                col.Item().PaddingTop(2).Table(table =>
                {
                    table.ColumnsDefinition(cd => { cd.RelativeColumn(2.4f); cd.RelativeColumn(); });
                    table.Header(h => { HeaderCell(h.Cell(), "Line item"); HeaderCell(h.Cell(), "Value", right: true); });
                    for (var ri = 0; ri < undated.Count; ri++)
                    {
                        var x = undated[ri];
                        var shaded = ri % 2 == 1;
                        BodyCell(table.Cell(), x.Label, shaded: shaded);
                        BodyCell(table.Cell(), x.NumericValue?.ToString("N2") ?? x.RawValue, right: true, shaded: shaded);
                    }
                });
            }

            if (group.Any(x => x.YearInferred))
                col.Item().PaddingTop(3).Text("Some rows above: the source section carries no year header — years inferred by column position, excluded from automated analysis.")
                    .FontSize(DossierTheme.Small).FontColor(DossierTheme.Amber);
        }
    }

    // ── C. Charges & Security ─────────────────────────────────────────────

    private void AnnexureC(IContainer container) => container.Column(col =>
    {
        var ch = model.Charges;
        AnnexureHead(col, "Section 3", "Borrowing & Security",
            "Grouped by Charge ID — the full creation → modification → satisfaction sequence, the instrument " +
            "detail, and the normalized security where the source wording supports it.");

        // Net worth must be positive for "Nx net worth" to read as a sensible multiple — a negative net
        // worth (its own Critical finding elsewhere) would otherwise produce a misleading negative ratio.
        var netWorth = model.Financials.Latest?.NetWorth;
        var chargeRatio = netWorth is { } nw and > 0m ? ch.TotalOpenAmount / nw : (decimal?)null;
        var created12 = FindMetric(model.Metrics, "Charge register", "Amount created in last 12 months");
        AtAGlance(col, string.Join(" ", new[]
        {
            $"{ch.OpenCount} open charge(s) totalling {Money(ch.TotalOpenAmount)} are registered against the company.",
            chargeRatio is { } r ? $"That is {r:0.00}x FY{model.Financials.LatestYear} net worth." : null,
            created12?.HasValue == true ? $"{created12.DisplayValue()} of that was created in the trailing 12 months." : null,
        }.Where(s => s is not null)));

        if (model.SourceCoverage.ChargeReportMissing)
            col.Item().PaddingBottom(10).Border(0.75f).BorderColor(DossierTheme.Line).BorderLeft(2.5f)
                .BorderColor(DossierTheme.Amber).Background(DossierTheme.PaperRaised).Padding(11).Text(
                "The ROC report lists charges, but the Detailed Charge Report was not provided — charge " +
                "detail below is limited to the ROC sequence.")
                .FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft).LineHeight(1.4f);

        var n = 0;
        n++;
        col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — Open charges")
            .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
        if (ch.Open.Count == 0) col.Item().Text("No open charges.").FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
        foreach (var c in ch.Open) col.Item().PaddingBottom(8).Element(x => ChargeCard(x, c));

        n++;
        col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — Satisfied charges")
            .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
        if (ch.Satisfied.Count == 0) col.Item().Text("No satisfied charges on record.").FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
        foreach (var c in ch.Satisfied) col.Item().PaddingBottom(8).Element(x => ChargeCard(x, c));
    });

    private void ChargeCard(IContainer c, RocCharge charge) => c
        .Border(0.75f).BorderColor(DossierTheme.Line).Padding(11).Column(col =>
    {
        col.Item().Row(r =>
        {
            r.RelativeItem().Text(t =>
            {
                t.Span($"Charge {charge.RocChargeNumber}  ").FontFamily(DossierTheme.Mono).SemiBold().FontSize(DossierTheme.Body);
                t.Span("· " + charge.LatestChargeHolderRaw).FontSize(DossierTheme.Body);
            });
            r.ConstantItem(80).AlignRight().Text(Money(charge.CurrentAmount)).FontFamily(DossierTheme.Mono).FontSize(DossierTheme.Body).SemiBold();
        });

        // Lifecycle chips.
        var evs = charge.Events.OrderBy(e => e.EventDate ?? DateOnly.MinValue).ToList();
        if (evs.Count > 0)
            col.Item().PaddingTop(6).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft));
                for (var i = 0; i < evs.Count; i++)
                {
                    if (i > 0) t.Span("   →   ");
                    t.Span($"{evs[i].EventType} {D(evs[i].EventDate)}");
                    if (evs[i].ChargeAmount is { } a) t.Span($" ({Money(a)})").FontColor(DossierTheme.MaroonDeep);
                }
            });

        // Normalized security (confidence-gated) or raw wording.
        var labels = model.Charges.SecurityTypeLabels(charge);
        var narrativeEv = evs.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.PropertyParticulars) || !string.IsNullOrWhiteSpace(e.InstrumentDescription));
        if (charge.LatestSecurityConfidence is ChargeClassificationConfidence.High or ChargeClassificationConfidence.Medium && labels.Count > 0)
            col.Item().PaddingTop(5).Text("Security: " + string.Join(", ", labels.Select(SpaceCamel))
                + (charge.LatestArrangement is { } arr and not ChargeArrangement.Unknown ? $"  ·  {arr}" : ""))
                .FontSize(DossierTheme.Small).FontColor(DossierTheme.Ink);
        else if (narrativeEv is not null)
            col.Item().PaddingTop(5).Text("Security (source wording): " + (narrativeEv.PropertyParticulars ?? narrativeEv.InstrumentDescription))
                .FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft);
    });

    private static string SpaceCamel(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(\\B[A-Z])", " $1");

    // ── D. Compliance ─────────────────────────────────────────────────────

    private void AnnexureD(IContainer container) => container.Column(col =>
    {
        var d = model.Compliance;
        AnnexureHead(col, "Section 5", "Statutory Compliance",
            "MCA / regulatory records (name removal, BIFR, CDR), the full CIBIL suit-filed history, GST " +
            "registrations, EPFO establishments and MSME dues.");

        var gstr1 = FindMetric(model.Metrics, "GST compliance", "GST filing on-time rate (GSTR1)");
        var gstr3b = FindMetric(model.Metrics, "GST compliance", "GST filing on-time rate (GSTR3B)");
        AtAGlance(col, string.Join(" ", new[]
        {
            $"{d.Gst.Count} GST registration(s) on record.",
            gstr1?.HasValue == true ? $"GSTR-1 filings were on time {gstr1.DisplayValue()} of assessed periods." : null,
            gstr3b?.HasValue == true ? $"GSTR-3B: {gstr3b.DisplayValue()}." : null,
        }.Where(s => s is not null)));

        var n = 0;
        Item(col, ref n, "MCA / regulatory & suit-filed records", d.Records, [SheetAliases.Compliance],
            new Col<ComplianceRecord>("Type", 1.2f, r => r.RecordType.ToString()),
            new Col<ComplianceRecord>("Date", 1f, r => D(r.RecordDate), true),
            new Col<ComplianceRecord>("Bank / description", 2.6f, r => r.Bank ?? r.Description ?? r.SourceText ?? "-"),
            new Col<ComplianceRecord>("Amount ₹Cr", 1f, r => r.AmountCrore?.ToString("0.##") ?? "-", true),
            new Col<ComplianceRecord>("Defaulter type", 1.6f, r => r.DefaulterType ?? "-"));

        Item(col, ref n, "GST registrations", d.Gst, [SheetAliases.Gst, SheetAliases.GstAnnexure],
            new Col<GstRegistration>("GSTIN", 1.8f, g => g.Gstin),
            new Col<GstRegistration>("State", 1.4f, g => g.State ?? "-"),
            new Col<GstRegistration>("Status", 1f, g => g.Status ?? "-"),
            new Col<GstRegistration>("Registered", 1.1f, g => D(g.RegistrationDate), true),
            new Col<GstRegistration>("Returns on file", 1.2f, g => g.Filings.Count.ToString(), true));

        Item(col, ref n, "EPFO monthly contributions", d.Epfo, [SheetAliases.Epfo, SheetAliases.EpfoAnnexure],
            new Col<EpfoContribution>("Establishment", 2.2f, e => e.EstablishmentName ?? e.EstablishmentId),
            new Col<EpfoContribution>("Wage month", 1.2f, e => e.WageMonth),
            new Col<EpfoContribution>("Employees", 1f, e => e.EmployeeCount?.ToString() ?? "-", true),
            new Col<EpfoContribution>("Amount ₹Cr", 1f, e => e.ContributionAmountCrore?.ToString("0.##") ?? "-", true),
            new Col<EpfoContribution>("Status", 1.4f, e => e.PaymentStatus ?? "-"));

        Item(col, ref n, "MSME dues", d.Msme, [SheetAliases.Msme],
            new Col<MsmePayment>("Supplier", 2.6f, m => m.SupplierNameRaw),
            new Col<MsmePayment>("PAN", 1.4f, m => m.SupplierPan ?? "-"),
            new Col<MsmePayment>("Amount due ₹Cr", 1.3f, m => m.AmountDueCrore?.ToString("0.##") ?? "-", true),
            new Col<MsmePayment>("Period", 1.3f, m => m.ReportingPeriod));
    });

    // ── E. Litigation ─────────────────────────────────────────────────────

    private void AnnexureE(IContainer container) => container.Column(col =>
    {
        var lit = model.Litigation;
        AnnexureHead(col, "Section 6", "Litigation",
            $"{lit.All.Count} filing(s) on record — grouped by case thread, then the complete flat register.");

        if (lit.All.Count > 0)
            AtAGlance(col, $"{lit.PendingCount} pending, {lit.DisposedCount} disposed. " + (lit.NotDeterminedCount > 0
                ? $"Company role could not be reliably determined in {lit.NotDeterminedCount} of {lit.All.Count} " +
                  "case(s) from the available records — case counts should not be read as direct adverse exposure " +
                  "until role is verified."
                : "Company role is determined for every matched case."));

        var grouped = lit.Threads.Where(t => t.Cases.Count > 1).ToList();
        if (grouped.Count > 0)
        {
            col.Item().PaddingTop(14).PaddingBottom(4).Text("Item 1 — Case threads")
                .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
            col.Item().Text("Grouped automatically by parent case number — verify against the flat register below.")
                .FontSize(DossierTheme.Small).FontColor(DossierTheme.Amber);
            foreach (var t in grouped)
                col.Item().PaddingTop(6).Border(0.75f).BorderColor(DossierTheme.Line).Padding(10).Column(cc =>
                {
                    cc.Item().Text($"{t.Primary.Court} — {t.ThreadKey}").FontFamily(DossierTheme.Display).FontSize(DossierTheme.Body);
                    cc.Item().PaddingTop(2).Text($"{t.Cases.Count} linked filing(s) · {t.PendingCount} pending")
                        .FontSize(DossierTheme.Small).FontColor(DossierTheme.InkSoft);
                });
        }

        var n = grouped.Count > 0 ? 1 : 0;
        Item(col, ref n, "Legal history (full register)", lit.All, [SheetAliases.LegalHistory],
            new Col<Litigation>("Role", 1.6f, l => RoleLabel(lit.RoleFor(l))),
            new Col<Litigation>("Status", 1f, l => l.CaseStatus ?? "-"),
            new Col<Litigation>("Category", 1.2f, l => l.CaseCategory ?? "-"),
            new Col<Litigation>("Court", 2f, l => l.Court ?? "-"),
            new Col<Litigation>("Parties", 2.4f, l => Clip(l.Litigants, 90)),
            new Col<Litigation>("Case no.", 1.8f, l => l.CaseNumber ?? "-"),
            new Col<Litigation>("Last hearing", 1.1f, l => D(l.LastHearingDate), true));
    });

    // ── F. Coverage & data sufficiency ──────────────────────────────────────

    /// <summary>Only rendered when <see cref="HasCoverageGap"/> — the full detail the Snapshot's one-line
    /// summary (<see cref="ComposeCoverageSummary"/>) points to: which optional sheets weren't in this
    /// upload, and which deterministic checks had no basis to run.</summary>
    private void AnnexureF(IContainer container) => container.Column(col =>
    {
        AnnexureHead(col, "Section 7", "Coverage & Data Sufficiency",
            "How much of the optional source data this dossier is built on, and which deterministic " +
            "checks could not be run against this data set — so a report with no flags is never mistaken " +
            "for a fully verified one.");

        ComposeSourceCoverage(col);
        ComposeNotAssessed(col);
    });

    private static string RoleLabel(LitigationRole r) => r switch
    {
        LitigationRole.FiledAgainst => "Filed against corporate",
        LitigationRole.FiledBy => "Filed by corporate",
        _ => "Role not determined"
    };

    private static string Clip(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "-" : s!.Length <= max ? s : s[..max] + "…";

    /// <summary>Auditor identity (who signed off) — null when unknown, so <see cref="AuditorComment"/>'s
    /// null-filtered join skips it cleanly rather than inserting a stray placeholder.</summary>
    private static string? AuditorIdentity(AuditorObservation a)
    {
        if (string.IsNullOrWhiteSpace(a.AuditorName) || a.AuditorName == "-") return null;
        var parts = new List<string> { a.AuditorName! };
        if (!string.IsNullOrWhiteSpace(a.FirmName)) parts.Add(a.FirmName!);
        var main = string.Join(" — ", parts);

        var suffix = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.FirmRegistrationNumber)) suffix.Add($"FRN {a.FirmRegistrationNumber}");
        if (!string.IsNullOrWhiteSpace(a.MembershipNumber)) suffix.Add($"Memb. {a.MembershipNumber}");
        return suffix.Count > 0 ? $"{main} ({string.Join(", ", suffix)})" : main;
    }

    /// <summary>G18: builds the full Auditor's-comments cell text — auditor identity (name/firm, when
    /// known — e.g. backfilled from the financial-data sheet's own AUDITOR(s) block, which can be the
    /// only source for a year with no qualitative remark at all) plus the base comment plus Section/
    /// Directors' Comments/Footnote when present — and clips the WHOLE assembled string once, so an
    /// appended field can never run past the column's intended width (clip must happen last, not on
    /// ObservationText alone before the other parts are appended). Deliberately folded into this existing
    /// column rather than a new one — see #161's dense-table-columns note.</summary>
    private static string AuditorComment(AuditorObservation a)
    {
        var parts = new List<string?> { AuditorIdentity(a), a.ObservationText };
        if (a.SectionCode is not null)
            parts.Add(a.SectionName is not null ? $"Section: {a.SectionCode} ({a.SectionName})" : $"Section: {a.SectionCode}");
        else if (a.SectionName is not null) parts.Add($"Section: {a.SectionName}");
        if (a.DirectorsComments is not null) parts.Add($"Directors' comments: {a.DirectorsComments}");
        if (a.Footnotes is not null) parts.Add($"Footnote: {a.Footnotes}");
        return Clip(string.Join(" — ", parts.Where(p => p is not null)), 160);
    }
}
