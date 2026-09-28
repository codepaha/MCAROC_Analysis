# Registry analytics for an existing master import

Registry Intelligence supports Company, LLP and Foreign master records. Company and LLP tabs show current status, states, industries, ROC offices, annual and monthly registrations, and current distressed statuses. LLPs also show districts; Companies include class, listing status and authorized-capital distributions.

## Existing imports

Set `RegistryAnalytics:AllowImportedBaseline` to `true` to allow analytics when master records exist but no completed, dated Company Master sync job exists. The Development configuration enables it. For a hosted deployment, set `RegistryAnalytics__AllowImportedBaseline=true` explicitly.

This is an imported-data baseline, not verified MCA publication provenance. The interface labels the publication date unknown, displays calculation time, and does not create or modify sync jobs. Calendar registration windows use the calculation date when publication date is unknown.

Imported baselines use a separate snapshot ID (-1), a 15-minute refresh interval, and the existing shared snapshot store. A single SQL GROUPING SETS query computes dimensions from the master input instead of issuing separate full-table queries per chart. The shared SQL promotion/rebuild gates serialize computation and prevent scanning during cooperating sync promotions. A newly completed, dated sync job automatically takes precedence over the baseline. Direct bulk imports should be performed while the portal is stopped; their contents become visible after the next background refresh. Restarting the portal alone does not invalidate a fresh shared baseline. A scoped background worker checks every five minutes and refreshes baselines older than fifteen minutes. Requests serve the last calculated baseline with its timestamp during refresh; they never wait for a full-table rebuild. Without a baseline, the page shows a preparation notice and keeps Explorer available.

Aggregate rebuild queries have a scoped 300-second SQL command budget. Explorer retains the normal timeout and selects only original display columns; it does not require the newer name-normalization columns. This does not substitute for applying identity-resolution migrations before using identity-resolution features.

Shared snapshots include an analytics payload version. Earlier payloads are rebuilt so their missing Company/LLP analytics do not leave empty tabs.

## Metric limits

Current status is not historical status. Registration dates support annual/monthly registration counts, not strike-off event dates, lifespan or historical status-transition trends. Missing field/date values are not filled with invented values. Distressed counts use explicitly recorded CIRP, liquidation, relevant dissolution and pending strike-off statuses; they are not a risk prediction.

## Local validation

Run the RegistryAnalyticsTests and RegistryTimeoutTests. SQL-backed verification should also search by company name and exact identifier, render all five tabs, confirm entity totals agree with the master database, and verify that a warm request uses the shared baseline rather than rebuilding it.
