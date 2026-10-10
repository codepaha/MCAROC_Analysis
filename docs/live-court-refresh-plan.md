# Live court refresh (DRT + consumer courts) and case-number normalisation — plan

Status: **draft v4.8 for review, nothing built.** (v4.8: §12.3 consumer scopes, associations and overlays; §12.5–12.7 the transition bridge, field ownership and cutover contract; §5 rewritten around the module.) (v4.7: §11.3 retains unmatched pre-login records; §4.7b compares the case set, not only the hashes.) (v4.6: §12 — a standalone litigation module that every consumer reads from; it changes how §4 and §11 are packaged, not what they do.) (v4.5: §11 extends the live updates to the SBI pre-login reports — PR #430 — and §6 records the decisions made on the owner's instruction "choose the best".) (v4.4: §3.5–3.6 display format and a second sample file, and three more review findings — §4.2b retained membership, §4.7b frozen payloads, §4.6 test.) (v4.3: §4.2e, the non-destructive merge rule, and Phase 0b.) (v4.1 added §4.7–4.9; v4.2 answers three more review findings — §4.7b, §4.6 and §10 rows 11–13.) v4 answers the seven findings on v3 (§9 change log); it also corrects two statements in v3 that the code did not support (§4.2). v2 answered the five review findings; v3 records the owner's decisions of 2026-10-10 (§4.4 eligibility rules, §4.6 de-duplication, proxies kept, no raw responses; table at the end). Scope now: **DRT/DRAT and consumer commissions (e-Jagriti)**. NCLT is later; neither existing crawler covers it.

## 1. Why — what is wrong with the stored cases today

Measured on the one request that has litigation in the dev database (Coastal, 626 cases — a sample, not a population; corpus figures are from that database and were not re-measured elsewhere):

| Court type | Cases | Last hearing missing | Next hearing missing | Decision date missing | CNR missing | Other gaps |
|---|---|---|---|---|---|---|
| DRT | 19 | 100% | 37% | 100% | 100% | — (36.7 orders/case) |
| Consumer | 3 | 0% | 100% | 100% | 100% | — |
| NCLT | 43 | 100% | 44% | 100% | 100% | court 12%, state/district 37%, filing 74% |
| NCLAT | 10 | 100% | 90% | 100% | 100% | status 60%, state/district 100% |
| High Court | 262 | 46% | 69% | 47% | 15% | status 12% |
| District | 259 | 12% | 78% | 58% | 0% | status 9% |

Three separate problems:

1. **Stale / missing fields** — status, hearings, decision date, court, state/district. Read them from the authoritative portal.
2. **Missing cases and orders** — the vendor feed is incomplete and lags. Party-name discovery and order listing on the portals.
3. **Inconsistent identifiers** — `292/2016`, `10555`, `CW/10555/2010`, `A.A/9/9`, `CMA\/1146\/2022`, or several numbers glued into one string. Parse every case number into a fixed structure (§3). No network needed, so it comes first.

Also found in the same data and fixed in the same pass: status spelled `PENDING`/`DISPOSED`/`Disposed`/`CASE DISPOSED`/`DISPOSAL`/`ORDERS`/empty (71 of 626 empty); dates in two string formats; 86 distinct court names (`CalcuttaHighCourt` beside `Senior Civil Judge cum RC, South-East, Saket`).

## 2. What the two existing crawlers give us (reference, not code to ship)

| | Consumer — `E:\EcourtCrawler` (e-Jagriti) | DRT — `kotak_drt\drt_downloader` (drt.gov.in/drtapi) |
|---|---|---|
| Company search | `getCauseTitleListByCompany`, 731 commissions, no CAPTCHA | `casedetail_party_name_wise` per tribunal, `drat_party_name_wise` for DRAT; LIKE search; no CAPTCHA |
| Case by number | `getCaseStatus` (hearings, `dailyOrderStatus`) | `getCaseDetailCaseNoWise` (tribunal id + type id + no + year); diary fallback. **Returns whatever case has that number at the tribunal** — identity must be re-checked |
| Orders | `getDailyOrderJudgementPdf` (base64 PDF, or an HTML note; HTTP 400 = none) | `getDrtDailyOrderReportCaseNo`, `getDrtFinalOrderReportCaseNo`; files via `pdf1.php`/`qrpdfview.php`; some orders are HTML, Word, images, or embedded in the URL |
| Known gaps | live path needs CAPTCHA; its model config is on retiring Gemini 2.5 | **DRAT: no public order endpoint**; RC/TRC endpoint HTTP 500; many Delhi/Ahmedabad PDFs are scanned images |
| Reusable data | `commissions_data.json` (state/district → commission id) | `tribunal_mapper.py`, `case_type_mapper.py` (name → API id) |
| Reference run | — | Kotak: 6,319 cases, 61,254 orders (2,090 final), 54,728 PDFs → **golden corpus** for our port |

Decision: **port the CAPTCHA-free calls to C#** as portal services. Do not run Python beside the portal. The Python stays the behavioural reference. Its matching logic is **not** reused (§4.3 — it is too loose).

## 2.1 What the portals actually return (probed live, 2026-10-10)

About ten read-only requests, 2 s apart, an identifying User-Agent (`MCAROC-research/1.0`), public data only. DRT's `robots.txt` allows everything; e-Jagriti has none. Both portals are single-page apps, so the pages themselves show nothing; this is what their data calls return. Personal names were not printed.

**DRT / DRAT (`drt.gov.in/drtapi`, POST, no CAPTCHA)**

| Call | Returns |
|---|---|
| `getDrtDratScheamName` | **44 tribunals/appellate tribunals**: `SchemaName`, `schemeNameDrtId`. No state/district — derive from the tribunal. |
| `getDrtDratCaseTyepName` | 21 case types (`caseType` id, `caseTypeName`): OA 1, Review 2, MA 3, Appeal 4, URA 5, TA 6, SA 7, AIR 8, Counter claim 10, TSA 11, TRC 12, Execution 13, Chamber appeal 14, IBC-C 100, IBC-A 101, … |
| `casedetail_party_name_wise` (party LIKE search) | 9 fields per row: `casetype`, **`caseno` already as `TYPE/NO/YEAR` (`MA/21/2016`)**, `diaryno`, `dateoffiling`, `applicant`, `respondent`, both advocates, `filingNo`. No status, no hearing dates. (161 rows for "KOTAK MAHINDRA BANK" at DRT 1 alone.) |
| `getCaseDetailCaseNoWise` | **26 fields**: `casestatus` (`DISPOSAL` / `PENDING` / `TRANSFERED` [sic]; about 2% null), status code, `dateoffiling`, `dateofdisposal`, **`disposalNature`**, **`nextlistingdate`** + **`nextListingPurpose`**, `courtNo`/`courtName`, **`maincasecaseno`** (the main case a MA/IA belongs to, e.g. `OA/445/1998`), **`suit_amount`**, parties, addresses, advocates, `iaDetails`, and **`caseProceedingDetails`: one row per hearing** (`causelistdate`, `purpose`, `courtName`, `courtNo`, `ascourtName`, **`orderUrl`**). |
| `getDrtDailyOrderReportCaseNo` | One row per daily order: `diaryno`, `dateoffiling`, party names, `pronouncedBy`, `dailyOrderPdf`, `item_no`. |
| `getDrtFinalOrderReportCaseNo` | Final orders/judgments, or `{"status":"Record Not Fund"}` (sic) when none. |

**Consumer commissions (`e-jagriti.gov.in/services/...`, GET, no CAPTCHA)**

| Call | Returns |
|---|---|
| `getCaseStatus?caseNumber=` | **24 fields**: full case number (`DC/497/CC/599/2017`), `fillingReferenceNumber` (sic), `caseFilingDate`, `filedInComissionName`, parties, **advocates**, **`caseStage`** (`ALLOWED` etc. — the outcome once decided), `dateOfNextearing` (sic), `applicationsFiled`, `attachedLowerCourt`, `impungedOrderDate`, `dateOfInterim`, `dateOfOrderReceipt`, and **`caseHearingDetails`** (32 hearings for one case): `dateOfHearing`, `dateOfNextHearing`, `caseStage`, **`proceedingText`**, `dailyOrderStatus`, `orderTypeId`, `hearingSequenceNumber`. |
| `report/getCauseTitleListByCompany` (company search) | 15 fields per row: `case_number`, `case_type_name`, `commission_name`, `case_stage_name`, `case_category_name`, parties, advocates, `case_filing_date`, hearing dates, `commission_type_en`. |
| `getDailyOrderJudgementPdf` | Per hearing: base64 PDF, or an HTML note; HTTP 400 when none (documented by the crawler; not re-probed). |
| (also in the site's code) | `getCaseDetailForHistoryDashBoard` (hearing history with `actionTaken`, `courtRoomName`), `getcasestatusreport`, judge-wise hearing lists. |

**What this fills in our gaps**

| Our field (missing today) | DRT gives | Consumer gives |
|---|---|---|
| Status | `casestatus` | `caseStage` (outcome words); pending/disposed must be derived |
| Court name | tribunal name from the 44-row table | `filedInComissionName` / `commission_name` |
| State / district | **not returned** — derive from the tribunal table | **not returned** — derive from the commission table (`commissions_data.json`) |
| Last hearing (LDOH) | latest past `causelistdate` | latest `dateOfHearing` |
| Next hearing (NDOH) | `nextlistingdate` (+ purpose) | `dateOfNextearing` / last hearing's `dateOfNextHearing` |
| Decision date | `dateofdisposal` (+ `disposalNature`) | **no field** — derive from the final hearing/order date, marked as derived |
| Filing date | `dateoffiling` | `caseFilingDate` |
| **New: hearing history** | `caseProceedingDetails` | `caseHearingDetails` |
| **New: suit amount** | `suit_amount` (often null) | — |
| **New: main-case link** | `maincasecaseno` | `attachedLowerCourt` |
| **New: advocates** | yes | yes |
| CNR | **none** | **none** — so `CaseKey` is the only identity |

**Traps for normalisation (found by probing, handled in §3)**
- **DRT dates are `d/M/yyyy`** (`9/2/2016`, `30/9/2026`). Checked against 20,000 stored DRT order dates: 12,088 have a first part above 12 and **none** have a second part above 12, so day-first is confirmed. Parsing them month-first would silently swap days and months.
- **e-Jagriti dates are UTC timestamps**: `2017-09-27T18:30:00.000+00:00` is midnight IST on **28 September**. Reading the date part gives the wrong day; convert to IST first.
- **e-Jagriti case numbers have three shapes**: full `DC/497/CC/599/2017` (commission code / serial / type / number / year), short `CC/599/2017`, and NCDRC `NC/RP/836/2005`. `getCaseStatus` accepts only the full form.
- **DRT status vocabulary** is `DISPOSAL` / `PENDING` / `TRANSFERED`; the vendor feed has seven spellings of the same states.
- The portals spell several keys wrongly (`fillingReferenceNumber`, `dateOfNextearing`, `Record Not Fund`, `TRANSFERED`); the clients must match them exactly and contract tests must pin them.
- An identifying User-Agent was **accepted** for `getCaseStatus` and for every DRT call (the crawler's notes say a default Python User-Agent is blocked; no browser disguise was needed there). The company search was called with a standard browser User-Agent, so whether an identifying one works for that endpoint is untested.

## 3. Case-number normalisation (Phase 0 — no network)

### 3.1 Structure (new columns; raw vendor columns untouched)
`CaseTypeCode` · `CaseNo` (no leading zeros) · `CaseYear` (int) · `CaseNumberDisplay` = **`TYPE/NO/YEAR`** (`OA/413/2022`, `CC/2/2022`, `SLP(C)/6459/2019`) · `IdentityConfidence` (`Complete` / `Partial` / `Unresolved`) · `ParseNotes` · `ConnectedMatters` (extra numbers from a glued string).

### 3.2 Parsing
Split `A/9/9`, `A A/9/9`, `9/9`, `9`, `A.9/A/9`, escaped slashes, brackets (`SLP(C) No. 006459 - / 2019`). A string with several numbers: the first is the primary, the rest go to `ConnectedMatters`. Dates are parsed per source (**DRT day-first `d/M/yyyy`; e-Jagriti UTC timestamps converted to IST**; vendor `dd-MM-yyyy` and `yyyy-MM-dd`) into real dates, never by guessing from the string. Status is mapped from each source's vocabulary into the existing status buckets. Types are normalised through a **per-forum dictionary** seeded from corpus frequencies and the DRT/consumer mapper files; an unknown type is kept verbatim and marked `Partial`, never guessed.

### 3.3 The identity gate (review finding 1)
**No parsed or inferred value can become shared identity.** Two separate things:

- `CaseNumberDisplay` and the parsed parts are for **display, search and lookup input**. They may be inferred (year from filing date or CNR) and carry `IdentityConfidence`.
- A **shared `CaseKey`** (`FORUM|COURT-KEY|TYPE|NO|YEAR`) is assigned **only** when all of these hold: (a) the court is resolved from an authoritative id (tribunal id, commission id, or a known High Court code), never from free-text court names; (b) the type matched the dictionary exactly; (c) the number is a single token; (d) the year is **explicit** (in the number or the `CaseYear` column), or two independent signals agree (filing-date year and CNR year); (e) for portal-sourced rows, the key is **minted from the identity the portal returned**, which must equal the identity looked up. Anything else stays `Partial`/`Unresolved`: it is kept as a separate unresolved observation, still displayed and still refreshable, but **never linked, merged or deduplicated** on its key.
- A row with a glued number is never keyed as one; each connected number may get its own key only if it passes the gate on its own.

Existing de-duplication (CNR-first, #388) is unchanged. `CaseKey` only adds a second identity for CNR-less DRT/consumer cases, and only for gated rows.

### 3.4 Tests
Golden test from the 626 Coastal rows and the Kotak CSV (6,319) with hand-reviewed expected output including the gate: each shape above becomes a case, plus negatives (year inferred from one signal only → no key; glued number → no key; unknown type → no key; court free text → no key). Idempotent backfill with a report of non-`Complete` rows; the backfill **merges nothing**, it only reports duplicates it finds.

### 3.5 How case numbers are shown (display format)

**One format for everything: `TYPE/NO/YEAR`** — an uppercase type code, the case number without leading zeros, a four-digit year, separated by `/`, no spaces. Always in that order, whatever shape the source used. The full type name ("Original Application") is in the tooltip and the case details; the number **as filed** (the raw text) is always kept and shown in the details and the tooltip, and is never overwritten.

**When a part is not known, show what is known in the same order, and say what is missing — never invent it:**

| What we know | Shown as | Note beside it (muted) |
|---|---|---|
| type, number, year (explicit) | `OA/413/2022` | — |
| number and year only | `175/2017` | "type not stated" |
| type and number only | `SERVICE TAX/75643` | "year not stated" |
| number only | `10555` | "type and year not stated" |
| year taken from the filing date or the CNR rather than the source's number | `WP/10555/2010` | "year inferred from filing date" (tooltip) |
| several numbers in one source string | primary, e.g. `SLP(C)/13999/2013` | "+2 connected matters" (details list them) |
| nothing parseable | the text as filed, in grey | "number format not recognised" |

The portal's own form is kept as a separate "portal number" for lookups and shown in the details, not in the list: consumer `DC/497/CC/599/2017` (commission code / serial / type / number / year) is displayed as `CC/599/2017`, with the commission in the court column; NCDRC `NC/RP/836/2005` is displayed `RP/836/2005`.

**Display is not identity.** What is shown can include inferred parts; the shared `CaseKey` (§3.3) is minted only from the identity a portal returned, never from this text. A row can be perfectly displayable and still have no key.

**Real examples from the 626 Coastal cases** (first-cut rules, run as a throwaway script; the golden tests will fix the exact output):

| Court | Source: number / type column / year column | Shown |
|---|---|---|
| Supreme Court | `SLP(C) No. 006459 - / 2019,(Verified On 23-10-2018)` | `SLP(C)/6459/2019` |
| Supreme Court | `T.C.(C) No. 000253 - / 2020` | `TC(C)/253/2020` |
| Supreme Court, glued | `SLP(C) No. 013999 / 2013,S.L.P.(C)...CC No. 006950 / 2013,…` | `SLP(C)/13999/2013` +2 connected |
| High Court | `10555` / `CIVIL WRIT` / `2010` | `WP/10555/2010` |
| High Court | `292/2016` / `CRLRC` / `2016` | `CRLRC/292/2016` |
| High Court | `134` / `CMP (Civil Misc Petition)` / `2016` | `CMP/134/2016` |
| High Court | `1200` / `ARBITRATION PETITION.` / `2013` | `AP/1200/2013` |
| District | `CMA\\/1146\\/2022` (doubled backslashes) | `CMA/1146/2022` |
| DRT | `41` / `SA-` / `2021` | `SA/41/2021` |
| DRT | `Original Application-` | `OA/…` |
| Consumer | `CC/2/2022` / `consumer case(CC)` | `CC/2/2022` |
| CESTAT | `75643` / `SERVICE TAX` / (no year) | `SERVICE TAX/75643` — "year not stated" |

**What the first cut could and could not do (626 rows, before any tuning):** 88% parse to a complete type/number/year from the source's own text; 6% have a type string that needs a dictionary entry (`Civil Misc.(execution)`, `Income Tax Appeal`, `CONTEMPT PETITION`, `Temporary Injunction`, `2 (c)cc` …); 5% have no number found — doubled backslashes (fixed by a rule), NCLT numbers glued with no separator (`TPNo.93/CTB/2019CPNo.1317/KB/2018`, which also carry a bench code between number and year), a lost value (`/`), and a free-text field (`RESTORATION OF SUITS 0-9 R-4`); 1% have a category in place of a type and no year. **Every one of those is a named golden-test case**, so the dictionary and rules grow from data, not from guesses; unknown types are shown as filed and marked, never guessed (§3.2).

### 3.6 A second sample, and the other fields normalised in the same pass

The individual-borrower tracker export (a sample file named after the borrower, kept outside the repository; 4,537 cases, 23 columns, Punjab/Haryana courts, Supreme Court and the National Consumer Commission) is a different layout from the Coastal database rows and confirms the rules are needed beyond case numbers. What it adds:

**Case numbers.** Five shapes in one file: `NO/YEAR` (3,065) and `NO` alone (704), where the type and year come from the separate `Case Type` and `Case Year` columns (those two columns are never empty here, so ~83% resolve from columns, not from the number); `TYPE - LONG NAME/NO/YEAR` (`CS - CIVIL SUIT/1411/2019`, `EXE - EXECUTION/198/2020`, ~440 rows); `TYPE/NO/YEAR` (consumer `RP/3784/2008`); and Supreme Court text (`SLP(Crl) No. 004537 - / 2026`). New rules: take the code before ` - ` and ignore the long name; **a `Case Type` value that is really a number** (`SLP(Crl) No. 004537 - `) is not a type — the type comes from the number text; `Second Appeal(SA)` → `SA`. The long name can carry information worth keeping: `NACT - 138 NIA ACT` identifies a **section 138 Negotiable Instruments Act** case, which is a financial act for the tier ranking (§ tiering) — captured as a hint on the Act field when that field is empty, never overwriting it.

**One combined hearing-date column.** `LDOH/NDOH` holds a single date (or `NA`) with no indication of which it is. By status in this file: 2,392 disposed cases have a past date, 746 pending-looking cases have a **past** date, 140 pending-looking cases have a future date, 1,027 have none. Rule: a future date on a pending case is a **next hearing**; a past date is a **last hearing / last listing** (never written into next hearing, even for a pending case — a pending case with only a past date is exactly the "hearing has passed, refresh it" case of §4.4); for a disposed case a past date is the last hearing and a *candidate* decision date marked derived. **Dates are day-first** here too (`dd-MM-yyyy` and `dd/MM/yyyy`; 315 slash dates have a first part above 12, none have a second part above 12).

**Status vocabulary** (14 spellings in the top group alone): `Disposed`, `Case disposed`, `DISPOSED`, `DISPOSED OF`, `DISPOSED OFF`, `DISPOSAL`, `DISMISSED`, `ALLOWED` → **disposed** (the last two are outcomes, kept as the outcome words); `PENDING`, `Pending` → pending; **stage words written into the status field** — `Appearance`, `Arguments`, `Prosecution Evidence` — → pending, with the word moved to the stage field if that is empty; `OTHER`, `NA`, empty → **unknown** (treated as pending for refresh, §4.4). One mapping table, one place, tested.

**Court names.** The same court under two names — `PunjabHighCourt` and `High Court of Punjab and Haryana` — and values that carry a state and a list type (`Civil Judge Senior Division,Taluka Court, Ajnala, Punjab, Civil Causelist`). An alias table maps each to one canonical court; the suffix (`, Punjab, Civil Causelist`) is split off into state and a list-type note. Missing state is then filled from the canonical court (92% of one of those names had no state).

**Empty columns.** `Filing Date`, `Case Stage`, `Bench`, `Role` and `Remark` are empty in every row, CNR in 50%, and diary number only on Supreme Court cases. So filing date is *always* a missing field for this feed — which, by §4.4, makes every such pending case a refresh candidate; and the placeholders seen are `NA` (1,024) and empty, which join `IsMissing` (§4.2e).

## 4. Live fetching and updating

### 4.1 Evidence model: immutable observations, one verified projection (finding 5)

- **`CourtCaseObservation` — append-only.** One row per portal call result: endpoint, requested identity, `RetrievedUtc`, HTTP outcome, `Outcome` (`Verified` / `NotFound` / `Ambiguous` / `Failed` / `IdentityMismatch`), `ParserVersion`, and a **compact snapshot of the parsed fields** (small; keeps history readable). **Raw responses are not stored** (owner decision 2026-10-10). Never updated, never deleted by a refresh. Two time concepts, kept apart:
  - **`RetrievedUtc`** — when we asked. Always set; decides ordering.
  - **`SourceUpdatedUtc`** — set **only** from a timestamp the portal itself returns for when the record changed. None of the responses probed on 2026-10-10 carries one, so in v1 it is **null**, and nothing depends on it. (An earlier draft derived it from the latest hearing/order/decision date; a date of an event is not the time the data changed, so that was wrong.)
  - **`EventWatermark`** — the latest hearing, order or decision date in the response. Informational only (timeline, staleness display); it is never used to accept or reject an observation.
- **Hearing history** is new data we do not store at all: a `LitigationCaseHearing` table (date, next date, purpose or stage, proceeding text, court room, order available/locator), filled from the same observation. It gives the case page a real timeline, makes LDOH/NDOH derivable instead of copied, and is where each hearing's order locator lives for §4.5.
- **`CourtCaseLive` — a projection of the last `Verified` observation** per `CaseKey`. Failed, ambiguous, not-found and mismatched observations are stored but **never change the projection**, so a bad refresh leaves the last verified result in place (with `LastVerifiedUtc` and a failure count, so staleness is visible).
- **Fenced publication.** Updating the projection is one compare-and-set: it succeeds only if the job's lease token is current and `PublishedObservationId` is still the value the job read — the same pattern `LitigationOrderDocumentService` already uses for lease-guarded writes. A losing attempt discards its work.
- **Conflict rules.** Ordering is by **fenced retrieval**: a later `Verified` observation wins, so a legitimate correction of an earlier event date (a hearing re-dated, a typo fixed) is accepted — there is no "stale because the date is older" rejection. Guards against a bad response replacing good data, all automatic (no analyst gate): (1) a **status regression** (disposed → pending) is published only after two consecutive agreeing observations or an order that explains it; (2) a **shrinking history** (a verified observation with fewer hearings or orders than the published one) is published only after two consecutive agreeing observations — history rarely loses rows; (3) an observation older than the one already published (an agent finishing late) is stored but never published. Disposed cases are not refreshed (§4.4), so a regression can only surface through an order/judgment fetch.

### 4.2 One consistent view for every consumer (findings 1, 2, 3, 6)

Presentation-only precedence is not enough: **31 files** read `CaseStatus`, `LastHearingDate`, `NextHearingDate`, `DecisionDate` or `CaseStage` directly. So the live values are **published into the existing `LitigationCase` columns** (the entity already defines them as "refreshed to that later observation"); every consumer then sees the same values. Four things have to be specified for that to hold.

**(a) A live-data version that does not exist today (finding 1).**
`RequestPageVersion` currently covers ingestion run, analysis run and status, filing batch, litigation snapshot id, search hash, and litigation-analysis run and status — **nothing that changes when a live row changes**. `ChargeLitigationService.VersionAsync` (which the dossier's memory and disk caches and the charge-link cache key on) is `snapshotId-extractedOrderCount-lastExtractionTicks`, also blind to live changes. The v3 statement that these caches "already key on a litigation data version" was wrong.
- New column `Requests.LiveDataVersion` (bigint, default 0).
- Incremented **in the same transaction** as every publication to the request's cases, discovery insert, merge or un-merge, **hide or un-hide**, order add, and hearing add: `UPDATE Requests SET LiveDataVersion = LiveDataVersion + 1 WHERE RequestId = @id` (atomic; never read-modify-write).
- Consumed by: `RequestPageVersion` (new field in the record and in `Key`, so the optimistic page fence in `RequestsController` and `RequestDetailsQuery` trips); `ChargeLitigationService.VersionAsync` (suffix, which flows into the dossier memory and disk caches and the PDF artifacts); the litigation report artifacts; chat retrieval caches. Exact list found by search at implementation time.
- **Test:** publish one live change, then assert each of those caches and fences misses and rebuilds.

**(b) One case-selection contract, with retained membership (findings 2 and 16).**
Eight places choose a request's cases through `LitigationCaseSourceReport` membership in **one** authoritative vendor snapshot (`LitigationSnapshotSelection` picks the snapshot for the current report hash, else the latest completed one): `LitigationCaseListQuery`, `LitigationDetailsQuery`, `LitigationReportAssembler`, `LitigationCasePageService`, `ChargeLitigationService`, chat `StructuredFactsProvider` and `RetrievalContextBuilder`, and `LitigationReuseService`. Two consequences: a case found only on a portal never appears, **and a vendor-only case that a later snapshot omits disappears from every current view — taking with it the fields we filled** (rows are kept in the database by §4.2e rule 8, but nothing shows them). Both are fixed by one definition.

`LitigationCaseSelection.ForRequest(db, requestId)` returns the **retained set**:
- every case linked to **any completed vendor snapshot of this request** (not only the authoritative one), **plus**
- every case with a `Verified` live link (§4.3),
- **minus** cases merged away (§4.6) and cases the owner has explicitly hidden (a recorded, reversible action with a reason).

Each case carries its provenance as a set — `CourtRecords` (with `last seen <snapshot date>`), `LivePortal`, or both — and a case that the latest vendor snapshot no longer contains is labelled **"Not in the latest court-records feed (last seen `<date>`)"** next to its freshness label (§4.8). It is still counted, listed, reported, analysed and available to chat and charge links. A frozen historical report keeps its own snapshot's membership.

*Decision for the owner:* retaining a case the vendor later dropped is the safe default (nothing we hold silently vanishes), but it means a vendor-corrected false positive stays visible until hidden. The hide action and the label are the remedy; say if you prefer the opposite default.

**Tests (visibility, not just retention):** a vendor-only case present in snapshot 1 and absent from snapshot 2 remains in the list, the counts and totals, the litigation report, the case page, the charge links, the chat facts and retrieval, and the AI analysis input, with the "not in latest feed" label and its filled fields intact; a hidden case disappears from all of them and reappears when un-hidden; a case dropped by the vendor and also live-verified shows both provenances and no warning label.

**(c) Vendor re-ingestion must not overwrite live values (finding 3).**
`LitigationCasePersistenceService.ApplyFields` assigns status, stage, dates, court, state and district unconditionally on every vendor rerun, so a stale vendor snapshot would erase a verified live value. Rule: for a case with a `Verified` live link, **fields the portal owns** (status, stage, filing/last/next/decision dates, court, state, district, parties and advocates, case number parts) are **not assigned from the vendor row**; the vendor's values stay in the snapshot's raw report (`RawReportBytes`) — **not** on `LitigationCaseSourceReport`, which stores only provider/CSP ids (another v3 error, corrected). Fields only the vendor has (act, classification, direction, CNR, provider ids) keep updating. If the live projection is newer than the row, `ApplyFields` re-applies it in the same transaction. Test: publish live → rerun a stale vendor snapshot → the live values and `LiveDataVersion` are unchanged, and a new vendor-only field still updates. The rule for empty values is in (e).

**(d) AI analysis and the hearing dates (finding 6).**
The case-analysis input currently contains `last_hearing_date` and `next_hearing_date` (and `as_of_date`, which is deliberately outside the evidence hash). Excluding the hearing dates from the hash while the model still reads them would reuse conclusions made against old dates. Chosen fix: **remove both hearing dates from the model input as well as from the hash**, instruct the model not to state hearing dates, and bump the prompt version. The case card and chat facts take current dates from the columns, so nothing user-visible depends on the model's words for them. The hash then covers what the analysis reads: status bucket, stage, decision date, parties, order set and text. The prompt change forces **one** re-analysis of existing cases (about $0.003 each on the current model) — scheduled, with a count, not a surprise; and a fixture test confirms an output that states a hearing date is rejected or flagged. `as_of_date` keeps today's behaviour (not hashed); whether conclusions that depend on "today" should expire is a separate, pre-existing question and is out of scope here.

Publication to a request's case is one transaction (columns + `LiveDataVersion` + hearing rows); tier ranking and portfolio synthesis recompute from the same columns when a case's status bucket changes.

**(e) Non-destructive merge: a missing value never erases a value we hold (owner rule, 2026-10-10).**
If we filled a field from the portal and the data lake (the vendor feed) still has it empty, a later vendor refetch must leave our value alone. The same rule applies in every direction and to every source.

*The problem exists today, independently of this work:* `ApplyFields` assigns every field from the vendor row, `null` included, so any vendor rerun that is missing a value erases whatever was there. (Orders are already add-only — `UpsertOrdersAsync` never removes — so only the case fields need this.)

**Rules** (one merge function, `FieldMerge`, used by vendor ingestion and by live publication):
1. **Missing is never a value.** Null, empty, whitespace and placeholders (`N/A`, `NA`, `-`, `--`, `null`, `NULL`, `NIL`, `0000-00-00`, and a list-valued field that is empty) mean "no information". An incoming missing value **does not overwrite** a held value, whatever its source. *This is the same `IsMissing` function that defines "a required field is missing" in §4.4 — one definition, so eligibility and overwrite protection cannot disagree.*
2. **A non-empty value fills an empty field** from any source.
3. **Two non-empty, different values:** if the case is live-verified, the portal value for a portal-owned field wins and the vendor's is ignored (§4.2c); otherwise the newer vendor snapshot wins, as today.
4. **Provenance per field.** A compact `FieldSourcesJson` on the case records, for each field, the source (`Vendor` / `Portal` / `Derived`), when it was set, and the observation. It is what lets rule 3 know which fields the portal owns, drives the per-field "as of · Court portal / Court records" note (§4.8), and is how "confirmed empty" is remembered.
5. **Portal-empty is meaningful only for a short list of fields**, where an empty answer is itself information: **next hearing date** (none listed), **decision / disposal date and nature** (not decided), **suit amount**. For these, a verified portal call that returns empty is recorded as *confirmed empty*, replaces a stale held value, and is not resurrected by a vendor rerun. For every other field (court, state, district, filing date, last hearing, parties, advocates, case-number parts) an empty portal answer is treated as "not provided" and **never blanks** a held value. (The portals never return state or district at all, so those are always filled from our tribunal and commission tables or from the vendor.)
6. **A partial portal response does not erase.** A field that an earlier verified observation had and the latest one lacks is kept unless two consecutive observations agree on emptiness (the same shrink guard as §4.1).
7. **Lists** (petitioners, respondents, advocates): an empty list never replaces a non-empty one; a non-empty list replaces per rule 3.
8. **Rows are never removed by ingestion.** A vendor snapshot that omits a case or an order we hold (because we added it from a portal, or the vendor dropped it) does not delete or hide it; removal is only ever the explicit soft-merge of §4.6.
9. **The vendor's own value is not lost either:** it stays in the snapshot's raw report bytes, so what the data lake said on a given date is always recoverable.

**Where:** `LitigationCasePersistenceService.ApplyFields` becomes a call to `FieldMerge` per field (the vendor-only fields — act, classification, direction, CNR, provider ids — keep their present behaviour except that rule 1 applies to them too). `LitigationReuseService`, which copies cases between requests, uses the same function.

**This can ship before any live fetching** (Phase 0b): it fixes erasure that exists now, needs no network, and gives the later work its foundation.

**Tests:**
- live-fills last hearing → vendor rerun with null last hearing → value and provenance unchanged;
- vendor rerun with a different non-empty value for a portal-owned field → portal value stays; for a vendor-only field → updates;
- vendor fills state → a later vendor rerun with empty state → state stays;
- every placeholder spelling counts as missing; `0` and `false` are not placeholders for numeric/boolean fields;
- portal says next hearing is empty → a stale vendor date is not resurrected by the next vendor rerun;
- portal returns a partial body (no last hearing) after a full one → last hearing kept; kept until two consecutive agreeing observations for a meaningful-empty field only;
- empty petitioner list after a non-empty one → not replaced;
- vendor snapshot omitting a case or an order → both still present;
- a vendor-only case with newer non-empty values still updates exactly as before (regression);
- the same inputs through the reuse service give the same result.

### 4.3 Identity verification: court, number and party (finding 2)
A portal response is applied to a request's case only when **all three** match; the registry's fetch status and the request's borrower verification are **separate records**:

1. **Court / type / number / year.** The returned tribunal or commission id, case type, case number and year must equal the lookup. (The DRT endpoint returns whatever case holds that number — the Kotak tool needed a party check for this reason.)
2. **Party — whole-name, not substring.** After stripping boilerplate (`M/s`, `Ltd`/`Limited`, `Pvt`, `& Anr/Ors`, punctuation, case), a returned party must **equal** the request's current or former legal name from the company master (or a registered alias). Containing the name is not enough. The existing crawler's `STRONG` rule (alias ≥ 12 characters contained in the party name) classifies *"Kotak Mahindra Bank Limited Employees Cooperative Society"* as a match for the bank; that rule is **not** carried over. Names with extra words after the registered name (`Employees`, `Cooperative Society`, `Union`, `Trust`, `Foundation`, a different suffix such as `Prime`/`Securities`) are rejected.
3. **Role.** The matched party must be on a side consistent with the stored direction when the vendor stated one; a conflict is recorded, not resolved by guessing.

Two records, two questions:
- **`CourtCaseLive` fetch status** (per `CaseKey`): *what does the portal say about this case, and did we verify the call?* Independent of any company.
- **`CourtCaseRequestLink`** (per request + case): *is this request's company really a party?* `Verified` / `Rejected` / `Ambiguous`, with the matched party text and rule. **Only `Verified` links publish into that request's `LitigationCase`.** A `Rejected` link never blocks another request that legitimately is a party.

Tests include the cooperative-society case, a differently-suffixed group company, a same-named company at another tribunal, and a number that resolves to a different case.

### 4.4 When a case is fetched, and the flows (owner rules, 2026-10-10)

**Eligibility — decided before any portal call, from data we already hold**

| Case state | Status / hearing refresh | Orders and judgments |
|---|---|---|
| **Disposed** | **Never.** No update, no fetch. | Fetched when orders are requested and the final order is not yet on file; never again once it is. Missing fields are filled only as a by-product of that call (the consumer portal needs its status call to list hearings with orders) — a call is never made just for them. |
| **Pending, or status unknown** | Fetch **only if** (a) the next hearing date is **before today (IST)** — the hearing has passed, so something may have changed; **or** (b) a required field is **missing**; **or** (c) the case has never been live-verified. A next hearing **today or in the future** means no call: nothing new can have happened yet. | Same call returns the hearing list; new orders are fetched from it. |
| **Newly discovered** (party search) | One detail call per genuinely new case, to learn its status. A new disposed case then goes to the orders path only. | as above |

*Required fields* (a gap in any one triggers a fetch even when the hearing is in the future): status, court name, state, district, filing date, last hearing date, next hearing date (for a pending case), and a complete case number (type, number, year). "Unknown status" is treated as pending. **Confirmed-empty rule:** a field the portal itself returned empty on a verified call (a case awaiting its next listing has no next hearing date) is *confirmed empty*, not missing, and does not trigger another fetch for `N` days (proposed 7). Without this, such a case would be fetched on every cycle forever.

This keeps the load on the portals to the cases that can actually have changed, and it stops disposed cases (the large majority in a bank's portfolio) from being re-read.

**Flows**
1. **Refresh.** Select by the eligibility table, build the lookup (§3), call the portal, verify identity (§4.3), then publish (§4.1–4.2). Live portal values are the source of truth for the fields it returns; a wrong match must never overwrite anything (§4.3, §4.6).
2. **Discover missing cases.** Party search per company across DRT/DRAT and consumer commissions with the company-master names; the whole-name rule from §4.3 decides what is added; rejected candidates are logged, not added. New cases pass the §3.3 gate, then de-duplication (§4.6).
3. **Orders and judgments** (§4.5), for pending and disposed cases as in the table.
4. **De-duplicate** (§4.6) after every publication and every discovery.
5. **Agents and traffic.** Several fetch agents run in parallel, each with its own proxy assignment. **Proxy and user-agent rotation are kept** (owner decision — the work is spread over multiple agents). Each work item (case or search) is claimed under a lease token, so two agents never fetch the same item and a crashed agent's item returns to the queue; results are idempotent. A **global per-host cap** across all agents uses the multi-holder operational lease (capacity = agreed total concurrency) so adding agents cannot silently multiply the load; a circuit breaker on repeated 5xx/429 is shared through the database, not held per agent; backoff with jitter; the outage-breaker pattern from the AI runs. Proxy credentials live in configuration/secret store only, never in the repository or logs. Limits apply to **clients only** (owner rule).

### 4.5 Order ingestion needs source adapters (finding 4)
The existing order pipeline is not reusable unchanged: `LitigationOrderDocumentService` requires `BprLitigationOptions`, authenticates to the vendor and downloads through `BprLitigationClient`, and treats a missing `PdfUrl` as failure. What **is** reusable, and stays exactly as is: the claim and lease-token fencing, per-attempt file paths, storage reservation, retention, HTML-as-text handling, text extraction, chunking, embedding, judgment filter, classification.

Change: introduce `IOrderSourceClient` — `CanHandle(order)` and `DownloadAsync(order, maxBytes, ct) → OrderDownloadResult(Ok, Bytes, ContentType, Error, Text, NotPublished)` (the shape `BprOrderDownloadResult` already has). The service picks the adapter from the order's `Source` and no longer requires BPR options unless the adapter is BPR.

| Adapter | Locator on the order | Behaviour |
|---|---|---|
| BPR (existing) | `PdfUrl` | unchanged |
| DRT | URL for `pdf1.php` / `qrpdfview.php` | GET; detect by content (PDF with leading blanks or stray prefix, HTML from `pdf_html.php`, and the case where the order's HTML is embedded in the URL's `file=` parameter); Word/image/zip are stored but marked **unsupported for extraction** in v1, with a reason, not silently dropped; per-order size limit |
| Consumer | `{filingReferenceNumber, dateOfHearing, orderTypeId}` | GET JSON; `dailyOrderPdf` is base64 PDF **or** an HTML note (kept as text); HTTP 400 "Daily Order not available" → `NotPublished` |

`LitigationCaseOrder` gains `Source`, `SourceLocator` (JSON) and `OrderKey` (the source-specific identity below); `PdfUrl` stays for BPR and is never required for the other sources. Scanned PDFs use the existing OCR fallback. DRAT: no orders, shown as unavailable.

**Order identity (finding 7).** A hearing date does not identify an order (several orders can be pronounced on one day), and two URLs do not prove two files. Identity is defined per source and never from the date alone:

| Source | `OrderKey` = |
|---|---|
| BPR (existing) | `(PdfUrl, OrderDate, OrderType)` — the existing unique index, unchanged |
| DRT | `(tribunalId, caseTypeId, caseNo, caseYear, kind[daily/final], orderDate, normalisedFilePath)` where `normalisedFilePath` is the decoded `file=` path after `drtcourt/` with any `***` suffix removed (so `pdf1.php` and `qrpdfview.php` links to the same file collapse); for an order whose HTML is embedded in the URL, the SHA-256 of that HTML |
| Consumer | `(filingReferenceNumber, courtRoomHearingId, orderTypeId)` — the hearing id is unique per hearing, and the type separates a daily order from a judgment at the same hearing |

**Cross-source equivalence** (a vendor order and a portal order for the same case) is established only **after both are downloaded**, when the content hash (bytes, or normalised text for HTML) is equal **and** the cases resolve to the same verified case **and** the order dates match. A matching path, a matching date, or similar names never decide it. Until then the two orders stay separate, flagged `PossibleDuplicateOf`; once verified, the duplicate is soft-collapsed (the one with extracted text survives) and chunking is not repeated for the loser. If the same identity later downloads with **different content**, it is stored as a new document version and the older one is marked superseded, not overwritten.

Tests: two distinct orders the same day for one case both survive; the same DRT file reached through both URL forms collapses; identical content from vendor and portal collapses only after the hash check; same date and similar name but different content stays two orders; changed content under one identity creates a new version.

### 4.6 De-duplication (owner decision: duplicates are merged, not just reported)

Duplicates arise in three ways: the same case twice from the vendor; a vendor row plus the same case discovered on the portal; and the same case reached by two different number spellings (`292/2016` vs `CRLRC/292/2016`).

- **Merge condition — all of:** both rows have a gate-complete `CaseKey` (§3.3) that is **equal**, in the same request, and the live record is `Verified` for that request (§4.3). Same number at a **different** tribunal, a different type (`OA` vs `SA`) or a different year is a different key and never merges. Unresolved/`Partial` rows are never merged; they are listed as *possible duplicates* in a report and stay as they are.
- **Survivor** = the row that is live-verified, else the one with more orders and hearings, else the older.
- **Merge contract (findings 4, 12, 13).** The loser's case id is referenced in more places than orders. The design keeps the number of changed values small so that the merge is simple to reverse and does not disturb existing hashes:
  - **Change only identifiers and flags; never labels or text.** Ids move (`LitigationCaseId`, and an order id only where defined below); chunk **text, its bracketed citation label, and the display columns `CaseNumber`, `Court`, `OrderDate`, `OrderType` stay exactly as embedded**. Citations resolve through ids, so a chunk may show the loser's number spelling — the true label of what was indexed.
  - `LitigationCaseOrder`, hearings, `LitigationCaseSourceReport` links and live links → re-pointed to the survivor.
  - **Orders that collapse (§4.5)**: when both orders have a downloaded document, **nothing is re-pointed**: the loser order, its document and its chunks are flagged `Superseded` (chunks are excluded from retrieval by a `Superseded` filter added to the retrieval queries — `DocumentRetriever`, `LitigationDocumentRetriever`, hybrid search), and its classification is retired. When only the loser's order has a document, that document (and its chunks and classification) is re-pointed to the survivor's order id.
  - `LitigationOrderChunk` → `LitigationCaseId` (and, only in the re-pointed-document case, `LitigationCaseOrderId`) updated; nothing else.
  - **Classifications (finding 13).** The stored evidence hash covers `CaseNumber`, `Court`, `OrderDate`, `OrderType` and the excerpts, plus the prompt (checked in `LitigationOrderClassifier.Hashes`). Because the merge changes none of those, **a re-pointed classification keeps the same hash and stays current with no model call.** Should those labels change for any other reason (a later re-ingestion with corrected metadata), the existing text-only `ContentKey` (excerpts + truncation + prompt version, no labels) allows **carry-forward** — a copy of the result with the new hashes and `ReusedFromClassificationId` set, no model call — provided the order date and type are unchanged; if either changed, the order is reclassified (about $0.001). `NeedsAnalysisAsync` and the outcome lookup treat a carried-forward row as current. Tests: after a merge, a full analysis run makes **zero order-classification calls** (every re-pointed classification is still current), while it **does** re-analyse the survivor case and recompute the portfolio summary — those two are required (see the next bullets) and the test asserts exactly them, so `NeedsAnalysisAsync` is true for the case-analysis part and false for the classification part; a label-only change with equal content → carried forward, zero classification calls; an order date or type change → exactly one classification call.
  - `LitigationCaseAiAnalysis` → **not** re-pointed: the survivor's evidence has changed, so the loser's analysis is retired (`Superseded`) and the survivor is re-analysed incrementally; tier and portfolio entries recompute.
  - Charge–litigation links and the cached comparison → invalidated through `LiveDataVersion`, not edited.
  - The loser keeps its row with `MergedIntoCaseId` and is excluded by `LitigationCaseSelection` (§4.2b), so every list, count, retrieval filter, report and analysis ignores it with one rule. **Vendor re-ingestion resolves through it**: when a later vendor row matches the loser's identity (CNR, provider id, case number), `LitigationCasePersistenceService` follows `MergedIntoCaseId` to the survivor instead of recreating the loser.
- **Change journal, not just an id map (finding 12).** Every changed value is journaled in `LitigationCaseMergeChange(MergeId, Table, RowId, Column, BeforeValue, AfterValue)` — case ids on orders, hearings, links, chunks, classifications and documents; order ids where a document is re-pointed; and every flag set (`MergedIntoCaseId`, `Superseded`, `CollapsedIntoOrderId`). It is written in the same transaction as the merge.
- **Reversal is conditional per value.** For each journal entry: if the current value **equals `AfterValue`**, set it back to `BeforeValue`; if it **differs**, something changed it after the merge (a live publication, a vendor rerun, a new classification) — the later value is **kept**, the entry is reported as a `ReversalConflict`, and nothing is overwritten. Rows created after the merge (a new order downloaded, new hearings, new chunks, new analyses) are not in the journal; they stay with the survivor and are listed in the reversal report with their identity keys so the owner can see what to review. Reversal bumps `LiveDataVersion` and is itself journaled; the loser becomes selectable again, and the survivor and loser are re-analysed incrementally.
- **Test for reversal after intervening work**: merge two cases; then (1) a live publication changes the survivor's status, (2) a vendor rerun updates a vendor-only field, (3) a new order is downloaded and chunked on the survivor, (4) one re-pointed classification is replaced; reverse. Assert: untouched rows are restored exactly (ids, flags, chunk columns byte-for-byte), the four intervening changes are preserved and reported as conflicts or as post-merge rows, no value is overwritten by a stale `BeforeValue`, no row is lost, and a second reversal is a no-op.
- **Nothing is hard-deleted.**
- **Downstream.** The merge bumps `LiveDataVersion` (caches and page fences invalidate) and the survivor's evidence hash changes, so its analysis recomputes.
- **Across requests** the same case appears once per request today; the shared live record (`CourtCaseLive`) is fetched once and published to every request whose link is `Verified`.
- **Further tests:** the cases above, the 951 cross-tribunal groups in the Kotak data (checked by hand on a sample: which are true duplicates and which are different cases that share a number), a merge followed by a reverse, and a merge that must be refused because identity is not verified.

### 4.7 The refresh can take hours: show what we have, improve as updates land (owner clarification, 2026-10-10)

A first refresh of a large company is hours of fetching (every pending case with a missing or past-due field, §4.4), so **nothing waits for it**. The portal shows the data it holds today, honestly labelled, and gets better as batches are published.

**What users see**
- **Immediately**: the current data, unchanged, with no gate in front of the page, the analysis or the dossier.
- **Per case**: a freshness label — `Updated <date IST> from the portal` (verified), `Not yet updated` (queued or pending), or `Not verified` (could not be matched). A case never looks verified when it is not.
- **Per request**: a banner while the refresh runs — "Court update in progress: 1,240 of 6,800 cases checked; started <time>" — and, when it ends, "Court records verified up to <date>" with the count that stayed unverified and why (not found, mismatch, failed).
- **Reports and the dossier PDF**: always state the data's as-of date and how many cases were not yet refreshed when produced. A report is never silently a mix of fresh and stale.

**Progressive publication**
- Results are published **in batches** (proposed: every 100 cases or every 2 minutes, whichever comes first), one transaction and one `LiveDataVersion` bump per batch (§4.2a). Bumping per case would invalidate every cache and page fence constantly for hours.
- Pages and exports always render from the current columns on demand, so a user opening a case mid-refresh sees the latest published batch.
- **Expensive derived work is debounced, not repeated per batch**: the dossier pre-render and the portfolio summary wait for a **quiet period or the end of the live stage** (proposed 10 minutes of no new publications, or completion). Cheap work (case cards, chat facts, counts) follows each batch automatically because it reads the columns.

**AI analysis**
- **It does not wait.** It runs on the current data as soon as its existing prerequisites are met (the litigation search stage, and order text where present), on a **frozen input** (§4.7b), and says exactly which data it used.
- **Re-analysis is incremental and automatic.** After each debounced checkpoint (or at the end of the live stage), cases whose evidence changed — status bucket, stage, decision date, parties, orders or text (§4.2d) — are re-analysed; unchanged cases cost nothing. The portfolio summary re-runs once at the end, not per batch. Cost is therefore about the number of cases that really changed times about $0.003, not hours of repeated work.
- **Short refreshes** (a small company where the estimated remaining time is a few minutes, from queue size and agent throughput) may be waited for, up to `Pipeline:LiveWaitMinutes` (proposed 5); beyond that estimate the rule above applies. This is an optimisation, not a requirement.

**The company-data feed** needs no new waiting: the request-level analysis already follows Ingest, which follows the feed's refresh. A locked or needs-attention company is handled as today.

**Failure and visibility**: if the live stage fails or an agent stalls, everything keeps showing the last published data; the banner says the update stopped and why, and the existing needs-attention board is the place it surfaces.

### 4.7b A consistent input for every AI analysis run (review finding 11)

Today the orchestrator loads cases, order chunks, order documents and context in separate queries when a run is claimed, and **again on every resume** (a lease loss, a restart, a retry). With batches publishing for hours, that can mix versions inside one run. Contract:

1. **Freeze under a publication lock.** A run starts by taking the request's **publication lock** (the repository's existing `SqlSessionLock`, held by every publication, merge and order-text publication for the request). While holding it, for a few seconds, it reads `LiveDataVersion`, loads the case set through `LitigationCaseSelection` (§4.2b), every case's orders and the order-text content, and builds each case's **complete model input** exactly as it will be sent; then it releases the lock. Publications simply wait those seconds. As a belt-and-braces check the version is read again afterwards; if it moved, or the lock could not be taken within 60 seconds, the freeze is **repeated** (up to 3 times) — and if a consistent freeze still cannot be obtained, **the run is deferred** (`Pending` with a next-attempt time a few minutes out). **A run never starts on an input it could not freeze consistently**; there is no "proceed unstable" outcome.
2. **Persist the actual inputs, not just their hashes.** The freeze writes, per case, a row in `LitigationAiAnalysisInput`: case id, evidence hash, and the **full compressed prompt payload** (case fields plus every order's text as sent — the same bytes the model would receive), together with the run's `InputVersion`. Hashes alone cannot reproduce evidence; the payload can. Size is bounded by the existing limits (about 7,000 tokens of payload per case; 1,500 cases ≈ 40 MB compressed to roughly a quarter), kept until the run completes plus 7 days for diagnosis, then deleted — the evidence JSON and hash already stored on each analysis row remain.
3. **Everything the run does comes from those rows.** The model is called with the stored payload; no case, order or text is read from the live tables during the run, **including on resume, retry and lease takeover**. After the call, the case's *current* evidence hash is compared with the stored one: if they differ the result is stored as **`Superseded`** (it is a faithful analysis of the frozen input, and is shown as "analysed on earlier data"), never as current, and the case is re-analysed incrementally. A run whose input rows are missing fails and starts fresh.
4. **Order-text extraction counts as a publication**: it bumps `LiveDataVersion` and changes the text hash, so a case analysed before its text arrived is re-analysed when it does.
5. **What the page may call current.** Two things must agree, not one:
   - **Per case** — the analysis is current only if its stored evidence hash equals the case's current evidence hash; otherwise it is shown as "Analysed on earlier data — re-analysis queued" (§4.8).
   - **For the whole subject** — the portfolio summary is current only if **the current selected case set equals the frozen one** *and* every hash agrees. The manifest therefore stores a `CaseSetHash` (a hash of the sorted case ids that `LitigationCaseSelection` returned, including which were hidden), and the check recomputes the same hash from the live selection. A **newly discovered** case, a case **hidden or un-hidden**, a merge or un-merge, or a vendor case that left or returned changes the set without changing any remaining case's hash — and now marks the summary as "reflects data as of `<date>`; update pending" and queues a new portfolio summary. A case in the live set but not in the manifest is shown as "Not analysed yet" and queued; a case in the manifest but no longer in the live set is dropped from the summary on the next one.
   The summary carries its `InputVersion` and `CaseSetHash`. "Latest run" is decided by this agreement, never by the highest run number, so an older run finishing late can never overwrite or masquerade as a newer result.
6. **Incremental runs** are cheap by construction: unchanged cases carry their analysis forward (existing hash reuse); only `Superseded` or changed cases are read.

**Test — publication during analysis:** start a run on a manifest; publish a batch mid-run that changes one case's status and adds an order; (a) every other case is analysed on its manifest evidence; (b) the changed case is `Superseded`, **not** recorded as current, and a follow-up run re-analyses only it; (c) the run's `InputVersion` is the original; (d) simulate a crash and resume after the publication — still the same manifest and the same outcome; (e) the page shows "earlier data — update pending", and after the follow-up run completes shows current; (f) a slow older run that finishes after a newer one cannot replace the newer analysis or summary; (g) **a case discovered during the run** — every existing case keeps its hash, yet the summary is marked as not current, the new case shows "Not analysed yet" and is analysed by the follow-up run; (h) **a case hidden, then un-hidden, during the run** — the set changes both times, the summary is marked not current each time, and after the follow-up run it matches the final set; (i) a merge during the run changes the set the same way.

### 4.8 Progress bar and "what is not updated yet" labels (owner request, 2026-10-10)

**One definition of freshness, used everywhere.** A single server-side function, `CaseFreshness(case, observations, eligibility)`, decides each case's label from stored facts (latest observation outcome, §4.4 eligibility, merge state). The banner, the case list, the case card, the report and the PDF all call it, so they can never disagree, and no label is ever guessed. Labels:

| Label | Meaning | Look |
|---|---|---|
| **Updated `<date IST>`** | verified on the portal | green |
| **Not yet updated** | queued, or fetch in progress | grey |
| **Not due — next hearing `<date>`** | pending, hearing today or later, nothing missing (§4.4); intentionally not fetched | neutral |
| **Disposed — final order on file** / **Disposed — not refreshed** | disposed cases are not refreshed by design (§4.4) | neutral |
| **Could not verify — `<reason>`** | not found, identity mismatch, ambiguous, or failed after retries | amber |
| **Confirmed empty at portal** | the portal itself has no value (e.g. no next listing) | neutral |
| **Analysed on earlier data — re-analysis queued** | the case's AI analysis was made on evidence that has since changed (stored hash ≠ current hash, §4.7b); shown beside the data label, not instead of it | amber |

**Request banner** (extends the existing litigation-search banner: `.progress` bar and percentage badge):
- Overall bar: cases handled ÷ cases that were eligible when the run started (disposed and not-due cases are excluded from the total, so the bar means something). `role="progressbar"` with `aria-valuenow`.
- Under the bar: "1,240 of 6,800 cases checked · started 14:05 IST · about 2 h 10 min left (estimate from the last 10 minutes' speed)". A phase line shows where it is: refreshing cases → finding missing cases → fetching orders → re-analysing changed cases.
- **Heartbeat.** If no agent has reported for a set time (proposed 10 minutes) the banner changes to "Paused — no fetch agent is working on this" instead of freezing at a percentage; if the stage failed it says so with the reason (§4.7).
- **Live without disturbance.** A small JSON endpoint (`GET /Requests/{id}/court-update/status`) is polled every 10 seconds and updates only the banner and the counts. Unlike the analysis indicator, it **does not reload the page** (that one reloads every 5 s, which is unusable for hours). Polling stops when the run ends. Times use `window.mcaIst`.

**"What is still not updated" panel** (a *Show details* link on the banner; also reachable after the run ends, then titled "Not updated"). Each line is a count that links to the case list filtered to those cases:
- Not yet checked — queued · in progress
- Not due, by design — next hearing today or later · disposed cases
- Could not verify — not found · identity mismatch · failed (with the most common reasons)
- **Fields still missing, by field** — status · court · state · district · last hearing · next hearing · decision date · filing date (missing is kept separate from "confirmed empty at portal")
- **Orders** — judgments/orders not yet downloaded · text extraction pending · formats we cannot read yet (Word, images) · DRAT (no public orders)
- **Analysis** — cases queued for re-analysis · portfolio summary pending

**On the case itself.** The label chip on the list row and card; on the case page, next to Status, Last hearing and Next hearing, a small "as of `<date>` · `Court portal` / `Court records`" note, and "not updated yet" in place of a value that is stale or missing. The case list gets an **Updated** column (sortable) and a **Not updated** filter.

**Reports and PDF.** A "Data currency" box: as-of date, the label counts above, and the sentence that unrefreshed cases show the last known values.

**Tests.** The freshness function for every label and its edge cases (hearing exactly today, disposed with a missing field, merged-away case, a failed retry after an earlier success); the banner total excluding not-due and disposed cases; the heartbeat state; the filters returning exactly the counted cases; the endpoint not reloading the page.

### 4.9 Retry policy for portal fetching

Retries exist at three levels. Each has a different job, and they must not be confused: a call retry survives a blip, an item retry survives an outage, and the breaker stops everyone hammering a portal that is down.

**1. What counts as retryable (decided per response, not per exception type)**

| Response | Class | Action |
|---|---|---|
| Timeout, connection reset, proxy failure or refusal, TLS error | transient | retry |
| HTTP 429, 502, 503, 504 | transient | retry, honour `Retry-After`, slow the whole host down (below) |
| HTTP 500 on a call that normally works | transient, but counted separately | retry; if it repeats for the same endpoint across agents it is treated as a portal defect (the RC/TRC endpoint returns 500 permanently) and the endpoint is marked unavailable, not retried forever |
| **HTTP 200 with an empty body (DRT's way of saying "no such case")** | **NotFound candidate** | **not retried as an error; confirmed first** — see below |
| e-Jagriti HTTP 400 "Case Details are not available" / "Daily Order not available" | NotFound / NotPublished | final, no retry |
| HTTP 400 with validation messages, 403 that persists, 404 | permanent | no retry; recorded as `Failed` with the reason |
| 200 with a body that does not parse or has an unexpected shape | contract failure | no retry; recorded, and counted toward a "portal changed" alarm (§6) |
| Identity mismatch (§4.3) | deterministic | never retried |
| A downloaded order that is not a valid document (truncated, wrong magic bytes, size mismatch) | transient | retry the download; after the retries, store as unsupported with the reason |

**2. Call retries (inside one attempt)**: up to 3 attempts, exponential backoff with jitter (about 2 s, 6 s, 18 s), a **different proxy on each retry** (rotation is kept, owner decision), per-call timeout 20–45 s by endpoint. The attempt count and the last error are kept on the work item.

**3. NotFound is confirmed before it is final.** DRT answers "not found" with an empty 200, which is indistinguishable from a proxy or gateway returning an empty page. So a `NotFound` becomes final only after **two concordant answers through different exit addresses** (the second may be a different agent). One empty answer plus one real answer is a success. This prevents a bad proxy from marking real cases "not found" and, through §4.4, removing them from refresh.

**4. Item retries (across attempts, minutes to hours apart)**: when the call retries are exhausted the item goes back to the queue with a cool-down — about 30 min, 2 h, 8 h, 24 h — up to 5 attempts, then `Failed` with the reason. The panel shows "Could not verify — failed after 5 attempts: `<reason>`" (§4.8). `Failed` is not permanent: it becomes eligible again at the next scheduled refresh after a 24-hour cool-down (§4.4), so a portal that was down for a day heals itself without a manual re-run. A crashed or stalled agent's item returns to the queue when its lease expires; a lease expiry counts as an attempt only from the second time.

**5. Shared circuit breaker and rate adaptation**: the breaker is stored in the database, not per agent. After N consecutive 5xx/429 (proposed 8) across all agents for one host it opens: items are **deferred, not failed, and the attempt is not consumed**, the banner says "Portal not responding — will retry" (§4.7), and one agent sends a probe every few minutes (half-open) until it closes. On 429 the global per-host concurrency cap (§4.4) is halved and recovers slowly (additive increase, multiplicative decrease), so adding agents never turns a rate limit into a ban.

**6. Orders and judgments** use the same classes with their own item state: `NotPublished` is final until the case's next refresh; a download that returns a document is not fetched again; the order identity (§4.5) makes a retried download idempotent, and a retried publish never creates a second order or a second chunk set.

**7. Everything a retry does is idempotent and visible.** Each attempt appends an observation (`Failed` with its reason) but never changes the published projection (§4.1); the status endpoint and the "not updated" panel group failures by reason and count, and a failure rate above a threshold raises the existing needs-attention item for the request.

**Tests** (a fake portal that scripts response sequences): 503, 503, 200 succeeds on the third try via three different proxies; an empty 200 once then data succeeds; an empty 200 twice through different exits becomes `NotFound`; an empty 200 twice through the **same** exit stays unconfirmed; a permanent 400 is not retried; 429 halves the cap; the breaker opening defers items without consuming attempts; an agent that dies mid-item gets the item re-claimed; a truncated PDF is retried then marked unsupported; `Failed` items are picked up again after the cool-down and not before.

## 11. Extending the live updates to the SBI pre-login reports (PR #430)

*Read for integration, not reviewed as a PR: the design document `sbi-prelogin-litigation-design.md` (Rev 12), `LitigationCaseReconciliationService`, `LegalCaseRecord` and `LegalCaseFileParser`, from branch `feat/sbi-prelogin-litigation-search` (open, +12,665 lines, merge state unstable at the time of reading).*

### 11.1 What the pre-login side is
- A **second, different store of cases.** Pre-login cases are not `LitigationCase` rows. They are `LegalCaseRecord`s — the client **tracker-export format** (the same columns as the individual-borrower sample export and the Kotak sheets: Court, Case_Number, Case_Type, Case_Status, Case_Stage, Case_Year, Court_Forum, State, District, Petitioners, Respondents, LDOH_NDOH, Act, Cnr_Number, Filing_Date) — kept inside the assignment job's `DataJson`, each with a stable `CaseRowId` (`C-1`, `C-2`, …), analyst fields (`IsExcludedFromReport`, `ExclusionReason`, `AnalystNote`), and one `DateOfHearing` string for both last and next hearing.
- **The sample file is the real input.** That file shows what these assignments look like: thousands of cases for one individual, filing date empty in every row, status in 14 spellings, one combined hearing-date column (§3.6). So the refresh here is large and mostly *fills missing fields*.
- #430 adds: automated BPR litigation search for the borrower through a child-target queue and worker (leases, lease fences, recovery sweeps, per-target progress), a strict **reconciliation** of each re-import into existing records (CNR match, or an exact six-field match for CNR-less records, one-to-one only; ambiguous or incomplete matches become separate observations), **concluded-case immutability** (status and stage of a disposed case are preserved), write-time version fences (`DataVersion` on the job, `AttemptVersion` on a target), and an assignment workspace with a case lake, single and bulk edits.

### 11.2 Decision: one shared fetcher, a publisher per consumer
*(§12 restructures this: in the module design this publisher is the **projector** of §12.5 during the transition and disappears when the pre-login consumer is flipped.)*
Chosen (best of the options): **keep the shared registry (`CourtCaseLive`, fetched once per case) and add a second publisher** that writes into `LegalCaseRecord`s, instead of moving pre-login cases into `LitigationCase` (a large migration of a feature still in review) or fetching separately per consumer (duplicate load on the portals). A publisher is: *select eligible cases* (§4.4) → *verify identity for this consumer* (§4.3) → *apply with `FieldMerge`* (§4.2e) → *bump this consumer's version* → *report freshness* (§4.8). There are two publishers: `LitigationCasePublisher` (request cases, §4.2) and `PreLoginCasePublisher` (assignment records). Both read the same registry rows.

### 11.3 What changes in #430's code (found by reading it)
1. **`ReconcileCases` overwrites with empties.** On a confirmed match it returns `inc with { CaseRowId, Status, CaseStage, analyst fields }` — i.e. **every other field comes from the incoming vendor record, empty or not**, and for a concluded case only status and stage are protected. A vendor refresh with an empty `DateOfFiling`, `State`, `District` or `DateOfHearing` therefore erases a value the portal had filled — exactly the case the owner described. It must merge through `FieldMerge` (§4.2e): missing never erases; portal-owned fields stay portal-owned; analyst-set fields stay analyst-set.
1b. **`ReconcileCases` also drops every existing record the new report omits.** Its output (`reconciled`) is built only by iterating the *incoming* records; the set of matched existing ids is tracked but never used to keep the unmatched ones. A vendor report that omits a case (or lists it in a different order with a changed identity) therefore removes it from the job's `DataJson`, together with its filled fields, its stable `CaseRowId` and the analyst's exclusion and note — and the monotonic id counter then never reissues that id, so the loss is silent. Required change: after the incoming records are merged, **every existing record that was not matched is carried over unchanged** (same `CaseRowId`, analyst state and fields), marked `NotInLatestFeed` with the date it was last seen, and shown with the "not in the latest court-records feed" label (§4.2b). A record leaves the assignment only by an explicit analyst or owner action, never by omission. Order never matters: the output keeps existing records in their stored order, then appends new ones. The module-side equivalent is the retained set of §4.2b.
2. **Three definitions of "missing".** `LitigationCaseIdentity.IsPlaceholder`, the reconciliation service's own `IsPlaceholder` (`-`, `.`, `NA`, `NIL`, `NULL`, `DUMMY`, `TEST`, `Not Found`), and this plan's `IsMissing`. They become **one** function (the union), used by identity checks, reconciliation, eligibility and `FieldMerge`.
3. **Status classification** (`IsDisposedExpr`, `IsPendingExpr`, `IsUnknownExpr`, `IsConcluded`) is the one status mapping; §3.6's vocabulary table is added to it rather than creating a second.
4. **`LegalCaseRecord` needs new fields**, all optional so existing `DataJson` still loads: `LastHearingDate`, `NextHearingDate`, `DecisionDate`, `FieldSources` (per-field provenance), `LiveCheckedUtc`, `LiveState`. `DateOfHearing` stays, derived for the report and the lake: the next hearing if pending and in the future, otherwise the last.

### 11.4 Rules for the pre-login records
- **Concluded cases are immutable** (their §11.6) — and that is also this plan's rule: disposed cases are not refreshed (§4.4). Orders and judgments may still be fetched for them; fetching orders **never** changes status, stage or any record field.
- **Analyst state wins.** A field an analyst edited is pinned (`FieldSources` = `Analyst`) and never overwritten by the portal or the vendor. Source precedence for a field: **Analyst > Portal > Vendor > Derived**. Exclusion flags, reasons and notes are never touched.
- **Excluded-from-report cases are not refreshed** (they do not appear in the report, and each refresh costs portal load); they join the next refresh if un-excluded.
- **Linking.** A record is linked to the registry only if the portal returns the same court, type, number and year **and** a party equals the assignment's borrower name by the whole-name rule (§4.3) — directors' manual searches use the director's name. Unlinked records are untouched. `CaseRowId` never changes.
- **Publication goes through #430's version fence.** The publisher reads the job's `DataJson` and `DataVersion`, applies `FieldMerge` to the *current* records, and writes with the conditional `DataVersion` update. On a conflict (an analyst saved, a bulk edit landed, a vendor poll reconciled) it **re-reads and re-applies** — `FieldMerge` is idempotent, so a retry never loses the other writer's change. Batches of about 100 cases (§4.7), so a bulk edit is delayed by seconds, never blocked for hours.
- **Execution** reuses #430's pattern: a new child-target kind (`LiveRefresh`) on `PreLoginLitigationTarget` with the same atomic claim, lease token, recovery sweep, `ProgressPercent` and `StatusMessage`. The retry policy (§4.9) and agents (§4.4) are shared with the request side.
- **Report timing.** The `.docx` report is generated as today and **never waits** for the refresh. It carries the data-currency box (§4.8); a **Regenerate report** action (and, when the refresh finishes, a prompt on the assignment) produces the version with the updated values. The workspace shows the freshness chips and the progress bar from §4.8 on Tab 2, with the "what is not updated" panel.

### 11.5 Field mapping for `LegalCaseRecord`
| Record field | Portal source (DRT / consumer) | Rule |
|---|---|---|
| `Status` | `casestatus` / `caseStage` | portal-owned; mapped through the status table; concluded stays |
| `CaseStage` | last proceeding purpose / `caseStage` | portal-owned |
| `LastHearingDate` / `NextHearingDate` | latest past proceeding / `nextlistingdate`, `dateOfNextearing` | portal-owned; next is meaningful-empty (§4.2e) |
| `DecisionDate` | `dateofdisposal` / derived | meaningful-empty; derived for consumer |
| `DateOfFiling` | `dateoffiling` / `caseFilingDate` (IST-converted) | fills the empty filing date in every tracker row |
| `Court`, `State`, `District` | tribunal / commission tables | fill when empty; never blanked |
| `CaseNo`, `CaseType`, `CaseYear` | normalised parts (§3) | display normalised; raw kept |
| `Act` | section hint (`138 NIA ACT`) | only when empty |
| `Petitioner`, `Respondent`, advocates | portal | fill when empty; never blanked |
| `CnrNumber` | — (not returned by either portal) | untouched |

### 11.6 Sequencing and coordination (chosen)
1. **Land #430 first**, as a prerequisite; this plan changes nothing it touches until the integration step. Its own review stays with its author.
2. The shared foundations come next and benefit both sides: the one `IsMissing`/status mapping and `FieldMerge` (Phase 0b), and the case-number normaliser (Phase 0), applied to `LegalCaseRecord` too.
3. Phase 2d (below) adds the `PreLoginCasePublisher`, the record fields, the reconciliation change in 11.3.1 and the `LiveRefresh` target.
4. Both efforts touch `LitigationTabViewModel` (the status classifier) and `BprLitigationReportParser`: the owner of each change coordinates through merge order, not parallel edits of those files.

### 11.7 Tests specific to pre-login
- Live fills `DateOfFiling` → a vendor refresh with it empty (through `ReconcileCases`) → the value stays; a concluded case's other fields are not blanked either.
- An analyst edits status → the portal's value never overwrites it; their exclusion and note survive a refresh and a regenerate.
- A publication racing an analyst bulk edit: the version fence conflicts, the publisher re-reads and re-applies, and **both** changes are present afterwards.
- A record whose party does not equal the borrower name is not linked and is not changed.
- An excluded case is not fetched; un-excluding it queues it.
- A disposed (concluded) record is never refreshed, but its orders are fetched and its status does not change.
- The report is generated while the refresh is running, shows the currency box, and a regenerate after completion shows the new values.
- A `DataJson` written before the new fields existed loads and round-trips unchanged.
- **Omission and reordering through `ReconcileCases`:** reconcile a report that omits two existing records and lists the rest in reverse order → the two omitted records are still present with their `CaseRowId`, filled fields, analyst exclusion and note, marked `NotInLatestFeed`; the others keep their ids; the id counter does not move; reconciling the original report again restores them to normal.

## 12. Architecture: one litigation module that every consumer reads from (owner question, 2026-10-10)

**Recommendation: yes — build a standalone litigation module inside the application, with a hard boundary, designed so it could be lifted out into its own service later. Do not start with a separate deployable.**

### 12.1 Why (what the code shows today)
Two pipelines do the same job. The request side: `LitigationSearchJobService` → report snapshot → `LitigationCasePersistenceService` → `LitigationCase` rows → order download/extraction/chunking → classification. The pre-login side (#430): a BPR search dispatch, polling and envelope validation in `PreLoginLitigationTargetService` (407 lines), its own queue and worker, its own reconciliation (`ReconcileCases`), and its own copy of the cases as `LegalCaseRecord`s in the job's `DataJson`. This plan then adds a third piece of the same work: portal fetching, normalisation, de-duplication, freshness, retries. With "one registry, two publishers" (§11.2) the fetching is shared, but there are still **two stores of case truth**, two reconcilers, two version fences, two sets of rules for what counts as missing or concluded — each of which has already needed a fix in this review.

### 12.2 The boundary
**The module owns everything that is true of a case regardless of who is asking:**
- the case registry and its identity (`CaseKey`, aliases such as `CaseRowId`, merges and their journal) — §3, §4.6;
- observations from every source (vendor snapshots, DRT, consumer portals) and the field values with provenance — §4.1, §4.2e;
- normalisation of numbers, types, status, dates and court names — §3;
- hearing history, orders, documents, text extraction, chunks, embeddings, order classifications (these depend only on the order's text) — §4.5;
- fetching and refresh: eligibility, agents, retries, breaker, progress — §4.4, §4.9, §4.7;
- freshness labels and the module's own version — §4.8, §4.2a;
- source adapters (`ICaseSource`): BPR today, DRT and consumer next, NCLT or eCourts later.

**Consumers keep only what is true for them:** the request page and dossier; the pre-login assignment (borrower, analyst exclusions, notes, report inclusion); the Copilot; **case analysis and risk tiering, which depend on who the subject is** (which party is "us", the target's names) — they read the module's evidence and are parameterised by the subject. A consumer's own data attaches to a case by its registry id: an **overlay**, never a copy.

### 12.3 The contract: subjects, scopes, associations and overlays (review finding 24)

A subject is not a consumer. Two assignments (or an assignment and a request) can have the **same subject** and still have different membership, different hidden or excluded cases, different analyst edits and different display ids. So the contract is scope-aware.

**Concepts**
- **Subject** — the party being checked (name variants, identifiers). Shared; owns nothing but its identity.
- **Scope** — one consumer instance: `(Kind, Id)` with Kind = `Request` or `Assignment` (a pre-login job). Each scope refers to exactly one subject.
- **Case** — registry fact, independent of scope: identity, observations, field values and provenance, hearings, orders, classifications.
- **`ScopeCase` — the association** between a scope and a case, unique on `(Scope, CaseId)`. It carries everything that is true *for that scope only*: the **alias** (the scope's own display id, unique on `(Scope, Alias)` from a per-scope monotonic counter — `C-12` for an assignment keeps its existing `CaseRowId`; `C-1` in one assignment and `C-1` in another are different cases), provenance and feed membership (last seen in *this scope's* vendor imports), **hidden / excluded** with reason, the **analyst note**, **pinned field overrides** (the analyst's edits, with who and when — they win over portal and vendor *inside this scope only*, §11.4), report inclusion, and `RedirectAliases` left by merges.
- **Scope version** — a counter on the scope bumped by every membership or overlay change; it replaces `DataVersion` / `AttemptVersion` style fences for the case set and gives the optimistic write check for analyst edits and bulk updates.

**Calls**
- `GetCases(scope, filter, page)` → the scope's **retained set** (§4.2b) computed from *its* feed memberships and live links, minus *its* merges and hidden cases, with each case's freshness (§4.8) and the scope's overlay applied. Paged, because one scope can have 15,000 cases.
- `GetCase(scope, alias | caseId)`, `GetHearings(caseId)`, `GetOrders(caseId)`, `GetEvidence(scope, caseId)` (the frozen, hash-stable input of §4.7b; the scope's pinned overrides are *not* part of the analysis evidence).
- `SetOverlay(scope, caseId, change, expectedScopeVersion)` / `BulkSetOverlay(...)` — the **only** write a consumer makes. It touches that scope's association row and nothing else.
- `RequestRefresh(subject, policy)` and `GetProgress(subject)`; the scope's view of progress is the subject's progress filtered to the scope's cases. One fetch serves every scope of the subject.
- **Events**, carrying scope ids: `CaseChanged`, `OrdersAdded`, `CaseMerged`, `RefreshCompleted`, `ScopeChanged`. Each consumer maps them to its own caches and re-analysis; the version of §4.2a becomes the module's per-scope version.
- **No consumer reads or writes the module's tables.** Enforced by a separate EF context and database schema and an architecture test that fails if any other namespace references its entities.

**Merges across scopes (§4.6).** A module merge repoints every `ScopeCase` of the loser. In a scope that has both cases, the survivor's alias stays and the loser's alias becomes a `RedirectAlias` (old links, PDFs and URLs keep resolving); the **survivor's overlay wins**, and a differing exclusion, note or pinned edit on the loser is not applied but kept in the merge journal and shown to that scope's analyst as "merged from `<alias>`: …". In a scope that has only the loser, the association is repointed and the alias is unchanged. Reversal restores both. Other scopes are not affected beyond the merge itself.

**Tests (scope isolation):**
- two assignments with the **same subject**: hide a case, exclude another, edit a status and add a note in assignment A → assignment B's list, counts, overlay and report are unchanged, and its aliases are independent (`C-1` in each refers to a different case);
- a portal publication or a new order for a shared case is visible in **both**; each scope's pinned edit still wins in its own scope only;
- bulk updates in A race a refresh: the scope-version check conflicts, retries, and both changes survive; B is untouched;
- a request and an assignment for one subject: hiding in one does not hide in the other;
- merge across scopes: alias redirect resolves, survivor overlay wins, the loser's differing note is shown and journaled, reversal restores both scopes exactly.

### 12.4 What changes in this plan if we do it
- §4.2 (publish into the existing `LitigationCase` columns, `LitigationCaseSelection` over eight consumers) becomes: those eight consumers read the module through one reader. The retained-set rule, the version, the freeze lock and non-destructive merge all live **inside** the module, once.
- §11 (pre-login publisher, `ReconcileCases` through `FieldMerge`, record fields, `DataVersion` races) shrinks to: the pre-login workspace reads the module and stores only its overlay; there is no second copy of statuses and dates to fence or reconcile.
- De-duplication (§4.6) merges **one** canonical case; consumers' aliases follow the merge, so a request and an assignment seeing the same case can never disagree.
- One live fetch serves every consumer of the same case (already the registry's purpose).
- During the transition (§12.5) the publication logic of §4.2 and §11 does not disappear: it becomes the **projector** that keeps not-yet-switched legacy stores correct, and is switched off consumer by consumer.
- Tests move with the code: the golden number-normalisation tests, the non-destructive merge tests, the merge/reversal tests and the retry tests are module tests.

### 12.5 The transition: bridge, projector and ownership (review finding 25)

The earlier order (switch readers, then move BPR ingestion) leaves BPR writing the legacy stores while readers use module data. That is workable only with an explicit bridge. Stages:

| Stage | State |
|---|---|
| T0 | Today. Legacy stores only (`LitigationCase` rows and the pre-login `DataJson`). |
| T1 | Module schema, ids and scopes exist; the reader serves from legacy through a facade; nothing is switched. A parity checker exists. |
| T2 | **Bridge live**: legacy writers feed the module; the module's portal data is projected back into legacy for consumers still on legacy. |
| T3 | Consumers switch one at a time (shadow → promote, §12.6). |
| T4 | BPR ingestion moves into the module (a source adapter). Legacy tables become projections kept for rollback. |
| T5 | Legacy stores retired. |

**Bridge, legacy → module (transactional outbox).** Every legacy writer — `LitigationCasePersistenceService` after a snapshot import, the pre-login `DataJson` write, an analyst overlay edit in the workspace — adds rows to `LitigationBridgeOutbox` **in the same transaction** as its own write (`CaseUpserted`, `OverlayChanged`, `FeedMembershipChanged`, with the legacy reference and a content hash). A bridge worker applies them **idempotently** and in order per legacy reference: upsert the registry case (keyed by `LegacyRef`), append a vendor observation, create or update the `ScopeCase` (alias = existing `CaseRowId` or the request case id), mirror the overlay. Failures retry and then dead-letter with an alert; lag is a metric, and a scope whose backlog exceeds a threshold is served from legacy or marked "syncing" — never served half-synced.

**Projector, module → legacy.** After a module publication (portal fields, hearings, merges), the projector writes the projection into the legacy columns or the `DataJson` records for every scope whose consumer is **still on legacy**, using that store's own safeguards (`FieldMerge`, `DataVersion` conditional write, analyst pinning). This is exactly the publisher logic of §4.2 and §11. **Loop prevention:** projector writes carry `Origin = Projector` and emit no outbox events; the bridge ignores them.

**Backfill.** A one-time, resumable job imports every existing `LitigationCase` and every `LegalCaseRecord` into the registry and scopes: new module `CaseId`, **`LegacyRef` kept permanently** (`LitigationCaseId`, and `(JobId, CaseRowId)`), pre-login `CaseRowId` becomes the alias verbatim, per-scope counters are imported (`NextCaseRowIdIndex`), and analyst exclusions, notes and edits are copied into the `ScopeCase`. It reports duplicates it finds (it merges nothing, §3.4) and is safe to re-run.

### 12.6 Cutover contract (per consumer)

Each consumer has a flag, `Litigation:Reader:<Consumer> = Legacy | Shadow | Module`.
1. **Shadow.** The reader runs beside legacy on live traffic and a **parity checker** compares, per scope: case set, counts by status, every field with the source-precedence rule, aliases, overlays (hidden, excluded, notes, pinned edits), freshness labels. Differences are logged with the case and field. **Promotion needs zero unexplained differences** over a defined window (all scopes of the test companies plus seven days of live use); an explained difference (a known improvement, e.g. a filled field) is listed and accepted explicitly.
2. **Drain.** Briefly lock the consumer's writes for the scope, drain the outbox, run the parity check once more.
3. **Flip to Module.** From now on that consumer's overlay writes go to the `ScopeCase`; the projector keeps the legacy store current **including overlays (reverse mirror)**, read-only to users, for at least two releases.
4. **Rollback** is flipping the flag back: legacy is current because of the projector and the reverse mirror, so no analyst work is lost.
5. **Ids survive.** `LegacyRef` resolves every old URL (`/Litigation/case/{id}`), every `CaseRowId` printed in a report or PDF, and every saved reference; aliases are never reused; `RedirectAliases` cover merges. A report generated before cutover still opens its case links.
6. **Order of consumers** (lowest risk first): case list and counts, the case page, charge links, the litigation report and dossier, chat facts and retrieval, AI analysis; then the pre-login workspace and report (after #430 merges). Each is its own shadow → promote → flip.
7. **T4, BPR into the module.** The module's BPR source adapter takes the writes; the old persistence service and the pre-login child worker become thin triggers; #430's `ReconcileCases` is replaced by the module's scope-aware feed reconciliation, which keeps #430's strict one-to-one rules for CNR-less records and its retention rule from §11.3 item 1b.

### 12.7 Field ownership during the transition

| Data | System of record at T2–T3 | Moves to the module |
|---|---|---|
| Vendor-derived case fields (act, classification, direction, CNR, provider ids, vendor status and dates) | **legacy** | observations arrive by the outbox; module owns them from T4 |
| Portal-derived fields, hearings, observations, live links, freshness | **module** (new data) | projected into legacy for unswitched consumers |
| Case identity (`CaseId`, `CaseKey`), merges and the journal | **module** | `ModuleCaseId` stored on legacy rows as the bridge key |
| Scope overlays (hidden, excluded, notes, pinned edits, aliases) | **legacy until that consumer is flipped, then module** | outbox mirrors legacy → module before the flip; the reverse mirror after it |
| Orders, documents, chunks, embeddings, order classifications | **legacy tables** (they are already keyed by case id and keep their fencing) | reached through the reader via `LegacyRef`; the order source adapters (§4.5) write to them; they move at T4 or later |
| Case analyses and portfolio summaries | **consumer** (subject-dependent) | stay consumer-side |

At no time does a field have two writers: each row of this table names one owner for the stage, and the parity checker verifies it.

### 12.8 What to do about #430
**Land #430 as it is** — it is working, scoped and needed for SBI now — and treat its litigation search, reconciler and `LegalCaseRecord` store as step 4's migration target, not as something to rework mid-review. New work (live refresh, normalisation, merge) targets the module from day one, so #430's duplicated pieces shrink instead of growing. If you would rather restructure #430 now, the cost is a delay to SBI delivery and a larger diff to review; I do not recommend it.

### 12.9 Costs and risks of the module approach
- More up-front design than "add a publisher": the reader, the subject concept, the events and the schema split must exist before the first consumer switches.
- A period with two stores and a bridge (§12.5): mitigated by one owner per field per stage (§12.7), idempotent outbox, projector with loop prevention, shadow comparison and a flag-based rollback (§12.6).
- Paging and performance for the 15,000-case subject must be designed in the reader, not bolted on.
- Case analysis stays consumer-side (subject-dependent); if later more than one consumer needs the same analysis for the same subject, it can move into the module as a subject-parameterised service.
- Separate deployable: **not now.** The deployment is a single local SQL Express application used by one team; a network service would add failure modes and operations cost for no current benefit. The schema/context/interface boundary keeps that option open.

**Decision recorded (owner's call):** the module as the home of litigation data, built inside the application behind the contract in 12.3, introduced through the bridge and cutover of 12.5–12.7, with #430 landing first.

## 5. Phases (each ends in something testable)

Rewritten around the module (§12); the earlier per-consumer publishers are now the **projector** (§12.5) and the duplicated pre-login fields are replaced by the scope overlay (§12.3).

| # | Phase | Network | Size |
|---|---|---|---|
| 0 | **Foundations in the module namespace, no dependency on any consumer**: case-number normaliser with the identity gate (§3.2–3.6, golden tests from both sample files), status / date / court normalisation incl. the combined `LDOH/NDOH`, one `IsMissing`, `FieldMerge` | no | M |
| 0b | **Non-destructive merge in the legacy ingestion that exists today** — `ApplyFields`, the reuse service and #430's `ReconcileCases` (including retaining unmatched records, §11.3) — fixes erasure now; ships on its own | no | S–M |
| M1 | **Module skeleton**: own schema and EF context, `CaseId` + `LegacyRef`, `Subject` / `Scope` / `ScopeCase` with per-scope alias counters and scope versions (§12.3), the reader as a facade over legacy, architecture test, parity checker — nothing switched | no | L |
| M2 | **Bridge** (§12.5): outbox from legacy writers, idempotent bridge worker, resumable backfill with `LegacyRef` and alias preservation, projector (module → legacy) with loop prevention, lag metric | no | L |
| 1 | DRT source adapter (C# client: party search, case by number, orders), recorded-sample contract tests, comparison with the Kotak corpus on ~50 random cases | yes | M |
| 2a | Module observations and projection, identity verification (§4.3) with its negative tests, eligibility selection (§4.4), fenced publication inside the module, projection into legacy for unswitched consumers | yes | L |
| 2b | Consumer-facing integration: module events and per-scope version (§4.2a), the retained set (§4.2b), vendor re-ingestion guard (§4.2c), hearing dates out of the analysis input with a prompt version bump (§4.2d), frozen analysis payloads with `CaseSetHash` (§4.7b) | no new calls | L |
| 2c | De-duplication and reversible merge with the change journal and scope-aware merge rules (§4.6, §12.3), retrieval `Superseded` filters, order identity and equivalence (§4.5) | no new calls | L |
| 3 | Consumer-portal source adapter: client, refresh | yes | L |
| 4 | Discovery with whole-name matching and audit log | yes | M |
| 5 | Order source adapters (§4.5) and OCR check — **first deliverable: the "are properties really found in the orders, and how are they shown" report on a few companies** | yes | M |
| 6 | Fetch agents (leases, proxy assignment, shared breaker, global per-host cap), the `LitigationLive` stage and batched publication (§4.7), the progress banner, "not updated" panel and freshness labels (§4.8), scheduling, admin controls | yes | L |
| S1 | **Switch the request-side consumers** one at a time, each shadow → promote → flip (§12.6): list and counts, case page, charge links, report and dossier, chat, AI analysis | no new calls | L |
| S2 | **Switch pre-login** (after #430 merges): overlay migration into `ScopeCase`, the workspace and report read the module, `LiveRefresh` target, freshness chips and progress (§11) | no new calls | L |
| S3 | **BPR ingestion into the module** (T4): source adapter, thin triggers, scope-aware feed reconciliation | no new calls | L |
| S4 | Retire the legacy stores and the projector once nothing reads them | no | M |
| 7 | NCLT (own investigation) | — | later |

## 6. Risks and open decisions

*(See §12 for the module question; §11 and §4 describe the same behaviour whether the code lives in the module or beside the consumers — §12.4 says what moves.)*

**Owner decisions (recorded 2026-10-10)**
1. **Proxies and rotation are kept**, because fetching runs through multiple agents. (My earlier recommendation was a fixed rate without rotation. A legal read of both portals' terms is still advisable, but it does not block the work.)
2. **The live portal is the source of truth** for the fields it returns, **provided the match is certain**: identity is verified on court, type, number, year and whole-name party (§4.3), and similar-but-different cases are tested explicitly (§4.6).
3. **Duplicates are merged** (§4.6).
4. **Disposed cases are not updated or fetched**, except to fetch their judgments and orders (§4.4).
5. **Cases whose next hearing is today or later are not sent for a status/hearing update**, unless a required field is missing, in which case it is fetched (§4.4).
6. **Raw responses are not kept** (§4.1). A compact parsed snapshot per observation is kept instead so history stays readable; say if you want that dropped too.

**Decided on the owner's instruction "choose the best" (2026-10-10; the owner can overrule any of these)**
7. **Cases the vendor later drops are retained** in current views with a "not in the latest feed" label and an owner **hide** action (§4.2b) — nothing we hold silently vanishes.
8. **Hearing dates leave the AI input** and the evidence hash (§4.2d), with one scheduled re-analysis whose count and cost are shown before it runs.
9. **The required-field list in §4.4 stands as drafted**, plus the pre-login fields in §11.5.
10. **One shared fetcher with a publisher per consumer** (§11.2), not a migration of pre-login cases into `LitigationCase`.
11. **Excluded-from-report cases are not refreshed** (§11.4); analyst-edited fields are pinned (§11.4).
12. **#430 lands first**; this work follows it (§11.6).

**Still open**
- **Required-field list** in §4.4 — please confirm or change it; it decides how often a case with a future hearing is still fetched.

**Technical risks.** Undocumented endpoints can change (contract tests fail loudly). Consumer parties are often individuals — apply the PII handling from the case-risk reference before showing parties in reports. DRAT has no orders and RC/TRC is down at the source. Backfilling keys may reveal existing duplicates; the backfill itself only reports them, and merging happens afterwards under the rules in §4.6. Word/image orders are stored but not extracted in v1.

## 7. Deferred
NCLT/NCLAT live fetching; High Court and district-court (eCourts) crawling (EcourtCrawler's architecture document describes eCourts but the code does not implement it); extraction from Word/image orders; AI changes in the old Python tools (reference only).

## 8. Review log (v1 → v2)

| Finding | Resolution |
|---|---|
| 1. Inferred identifiers become shared identity | §3.3: a shared key needs an authoritative court id, exact type, single number and an explicit or doubly-corroborated year; for portal rows the key comes from the identity the portal returned. Everything else stays an unlinked observation. Negative tests listed. |
| 2. Party matching too weak; borrower vs registry | §4.3: whole-name equality against the company master (substring/`STRONG` rule rejected, with the reproduced cooperative-society case as a test); court/type/number/year re-verified; fetch status and request link are separate records. |
| 3. Presentation-only resolver | §4.2: publish into the existing columns (31 consumers read them), one transaction, data-version bump, analysis invalidation defined and to be measured. |
| 4. Order ingestion not reusable unchanged | §4.5: `IOrderSourceClient` adapters for BPR, DRT and consumer; what is kept (fencing, storage, extraction) and what changes (`Source`, `SourceLocator`, `OrderKey`, no BPR dependency). |
| 5. Evidence history and conflicts | §4.1: append-only observations with retrieval time and `SourceAsOfUtc` kept apart, projection = last verified, fenced compare-and-set publication, failed/ambiguous refreshes never change it, explicit conflict rules. |

## 9. Change log (v2 → v3, owner decisions)

| Decision | Where applied |
|---|---|
| Keep proxies and rotation (multiple agents) | §4.4 flow 5 (agents, leases, shared breaker, global per-host cap, secrets handling), §6 |
| Live portal is the source of truth, but never a wrong match | §4.3 unchanged and restated in §4.4 flow 1, §4.6 tests, §6 |
| De-duplicate cases | new §4.6, Phase 2 |
| No update/fetch of disposed cases except judgments and orders | §4.4 eligibility table |
| No status/hearing update when the next hearing is today or later, unless fields are missing | §4.4 eligibility table and required-field list |
| No raw responses | §4.1 (parsed snapshot kept instead), §6 |

## 10. Change log (v3 → v4, seven review findings)

| # | Finding | Resolution |
|---|---|---|
| 1 | The shared litigation version does not exist | §4.2a: new `Requests.LiveDataVersion`, atomic increment in each publication/merge/discovery transaction, consumed by `RequestPageVersion.Key`, `ChargeLitigationService.VersionAsync` (dossier and charge-link caches), report artifacts and chat caches; test that each misses after a live change. v3's claim that caches already key on such a version was wrong. |
| 2 | Discovered cases never enter listings or reports | §4.2b: one `LitigationCaseSelection` (vendor snapshot ∪ verified live links − merged) used by all eight places that select cases; separate `Provenance` shown. |
| 3 | Vendor ingestion overwrites live values | §4.2c: portal-owned fields are not assigned from vendor rows for a verified live case; re-applied in the same transaction; test of live publication followed by a stale vendor rerun. Corrects v3: vendor values live in the snapshot's raw bytes, not on the source report. |
| 4 | Merge omits indexed evidence | §4.6: merge contract covering orders, hearings, links, chunks (ids and display columns; text and embedding untouched), classifications, analyses (retired, not re-pointed), caches, with a reversible map and a one-rule exclusion. |
| 5 | Event dates are not source-update times | §4.1: `SourceUpdatedUtc` only from a real source timestamp (null in v1); `EventWatermark` informational; ordering by fenced retrieval; corrections accepted; guards for regression, shrinking history and late agents. |
| 6 | Excluding dates from the hash reuses stale conclusions | §4.2d: hearing dates removed from the model input as well as the hash, prompt version bumped, one scheduled re-analysis, card and chat read the columns. |
| 7 | Order dedup contract too weak | §4.5: per-source `OrderKey` (DRT normalised path/HTML hash; consumer hearing id + type), cross-source equivalence only by content hash plus verified case and date, new versions for changed content, tests for distinct same-day orders. |
| 8 | The refresh can take hours; show current data meanwhile | §4.7 rewritten: no gate anywhere; per-case freshness labels and a request banner; batched publication with one version bump per batch; dossier pre-render and portfolio summary debounced to a quiet period or the end; AI analysis runs on current data and re-analyses only changed cases at checkpoints; a short wait only for small refreshes. |
| 9 | Progress bar and a label for what is still not updated | §4.8: one `CaseFreshness` function behind every label; banner with overall bar, ETA, phase and heartbeat; non-reloading status endpoint; a "what is not updated" panel with filtered counts (cases, fields, orders, analysis); per-case chips and "as of" notes; report data-currency box. |
| 10 | Have retries been added? | §4.9: retry classes decided per response (DRT's empty-200 "not found" is confirmed through two exits before it is final), call retries with proxy change, item retries with cool-downs up to 5 attempts, a shared database breaker with rate adaptation, order downloads idempotent, and scripted-portal tests. |
| 11 | Analysis can mix publication versions | §4.7b: version-bracketed freeze into a manifest (case ids, evidence hashes, text hashes); the run builds inputs only from the manifest and checks each case's hash at the moment of use; resumes keep the manifest; "current" is decided per case by hash agreement, never by run number; the summary carries its `InputVersion`; publication-during-analysis test. |
| 12 | The reversal map cannot restore all fields | §4.6: a per-value change journal (table, row, column, before, after) covering case ids, order ids and every flag; reversal restores a value only if it still equals `AfterValue`, keeps later values and reports conflicts, leaves post-merge rows with the survivor and lists them; merge-aware vendor ingestion; reversal-after-intervening-work test. |
| 13 | Repointed classifications are not automatically current | §4.6: the merge changes ids and flags only, never the label columns or text, so classification hashes (which cover case number, court, date, type and excerpts) are unchanged and no revalidation is needed; if labels change later, the text-only `ContentKey` allows carry-forward when date and type are unchanged, otherwise one reclassification; tests for zero calls after a merge. |
| 14 | (Owner, v4.3) A refetch must not erase a field we filled when the data lake still lacks it | §4.2e: missing is never a value; fills only empties; per-field provenance; portal-empty is meaningful only for next hearing, decision/disposal and suit amount; partial responses do not erase; lists, rows and orders are never removed by ingestion; the same `IsMissing` as §4.4; ships first as Phase 0b; tests listed. Also fixes erasure that exists today in `ApplyFields`. |
| 15 | (Owner, v4.3) In which format are case numbers shown, given three source shapes? | §3.5: one display format `TYPE/NO/YEAR`; rules for each partial combination; raw text and the portal's own number kept; display is separate from the shared key; real before/after examples and first-cut results on the Coastal rows. |
| 16 | (Owner, v4.4) A second sample file | §3.6: five case-number shapes, combined `LDOH/NDOH` column rules, status vocabulary map (outcome and stage words), court-name alias table, empty-column facts, `NA` placeholder, section 138 hint from the type text. |
| 17 | The analysis manifest does not freeze the actual inputs; it can start on an unstable read | §4.7b: freeze under the request's publication lock; persist the full compressed payloads; the run uses only those rows, including on resume; stale results stored as `Superseded`; if a consistent freeze cannot be obtained the run is deferred, never started. |
| 18 | Vendor-omitted cases can disappear from views | §4.2b: `LitigationCaseSelection` returns a retained set (any completed vendor snapshot ∪ verified live links − merged − owner-hidden) with a "not in latest feed" label; visibility tests across list, counts, report, case page, charge links, chat and analysis; owner decision flagged. |
| 19 | The merge test contradicts required re-analysis | §4.6: the test asserts zero order-classification calls and the required survivor-case and portfolio re-analysis, separately. |
| 20 | (Owner, v4.5) "Choose the best"; extend the case updates to the SBI pre-login reports; look at PR #430 | §6 items 7–12 record the choices; new §11: shared registry with a second publisher, the record fields, `ReconcileCases` blanking found by reading #430, one `IsMissing`, analyst pinning, version-fenced publication, `LiveRefresh` target, report timing, field mapping, sequencing, tests; Phase 2d. |
| 21 | (Owner, v4.6) Instead of two parallel processes, a standalone litigation module for all consumers | §12: a module inside the application with a hard boundary (own schema/context, reader API, events, subjects, overlays); the module owns case truth, normalisation, fetching, refresh and order evidence; consumers keep only overlays and subject-dependent analysis; strangler migration order; #430 lands first and becomes the pre-login migration target; separate deployable deferred. |
| 22 | Pre-login reconciliation can still discard retained cases | §11.3 item 1b: `ReconcileCases` builds its output from the incoming records only, so omitted existing records (with ids, filled fields, analyst notes) vanish; unmatched existing records are carried over unchanged and marked `NotInLatestFeed`; omission and reordering regression test in §11.7. |
| 23 | Portfolio freshness misses case-set changes | §4.7b item 5: the manifest stores a `CaseSetHash`; the summary is current only if the live selection's set equals the frozen set and all hashes agree; discovery, hide/unhide, merge and feed changes mark it not current; hide/unhide bump the version; tests (g)–(i). |
| 24 | Consumer scope missing from the module contract | §12.3: Subject / Scope / `ScopeCase` association with per-scope alias, feed membership, hide/exclude, notes, pinned overrides and `RedirectAliases`; scope-aware calls; overlay-only writes with a scope version; merge rules across scopes; scope-isolation tests (two assignments, one subject). |
| 25 | Migration lacks a bridge and cutover contract; phase table stale | §12.5–12.7: stages T0–T5, transactional outbox + idempotent worker, projector with loop prevention, backfill preserving ids and aliases, per-consumer Legacy/Shadow/Module flags with parity checks, rollback by reverse mirror, one owner per field per stage; §5 rewritten with phases M1, M2, S1–S4. |
