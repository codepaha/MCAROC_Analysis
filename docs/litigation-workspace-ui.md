# Litigation workspace UI (#405)

The request page now places a compact case register before the detailed report overview. It follows the CRA interaction in PropertyIntelligence: browse case summaries, choose **View**, then read the complete case information, extracted orders, analyses and available PDFs on the existing case page. The return link preserves the register filters and page.

## Behaviour

- Company metadata and secondary request actions are in accessible disclosures. Only the domain tabs stay sticky as the company header scrolls away.
- Litigation exports share a Download menu. Critical search/import failures, prior-snapshot warnings and charge-link notices remain visible; detailed risk, age, court, source and portfolio information is below the register in a closed disclosure.
- The register defaults to 25 rows, with 50 and 100 options. Case number, CNR, court and party search runs over the entire authoritative snapshot before SQL pagination. Court, status and order-outcome filters persist in the URL.
- Page changes fetch only the register. Requests can be cancelled; an old response cannot replace newer results. Keyboard focus, an announced loading/result message, back/forward navigation and ordinary GET links support navigation.
- Snapshot and request-generation checks return HTTP 409 when the displayed report has changed. Existing rows remain visible with a full-page reload link.
- Partial responses retain per-case charge-link badges from the initial page's versioned comparison cache, limited to the displayed case IDs. If that comparison expires or its evidence version changes, pagination returns HTTP 409 and offers a full-page refresh; it never silently drops badges or performs a cold full-evidence scan.
- Analysis eligibility is checked asynchronously when the Litigation tab opens. Analysis polling reads lifecycle metadata rather than every persisted case analysis.

## Performance boundary

The default case-list endpoint projects only a bounded page of case metadata. Order/document counts and scalar analysis warning flags are restricted to those case IDs. It does not materialize extracted text, PDF URLs, raw reports, full case-analysis JSON or evidence bodies. No schema migration or paid/external API call is introduced by browsing or pagination.

An explicitly selected order-outcome filter retains existing current-evidence/hash validation. Its first call may read evidence chunks before the existing cache is populated. Initial request rendering also retains whole-report metadata summaries and existing dossier/charge-link queries; the entire request page has not become constant-time.

## Verification

SQL-backed tests seed 3,000 cases, reused-snapshot membership, large stored evidence and analysis payloads. They verify SQL OFFSET/FETCH, bounded pages, global party search, disjoint pages, final-page clamping, unknown statuses, deep-link ranking, URL encoding, publication conflicts and lifecycle-only polling. Razor tests cover compact rendering and escaped party text; existing evidence, risk, age and snapshot-consistency regressions remain covered.

One local diagnostic run measured 3,927 ms for the cold first query (7 SQL statements, 202 ms cumulative command execution), 60 ms for the warmed first page and 156 ms for the last 100-row page. These measurements exclude browser/network rendering and are diagnostics, not production latency guarantees.

Browser verification completed on 6 October 2026 using Chrome against an isolated local database with 3,000 synthetic cases. Desktop and 390px mobile viewport checks covered the register, long party names, charge badges after AJAX paging, View/back to page 2, global search for case 2999, View/back preserving that filter, keyboard export-menu opening/Escape, and light/dark layouts. The 100-row option rendered exactly 100 table rows. Mobile document width was 380px with scroll width 380px; the wide table scrolls within its container. Screenshots are saved locally under `E:/MCAROC_Analysis/artifacts/litigation-ui407/`.

These checks found and corrected a default-tab initialization ReferenceError, tab-hash positioning underneath sticky navigation, overflow caused by an absolutely positioned hidden table label, and hardcoded light-theme table/status/warning colors. All 100 JavaScript tests pass, including a new default-tab initialization and tab-hash scroll regression. The final source review preserves the SQL paging and publication fences; the browser corrections change only CSS/navigation JavaScript and their coverage.

This was viewport testing in desktop Chrome with synthetic data, not a physical mobile-device or production-provider test. Paid analysis/refresh actions and PDF downloads were not exercised. Hosted checks must be refreshed for the browser-correction commit before release.
