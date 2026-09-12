# C5b (#123) — amount unit toggle: call-site inventory

Every place a crore-denominated amount, or a unit label naming one, is rendered on the Requests
Details page. Enforced (not just documented) by
`MCAROC_Analysis.Tests/AmountToggleCoverageTests.cs`, which counts the `<partial name="Details/_Amount"`
invocations and `data-amount-unit-label="` sites in each file below and fails the build if either count
drifts from this table. Line numbers are as of the PR that added this file; they will drift with future
edits — the counts are what the test actually pins.

Totals: **43** value sites (via the `Details/_Amount` partial) + **21** unit-label sites +
**1** `title=` attribute fix. All amounts across the 6 files were classified by direct line-by-line
reading, not a keyword grep — the totals differ slightly from an earlier draft estimate (44/20) once
every site was actually converted and verified against source.

## Value sites — 43 (via `Details/_Amount`)

| File | Lines | Count | What |
|---|---|---|---|
| `_ChargeDrawer.cshtml` | 68, 69, 82, 109, 110, 118, 223, 224, 226 | 9 | Registered amount (latest/original), per-event charge amount, enhancement delta |
| `_ChargesTab.cshtml` | 79, 89, 93, 121, 171, 228 | 6 | Registered Open Amount KPI, MCA-stated total (2 branches), Lender Concentration row, charge-row amounts (open/satisfied tables) |
| `_ComplianceTab.cshtml` | 194, 199, 246, 270, 274, 304, 479, 515, 548, 551 | 10 | CIBIL Total/Institutional Peak Exposure (incl. partial-coverage `*` variants), per-bank peak, credit-linked amount, EPFO aggregate + per-month contribution, MSME amount-due + total |
| `_CorporateTab.cshtml` | 49, 50, 58, 83, 285, 286, 368, 369, 439, 440, 472, 497 | 12 | Authorised/Paid-up Capital, MCA sum of charges, divergence badge's visible computed total, Paid-up/Charges pairs across Director Associations / Shareholdings / Related Corporates, Related Party Transaction amount, Security Allotment amount |
| `_FinancialStatement.cshtml` | 31 | 1 | P&L/BS/CF statement cell (gated on `data.Unit == "₹ Crore"`; the Ratios variant of the same `RenderTable` call never routes through this) |
| `_FinancialsTab.cshtml` | 52, 53, 54, 55, 173 | 5 | Latest Revenue/Net Worth/PAT/Total Debt KPI cards (previously bare `.ToString("N1")` bypasses), Closest Peers by Revenue row |

## Unit-label sites — 21 (`data-amount-unit-label`)

Three variants, matching each site's own pre-existing textual convention rather than normalizing every
header to look identical:
- `"long"` → "₹ Crore" / "₹ Lakh" / "₹" — standalone captions.
- `"suffix"` → "Cr" / "L" / "₹" — bare abbreviation already inside a header's own "(...)".
- `"suffix-symbol"` → "₹ Cr" / "₹ L" / "₹" — symbol *and* abbreviation inside a header's "(...)".

| File | Lines | Count | Variant(s) |
|---|---|---|---|
| `_ChargesTab.cshtml` | 89, 93, 98, 114 | 4 | 3× `long` (MCA-stated captions), 1× `suffix` (Registered Amount header) |
| `_ComplianceTab.cshtml` | 231, 295, 506, 544 | 4 | 1× `suffix-symbol` (Peak Exposure header), 3× `suffix` (Amount/Amount Due headers) |
| `_CorporateTab.cshtml` | 276, 348, 426, 462, 489 | 8 | 8× `suffix` (Paid-up/Charges pairs ×3 tables, 2 single Amount headers) |
| `_FinancialStatement.cshtml` | 113 | 1 | 1× `long` ("All figures in ₹ Crore." caption — caught during implementation; not in the original draft inventory, since P&L/BS/CF panels share this caption and would otherwise show a stale "₹ Crore" after toggling to Lakh/₹) |
| `_FinancialsTab.cshtml` | 53, 54, 55, 163 | 4 | 3× `long` (Net Worth/PAT/Total Debt captions), 1× `suffix-symbol` (Revenue header) |

## The one `title=` fix (not a value or label site)

`_CorporateTab.cshtml`'s charge-register divergence badge originally embedded two `@Money()` calls
directly in a `title="..."` attribute — a `title` attribute cannot hold markup, so it cannot call the
`_Amount` partial. Since the badge's own **visible** text already repeats the computed total (and now
routes through `_Amount`), the fix drops the dynamic amounts from the tooltip text entirely rather than
inventing a bespoke attribute-templating path for one occurrence. Regression-tested by
`AmountToggleCoverageTests.Corporate_tab_divergence_tooltip_no_longer_embeds_a_dynamic_amount` (source
scan) and `CorporateTabRenderingTests.Charge_register_divergence_tooltip_names_no_dynamic_amount_but_the_visible_badge_still_does`
(rendered output).

## Deliberately out of scope

- **Native-currency amounts** (`_ComplianceTab.cshtml:595,615`, `CreditRating.Amount`) — not crore-denominated, the toggle doesn't apply.
- **Percentages** (`_CorporateTab.cshtml:137,361,404,406,438`) and **per-share values** (`_CorporateTab.cshtml:499,500`) and **share/entity counts** (`_CorporateTab.cshtml:332,333,362,403,405,498`) — not amounts at all.
- **`_FinancialsTab.cshtml`'s Peer Comparison metrics table** (lines 144, 145: `m.CompanyValue`, `m.PeerMedianValue`) — mixed unit; the same column holds a percentage, a ratio, or a crore value depending on `MetricName`, so a single toggle-aware wrapper would be actively wrong for the non-crore rows.
- **`_FinancialsTab.cshtml`'s Additional Indicators table** (lines 109, 113) — each row's `Unit` is a free-text workbook value (not always crore); shown as-is, never coerced into a unit the toggle understands.
- **`_FinancialStatement.cshtml`'s Ratios variant** (line 35, the `else` branch of the same `RenderTable`) — dimensionless ratios/percentages, gated out by the `data.Unit == "₹ Crore"` check at line 29.

## The dossier PDF is untouched

`Services/Dossier/DossierPdfComposer.cs` has its own, independent `Money()` implementation and always
renders `"₹{v:N2} Cr"` — this issue does not touch it. The PDF is a static, server-generated artifact
(Executive/Full/Source-record variants); a live "current unit selection" doesn't apply to something
already rendered to a fixed document. The distinct, later, already-tracked concern is C10's print
stylesheet for the *portal page itself* (a browser print of the live Details tab), which will show
whichever unit was selected on screen at print time — unrelated to this dossier PDF pipeline.
