using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
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
        IReadOnlyList<T> rows, params Col<T>[] cols)
    {
        n++;

        col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — {caption}")
            .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);

        if (rows.Count == 0)
        {
            col.Item().Text("No records in this workbook.").FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
            return;
        }

        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cd => { foreach (var c in cols) cd.RelativeColumn(c.Weight); });
            table.Header(h => { foreach (var c in cols) HeaderCell(h.Cell(), c.Header); });
            foreach (var r in rows)
                foreach (var c in cols)
                    BodyCell(table.Cell(), c.Cell(r), c.Right && c.Cell(r) is not "-" and not "");
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
        Item(col, ref n, "Directors register", c.Directors,
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

        Item(col, ref n, "Related corporates", c.RelatedCorporates,
            new Col<RelatedCorporate>("Entity", 2.4f, r => r.EntityNameRaw),
            new Col<RelatedCorporate>("Relationship", 1.3f, r => r.RelationshipType.ToString()),
            new Col<RelatedCorporate>("Holding %", 1f, r => r.HoldingPercent?.ToString("0.##") ?? "-", true),
            new Col<RelatedCorporate>("Status", 1.2f, r => r.CompanyStatus ?? "-"),
            new Col<RelatedCorporate>("Location", 1.4f, r => r.Location ?? "-"));

        Item(col, ref n, "Shareholding above 5%", c.Shareholders,
            new Col<Shareholding>("FY", 0.7f, s => s.FinancialYear.ToString()),
            new Col<Shareholding>("Shareholder", 2.4f, s => s.ShareholderNameRaw),
            new Col<Shareholding>("Type", 1.1f, s => s.ShareholderType ?? "-"),
            new Col<Shareholding>("Promoter", 0.9f, s => s.IsPromoter ? "Yes" : "-"),
            new Col<Shareholding>("% held", 1f, s => s.HoldingPercentage?.ToString("0.##") ?? "-", true));

        Item(col, ref n, "Securities allotment", c.SecurityAllotments,
            new Col<SecurityAllotment>("Date", 1.1f, a => D(a.AllotmentDate), true),
            new Col<SecurityAllotment>("Type", 1.2f, a => a.AllotmentType ?? "-"),
            new Col<SecurityAllotment>("Instrument", 2.2f, a => a.InstrumentType ?? "-"),
            new Col<SecurityAllotment>("Amount ₹Cr", 1.1f, a => a.AmountCrore?.ToString("0.##") ?? "-", true),
            new Col<SecurityAllotment>("Securities", 1.2f, a => a.NumberOfSecurities?.ToString("N0") ?? "-", true));

        Item(col, ref n, "Designation history at this company", c.DesignationHistory,
            new Col<DirectorAssignmentHistory>("Director", 2.2f, h => h.DirectorNameRaw),
            new Col<DirectorAssignmentHistory>("Designation", 1.8f, h => h.Designation ?? "-"),
            new Col<DirectorAssignmentHistory>("Appointed", 1.2f, h => D(h.AppointmentDate), true),
            new Col<DirectorAssignmentHistory>("Ceased", 1.2f, h => D(h.CessationDate), true));

        Item(col, ref n, "Other directorships", c.OtherDirectorships,
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
        FinancialStatementTable(col, f.Standalone);

        if (f.Consolidated.Count > 0)
        {
            n++;
            col.Item().PaddingTop(14).PaddingBottom(4).Text($"Item {n} — Consolidated financial data (₹ Crore)")
                .FontFamily(DossierTheme.Display).FontSize(DossierTheme.Heading);
            FinancialStatementTable(col, f.Consolidated);
        }

        Item(col, ref n, "Additional line items (not in the summary above)", f.Facts,
            new Col<FinancialFact>("Section", 1.1f, x => x.Section.ToString()),
            new Col<FinancialFact>("Label", 2.6f, x => x.Label),
            new Col<FinancialFact>("FY", 0.7f, x => x.FinancialYear?.ToString() ?? "-"),
            new Col<FinancialFact>("Value", 1.2f, x => x.RawValue, true),
            new Col<FinancialFact>("Note", 1.1f, x => x.YearInferred ? "year inferred" : ""));

        Item(col, ref n, "Auditor's comments", f.AuditorObservations,
            new Col<AuditorObservation>("FY", 0.7f, a => a.FinancialYear.ToString()),
            new Col<AuditorObservation>("Basis", 1f, a => a.Basis.ToString()),
            new Col<AuditorObservation>("Qualified / adverse", 1.4f, a => a.HasQualificationOrAdverseRemark ? "Yes" : "No"),
            new Col<AuditorObservation>("Comment", 4f, AuditorComment));

        Item(col, ref n, "Peer comparison", f.PeerComparison,
            new Col<PeerComparisonMetric>("Metric", 2.6f, p => p.MetricName),
            new Col<PeerComparisonMetric>("FY", 0.7f, p => p.FinancialYear.ToString()),
            new Col<PeerComparisonMetric>("Company", 1.1f, p => p.CompanyValue?.ToString("0.##") ?? "-", true),
            new Col<PeerComparisonMetric>("Peer median", 1.2f, p => p.PeerMedianValue?.ToString("0.##") ?? "-", true),
            new Col<PeerComparisonMetric>("Position", 1.1f, p => p.Position.ToString()));
    });

    private void FinancialStatementTable(ColumnDescriptor col, IReadOnlyList<FinancialYearData> rows)
    {
        if (rows.Count == 0)
        {
            col.Item().Text("No financial data extracted.").FontSize(DossierTheme.Small).FontColor(DossierTheme.InkFaint);
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

        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cd => { cd.RelativeColumn(2.4f); foreach (var _ in years) cd.RelativeColumn(); });
            table.Header(h =>
            {
                HeaderCell(h.Cell(), "Line item");
                foreach (var y in years) HeaderCell(h.Cell(), $"FY{y.FinancialYear}");
            });
            foreach (var (label, sel) in lines)
            {
                if (years.All(y => sel(y) is null)) continue;
                BodyCell(table.Cell(), label);
                foreach (var y in years) BodyCell(table.Cell(), sel(y)?.ToString("N2") ?? "-", right: true);
            }
        });

        if (years.Any(y => y.CashFlowYearInferred))
            col.Item().PaddingTop(3).Text("Cash-flow rows: the source section carries no year header — years inferred by column position, excluded from automated analysis.")
                .FontSize(DossierTheme.Small).FontColor(DossierTheme.Amber);
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
        Item(col, ref n, "MCA / regulatory & suit-filed records", d.Records,
            new Col<ComplianceRecord>("Type", 1.2f, r => r.RecordType.ToString()),
            new Col<ComplianceRecord>("Date", 1f, r => D(r.RecordDate), true),
            new Col<ComplianceRecord>("Bank / description", 2.6f, r => r.Bank ?? r.Description ?? r.SourceText ?? "-"),
            new Col<ComplianceRecord>("Amount ₹Cr", 1f, r => r.AmountCrore?.ToString("0.##") ?? "-", true),
            new Col<ComplianceRecord>("Defaulter type", 1.6f, r => r.DefaulterType ?? "-"));

        Item(col, ref n, "GST registrations", d.Gst,
            new Col<GstRegistration>("GSTIN", 1.8f, g => g.Gstin),
            new Col<GstRegistration>("State", 1.4f, g => g.State ?? "-"),
            new Col<GstRegistration>("Status", 1f, g => g.Status ?? "-"),
            new Col<GstRegistration>("Registered", 1.1f, g => D(g.RegistrationDate), true),
            new Col<GstRegistration>("Returns on file", 1.2f, g => g.Filings.Count.ToString(), true));

        Item(col, ref n, "EPFO monthly contributions", d.Epfo,
            new Col<EpfoContribution>("Establishment", 2.2f, e => e.EstablishmentName ?? e.EstablishmentId),
            new Col<EpfoContribution>("Wage month", 1.2f, e => e.WageMonth),
            new Col<EpfoContribution>("Employees", 1f, e => e.EmployeeCount?.ToString() ?? "-", true),
            new Col<EpfoContribution>("Amount ₹Cr", 1f, e => e.ContributionAmountCrore?.ToString("0.##") ?? "-", true),
            new Col<EpfoContribution>("Status", 1.4f, e => e.PaymentStatus ?? "-"));

        Item(col, ref n, "MSME dues", d.Msme,
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
        Item(col, ref n, "Legal history (full register)", lit.All,
            new Col<Litigation>("Role", 1.6f, l => RoleLabel(lit.RoleFor(l))),
            new Col<Litigation>("Status", 1f, l => l.CaseStatus ?? "-"),
            new Col<Litigation>("Category", 1.2f, l => l.CaseCategory ?? "-"),
            new Col<Litigation>("Court", 2f, l => l.Court ?? "-"),
            new Col<Litigation>("Parties", 2.4f, l => Clip(l.Litigants, 90)),
            new Col<Litigation>("Case no.", 1.8f, l => l.CaseNumber ?? "-"),
            new Col<Litigation>("Last hearing", 1.1f, l => D(l.LastHearingDate), true));
    });

    private static string RoleLabel(LitigationRole r) => r switch
    {
        LitigationRole.FiledAgainst => "Filed against corporate",
        LitigationRole.FiledBy => "Filed by corporate",
        _ => "Role not determined"
    };

    private static string Clip(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "-" : s!.Length <= max ? s : s[..max] + "…";

    /// <summary>G18: builds the full Auditor's-comments cell text — the base comment plus Section/
    /// Directors' Comments/Footnote when present — and clips the WHOLE assembled string once, so an
    /// appended field can never run past the column's intended width (clip must happen last, not on
    /// ObservationText alone before the other parts are appended).</summary>
    private static string AuditorComment(AuditorObservation a)
    {
        var parts = new List<string?> { a.ObservationText };
        if (a.SectionCode is not null)
            parts.Add(a.SectionName is not null ? $"Section: {a.SectionCode} ({a.SectionName})" : $"Section: {a.SectionCode}");
        else if (a.SectionName is not null) parts.Add($"Section: {a.SectionName}");
        if (a.DirectorsComments is not null) parts.Add($"Directors' comments: {a.DirectorsComments}");
        if (a.Footnotes is not null) parts.Add($"Footnote: {a.Footnotes}");
        return Clip(string.Join(" — ", parts.Where(p => p is not null)), 160);
    }
}
