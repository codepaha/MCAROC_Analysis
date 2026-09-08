# Functional Specification Document (FSD)
## MCA ROC AI Analysis Report for BFSI

---

# 1. Document Purpose

The purpose of this document is to define the functional requirements for an **AI-based MCA ROC Analysis Portal** for BFSI users.

The application will allow users to upload MCA/ROC company reports and charge reports and generate a structured **Corporate Credit Intelligence Report**.

The system should not only extract and display MCA data. It should also:

- identify adverse events;
- detect changes and trends;
- generate section-wise flags;
- identify positive indicators;
- compare financial performance across years;
- compare company metrics against peers where peer data is available;
- analyse directors and promoter movements;
- analyse ROC charges and securities;
- identify collateral/property reuse across multiple charges;
- analyse MSME, GST, EPFO and statutory compliance;
- analyse litigation;
- correlate multiple sections;
- generate lender-oriented observations;
- identify items requiring manual review.

The system will assist BFSI credit and risk teams. It will **not automatically approve or reject a borrower**.

---

# 2. Business Objective

The objective is to convert fragmented MCA/ROC information into a lender-friendly corporate intelligence report.

The report should help a BFSI analyst answer the following questions quickly:

1. Is the company legally active and compliant?
2. Is the business growing or contracting?
3. Is profitability improving or deteriorating?
4. Is the company generating sufficient cash?
5. Is leverage increasing?
6. Can the company service its debt?
7. Are receivables or supplier payments becoming stressed?
8. Who owns and controls the company?
9. Are directors/promoters entering or exiting?
10. Are directors connected with distressed entities?
11. What borrowings and ROC charges currently exist?
12. Which assets/properties are already charged?
13. Is the same property/security used against multiple facilities?
14. Are charges being materially enhanced?
15. Are there GST, EPFO, MSME or MCA compliance concerns?
16. Are auditors raising adverse observations?
17. Is litigation increasing?
18. What has changed since the previous report?
19. What are the major positive indicators?
20. What needs manual review?

---

# 3. Scope

## 3.1 In Scope

The application shall analyse the following major sections where available in the uploaded MCA/ROC report:

1. Company Master
2. Company Status
3. Business Activity
4. Capital Structure
5. Directors
6. Director History
7. Other Directorships
8. Connected Entities
9. Shareholding
10. Major Shareholders
11. Securities Allotment
12. Financial Statements
13. Balance Sheet
14. Profit & Loss
15. Cash Flow
16. Financial Ratios
17. Peer Comparison
18. Related Party Transactions
19. ROC Charges
20. Charge History
21. Charge Security / Property Details
22. Lender Exposure
23. MSME Payment Details
24. GST
25. EPFO
26. Auditor Comments
27. Litigation
28. Legal History
29. Corporate Event Timeline
30. Cross-Section AI Analysis
31. Positive Indicators
32. Red Flags
33. Manual Review Items
34. Changes Since Previous Report

---

# 4. User Roles

## 4.1 Analyst

The Analyst shall be able to:

- upload MCA/ROC reports;
- upload detailed charge reports;
- initiate AI analysis;
- review extracted data;
- review section-wise flags;
- review source evidence;
- correct incorrectly extracted values if required;
- download generated analysis reports.

## 4.2 Reviewer / Credit Manager

The Reviewer shall be able to:

- access generated reports;
- review AI observations;
- review high-priority flags;
- add comments;
- accept/reject individual AI findings;
- download final analysis.

## 4.3 Administrator

The Administrator shall be able to configure:

- risk thresholds;
- materiality thresholds;
- financial ratio thresholds;
- charge enhancement thresholds;
- director churn thresholds;
- peer benchmark rules;
- report sections;
- severity levels;
- AI prompt versions.

---

# 5. Input Documents

The system should support:

- MCA company report;
- ROC company report;
- detailed ROC charge report;
- financial statements;
- balance sheet;
- profit & loss statement;
- cash flow statement;
- auditor comments;
- GST details;
- EPFO details;
- MSME disclosures;
- legal history;
- peer comparison information.

Supported formats may include:

- XLS
- XLSX
- CSV
- PDF
- JSON

Where multiple reports are uploaded for the same company, the system should combine the information.

---

# 6. Processing Flow

The high-level process shall be:

```text
Upload MCA / ROC Report
        ↓
Document Type Detection
        ↓
Section Detection
        ↓
Raw Data Extraction
        ↓
Data Normalisation
        ↓
Validation & Cross-Checking
        ↓
Derived Metric Calculation
        ↓
Section-wise Rule Engine
        ↓
Trend Analysis
        ↓
Cross-Section Analysis
        ↓
AI Interpretation
        ↓
Severity Classification
        ↓
Corporate Credit Intelligence Report
```

---

# 7. Analysis Architecture

The system should maintain four analysis layers.

## Layer 1 – Extracted Facts

Facts directly extracted from the source.

Example:

```text
Revenue FY25 = ₹36.46 Cr
Promoter Holding = 99.93%
Charge Amount = ₹15 Cr
```

## Layer 2 – Derived Metrics

Values calculated by the system.

Example:

```text
Revenue Growth = -25%
Charge Enhancement = 650%
Debtor Days = 95 Days
```

## Layer 3 – Rule-Based Flags

Examples:

```text
Revenue decline >20% → Material Revenue Decline
Director exits >2 in 12 months → Board Turnover
Charge increased >100% → Material Charge Enhancement
```

## Layer 4 – AI Interpretation

AI shall combine facts, derived values and rules into lender-oriented observations.

Example:

> Revenue has declined for two consecutive years while operating margins remain negative, indicating sustained operating weakness.

---

# 8. Severity Framework

The system shall use the following severity levels.

## Critical – Red

Potentially material adverse corporate, financial, security or compliance issue.

Examples:

- company under CIRP;
- negative net worth;
- material auditor going-concern qualification;
- repeated operating losses with increasing leverage;
- material property/security conflict;
- significant financial default.

## High Review – Orange

Significant issue requiring lender review.

Examples:

- major revenue decline;
- material charge enhancement;
- promoter/director instability;
- excessive debtor days;
- significant MSME dues.

## Watch – Yellow

Emerging or moderate concern.

Examples:

- minor filing delays;
- moderate ownership changes;
- isolated EPFO delay;
- ageing open charge.

## Information – Blue

Material event but not necessarily adverse.

Examples:

- new director;
- new lender;
- GST registration cancellation where another registration remains active;
- high promoter concentration.

## Positive – Green

Positive stability or improvement indicator.

Examples:

- debt reduction;
- charge satisfaction;
- stable management;
- timely GST filing;
- clean auditor opinion.

---

# 9. Section 1 – Executive Dashboard

The first page shall provide a consolidated borrower view.

## Data Points

- Company Name
- CIN
- PAN
- Incorporation Date
- Company Age
- Company Status
- Active Compliance Status
- Industry
- Promoter Holding %
- Current Directors
- FY Revenue
- Revenue Growth
- EBITDA Margin
- PAT Margin
- Net Worth
- Current Ratio
- Debt / Equity
- Interest Coverage
- Open ROC Charges
- Number of Open Charges
- Largest Charge
- MSME Outstanding
- GST Status
- EPFO Status
- Auditor Status
- Litigation Count

## Output Blocks

### Overall Corporate Status

Example:

```text
Corporate Status        Green
Financial Performance   Red
Borrowing / Charges      Orange
Compliance               Green
Governance               Green
Working Capital          Orange
```

## Executive Findings

The system should display:

- top 3–5 critical/adverse findings;
- top review items;
- major positive indicators.

---

# 10. Section 2 – Company Profile

## Data Points

- Company Name
- CIN
- PAN
- LEI
- Company Type
- Company Category
- Company Sub-category
- Listed / Unlisted
- Incorporation Date
- ROC
- Registration Number
- Authorised Capital
- Paid-up Capital
- Registered Address
- Business Address
- Email
- Website
- Industry
- NIC Code
- Main Business Activity

## Derived Metrics

- Company Age
- Paid-up Capital / Revenue
- Open ROC Charges / Paid-up Capital
- Net Worth / Paid-up Capital

## Flags

### Company Status Adverse

Trigger if status is:

- Strike Off
- Under CIRP
- Under Liquidation
- Dormant
- Amalgamated
- Inactive

### Active Non-Compliant

Trigger when company is active but compliance status is adverse.

### Newly Incorporated Company

Configurable threshold.

Example:

```text
Company age < 3 years
```

### Business Activity Mismatch

Trigger when business activity reported across MCA/GST/financials differs materially.

### Address Inconsistency

Trigger when MCA, GST and EPFO addresses materially conflict.

---

# 11. Section 3 – Director Analysis

## Data Points Per Director

- Director Name
- DIN
- Designation
- Original Appointment Date
- Current Appointment Date
- Cessation Date
- Current/Historical Status
- DIN Status
- KYC Status
- Promoter Status
- Tenure

## Derived Metrics

- Current Director Count
- Average Director Tenure
- Longest Director Tenure
- Appointments in last 12 months
- Appointments in last 24 months
- Cessations in last 12 months
- Cessations in last 24 months
- Board Turnover %

## Flags

### Recent Director Exit

Trigger:

```text
Cessation Date within last 12 months
```

### Multiple Director Exits

Example:

```text
2 or more directors ceased within 12 months
```

### Majority Board Exit

Trigger where more than 50% of board changes within configured period.

### Promoter Director Exit

Higher severity when exiting director is a promoter.

### Long-Serving Director Exit

Higher severity if:

```text
Director tenure >5 years
AND Director ceased recently
```

### Short-Tenure Director Exit

Example:

```text
Tenure <2 years
```

### New Director Appointment

Informational unless combined with other governance changes.

### Board Churn

Trigger where multiple appointments and exits occur within 12–24 months.

### DIN/KYC Issue

Trigger:

- DIN disqualified;
- DIN deactivated;
- DIR-3 KYC not filed.

---

# 12. Section 4 – Director Network / Other Directorships

## Data Points

- Director
- Connected Company
- CIN/LLPIN
- Appointment Date
- Cessation Date
- Company Status
- Active Compliance
- Charges
- Paid-up Capital
- Current/Historical Association

## Flags

### Connected Company Under CIRP

Severity should depend on whether association is:

- current;
- historical.

### Connected Company Struck Off

### Connected Company Under Liquidation

### Connected Company Active Non-Compliant

### Multiple Distressed Connected Entities

Trigger when same director is associated with multiple adverse entities.

### High External Directorship Count

Threshold configurable.

## Important Rule

Historical and current associations must always be distinguished.

Incorrect:

> Director is associated with CIRP company.

Correct:

> Former director had a historical association with an entity currently recorded as under CIRP.

---

# 13. Section 5 – Ownership & Shareholding

## Data Points

- Promoter Holding %
- Public Holding %
- Institutional Holding
- Foreign Holding
- Total Shareholders
- Promoter Shareholders
- Major Shareholders >5%
- Shareholder Name
- Holding %
- Promoter / Non-Promoter
- Historical Holding %

## Derived Metrics

- Largest Shareholder %
- Top 3 Shareholders %
- Promoter Concentration
- Year-on-Year Holding Change

## Flags

### Promoter Dilution

Example:

```text
Promoter holding decline >5 percentage points
```

### Significant Promoter Dilution

Example:

```text
Decline >15 percentage points
```

### Major Shareholder Exit

### New Major Shareholder

### Ownership Concentration

Example:

```text
Promoter holding >75%
```

Normally informational.

### Change in Control

Potential flag where:

```text
Largest shareholder changes
OR
Promoter holding falls below controlling threshold
```

### Ownership + Director Combination

If:

```text
Promoter holding ↓
+
Promoter director exits
```

generate:

> Potential ownership/control transition.

---

# 14. Section 6 – Securities Allotment

## Data Points

- Allotment Date
- Security Type
- Number of Securities
- Face Value
- Premium
- Total Amount
- Cash / Non-Cash
- Allottee
- Promoter / Non-Promoter

## Flags

- material fresh equity issuance;
- preference share issuance;
- large share premium;
- non-cash allotment;
- repeated allotments;
- allotment resulting in promoter dilution;
- securities issued shortly before major borrowing.

---

# 15. Section 7 – Financial Performance

The system shall capture at least 3 years where available.

## Data Points

- Revenue
- Other Income
- Total Income
- EBITDA
- EBIT
- PBT
- PAT
- EBITDA Margin
- EBIT Margin
- PAT Margin
- Net Worth
- Tangible Net Worth

## Derived Metrics

- YoY Revenue Growth
- Revenue CAGR
- EBITDA Growth
- PAT Growth
- Margin Change
- Net Worth Growth

## Flags

### Revenue Decline

Example:

```text
Revenue growth <0%
```

### Material Revenue Decline

Example:

```text
Revenue decline >20%
```

### Consecutive Revenue Decline

Example:

```text
Revenue declines in 2 consecutive years
```

### Negative EBITDA

### Consecutive Negative EBITDA

### EBITDA Margin Deterioration

### PAT Loss

### Consecutive PAT Losses

### Net Worth Erosion

### Negative Net Worth

---

# 16. Section 8 – Balance Sheet Analysis

## Asset Data

- Fixed Assets
- Gross Block
- Net Block
- CWIP
- Inventory
- Trade Receivables
- Cash
- Bank Balance
- Investments
- Loans & Advances
- Other Current Assets
- Total Assets

## Liability Data

- Share Capital
- Reserves
- Net Worth
- Long-Term Borrowings
- Short-Term Borrowings
- Current Maturities
- Trade Payables
- Provisions
- Other Current Liabilities
- Total Liabilities

## Flags

- fixed assets declining continuously;
- CWIP increasing without completion;
- inventory increasing while sales decline;
- receivables increasing faster than sales;
- cash declining materially;
- short-term borrowings increasing;
- trade payables increasing materially;
- net worth declining;
- liabilities growing faster than assets.

---

# 17. Section 9 – Liquidity Analysis

## Ratios

### Current Ratio

```text
Current Assets / Current Liabilities
```

### Quick Ratio

```text
(Current Assets - Inventory) / Current Liabilities
```

### Cash Ratio

```text
Cash / Current Liabilities
```

### Net Working Capital

```text
Current Assets - Current Liabilities
```

## Flags

- Current Ratio below threshold
- Quick Ratio below threshold
- declining current ratio
- negative working capital
- cash balance insufficient against short-term obligations

## Positive Flags

- current ratio materially above peer;
- improving liquidity.

---

# 18. Section 10 – Leverage & Debt Servicing

## Ratios

- Debt / Equity
- Total Debt / Net Worth
- Debt / EBITDA
- Total Liabilities / Net Worth
- Net Debt / EBITDA
- Interest Coverage
- DSCR where available
- CFO / Debt

## Flags

- increasing debt/equity;
- Debt/EBITDA above threshold;
- negative interest coverage;
- declining interest coverage;
- increasing finance cost;
- debt growing faster than earnings;
- debt increasing while net worth falls.

---

# 19. Section 11 – Working Capital Analysis

## Ratios

### Debtor Days

```text
Trade Receivables / Revenue × 365
```

### Inventory Days

```text
Inventory / COGS × 365
```

### Payable Days

```text
Trade Payables / COGS × 365
```

### Cash Conversion Cycle

```text
Debtor Days + Inventory Days - Payable Days
```

## Flags

- debtor days increasing;
- debtor days above peer median;
- inventory days increasing;
- payable days increasing;
- payable days materially above peers;
- CCC increasing;
- CCC materially above peers.

## Important Combined Rule

If debtor days and payable days are both high, but CCC remains normal, AI should mention this counterbalance.

---

# 20. Section 12 – Cash Flow Analysis

Where cash flow data is available.

## Data Points

- Cash Flow from Operations
- Cash Flow from Investing
- Cash Flow from Financing
- Capex
- Free Cash Flow

## Derived Metrics

### CFO/PAT

```text
Operating Cash Flow / PAT
```

### Free Cash Flow

```text
CFO - Capex
```

## Flags

- negative CFO;
- negative CFO for consecutive years;
- PAT positive but CFO negative;
- persistent negative free cash flow;
- negative FCF funded through debt;
- CFO declining materially.

---

# 21. Section 13 – Earnings Quality

The AI shall analyse relationships between:

- PAT;
- CFO;
- receivables;
- other income;
- exceptional items.

## Example Flag

If:

```text
PAT ↑
CFO ↓
Receivables ↑
Other Income ↑
```

generate:

> Earnings quality requires review because reported profitability is not supported by equivalent operating cash generation.

---

# 22. Section 14 – Peer Comparison

Where peer information exists, compare:

- Revenue Growth
- EBITDA Margin
- Net Margin
- ROE
- ROCE
- Debt/Equity
- Current Ratio
- Quick Ratio
- Debtor Days
- Inventory Days
- Payable Days
- CCC

## Flags

- materially below peer revenue growth;
- EBITDA margin below peer;
- negative margin while peers are positive;
- ROE/ROCE below peers;
- debtor days materially above peer;
- payable days materially above peer.

## Output

Display:

```text
Company
vs
Peer Median
vs
Variance
```

---

# 23. Section 15 – ROC Charge Summary

## Data Points

- Charge ID
- Charge Holder
- Creation Date
- Modification Date
- Satisfaction Date
- Original Charge Amount
- Current Charge Amount
- Charge Status
- Instrument
- Rate of Interest
- Repayment Terms
- Security Description
- Priority
- Filing Date

## Summary Metrics

- Open Charge Count
- Open Charge Amount
- Satisfied Charge Count
- Largest Open Charge
- Oldest Open Charge
- Latest Charge
- Total Historical Lenders
- Current Lenders

---

# 24. Section 16 – Charge Lifecycle

The system shall group all modifications under the same Charge ID.

Example:

```text
Charge ID: 100426748

2021 Creation       ₹2.00 Cr
2022 Modification   ₹5.50 Cr
2023 Modification   ₹15.00 Cr
```

## Derived Metrics

### Charge Enhancement %

```text
(Current Amount - Original Amount)
/
Original Amount × 100
```

## Flags

- charge enhancement;
- material charge enhancement;
- repeated modification;
- facility tenor extended;
- interest-rate change;
- security expanded;
- lender changed;
- charge priority changed.

## Suggested Thresholds

- <25% = informational
- 25–50% = watch
- 50–100% = high review
- >100% = material review

Thresholds must remain configurable.

---

# 25. Section 17 – Charge Satisfaction Analysis

## Data Points

- Charge ID
- Lender
- Creation Date
- Satisfaction Date
- Satisfaction Filing Date
- Tenure

## Derived Metrics

- Charge Duration
- Satisfaction Filing Delay
- Historical Satisfaction Ratio

## Flags

- very old open charge;
- charge maturity appears past but still open;
- delayed satisfaction filing;
- repeated historical filing delay.

## Positive Indicator

Multiple historical charges recorded as satisfied.

Important:

Charge satisfaction shall not be interpreted as proof of perfect repayment behaviour.

---

# 26. Section 18 – Security / Property Register

Each security mentioned within a charge shall be separately structured.

## Property / Asset Data Points

- Security ID
- Charge ID
- Lender
- Property Type
- Security Type
- Property Owner
- Address
- State
- District
- Taluka
- Village
- Survey Number
- Gat Number
- Khasra Number
- CTS Number
- Plot Number
- Flat / Unit Number
- Extent / Area
- Boundaries
- Vehicle Number / Chassis where applicable
- Asset Description
- Priority
- Charge Status

## Security Categories

- Land
- Building
- Factory
- Flat
- Office
- Plant & Machinery
- Inventory
- Book Debts
- Receivables
- Current Assets
- Fixed Deposit
- Vehicle
- Shares
- IP
- Floating Charge
- Personal Guarantee
- Corporate Guarantee

---

# 27. Section 19 – Property / Mortgage Matching

The system shall attempt to identify whether the same property appears under different charge IDs.

## Property Matching Fields

Priority should be:

1. State
2. District
3. Village
4. Survey/Gat/Khasra/CTS
5. Plot
6. Unit/Flat
7. Extent
8. Boundaries
9. Owner
10. Address similarity

## Possible Match Categories

- Exact Match
- Strong Match
- Probable Match
- Partial Match
- No Match

## Flags

### Same Property – Multiple Open Charges

### Same Property – Different Lenders

### First Charge Already Exists

### Second/Subservient Charge

### Pari Passu Charge

### Charge Priority Unclear

### Property Ownership Inconsistent

### Third-Party Mortgage

### Same Survey but Different Extent

This shall not automatically be considered duplicate property.

### Same Property with Different Description

Manual review required.

---

# 28. Section 20 – Collateral Concentration

## Derived Metrics

- Exposure by Property
- Exposure by Security Type
- Exposure by Lender

Example:

```text
Factory Property Exposure / Total Secured Charge Exposure
```

## Flags

- high collateral concentration;
- majority of secured exposure against one property;
- all major assets already encumbered;
- fixed + current assets simultaneously charged;
- broad asset encumbrance.

---

# 29. Section 21 – Lender Exposure

Aggregate all open charges by lender.

## Data Points

- Lender
- Open Charge Amount
- Open Charge Count
- Security Type
- First Association Date
- Latest Charge Date
- Historical Satisfied Charges

## Derived Metrics

### Lender Concentration

```text
Largest Lender Open Charges / Total Open Charges
```

## Flags

- high lender concentration;
- multiple new lenders within short period;
- frequent lender changes;
- refinancing pattern;
- new lender introduced during financial deterioration.

---

# 30. Section 22 – Financial Debt vs ROC Charges

The system shall reconcile:

```text
Balance Sheet Borrowings
vs
Open ROC Charges
```

## Output

- Balance Sheet Debt
- Open ROC Charges
- Difference
- ROC Charge / Debt %

## Important Rule

ROC charge amount shall never automatically be treated as current loan outstanding.

## Flags

- material unexplained difference;
- borrowings materially higher than open charges;
- secured charge exposure materially higher than reported secured debt.

AI should recommend reconciliation rather than conclude inconsistency.

---

# 31. Section 23 – MSME Analysis

## Data Points

- Reporting Period
- MSME Supplier
- Amount Due
- Payment Status
- Delay where available

## Derived Metrics

- Total MSME Amount Due
- MSME Supplier Count
- Largest Supplier Exposure
- MSME Due / Trade Payables
- MSME Due / Revenue

## Flags

- material MSME dues;
- rising MSME dues;
- repeated supplier names;
- MSME dues increasing while liquidity deteriorates;
- MSME dues + high payable days;
- MSME dues + negative CFO.

---

# 32. Section 24 – GST Analysis

## Data Points

- GSTIN
- State
- Status
- Registration Date
- Cancellation Date
- Latest GSTR-1
- Latest GSTR-3B
- Filing Status

## Flags

- GST registration cancelled;
- GST suspended;
- recent return missing;
- repeated delayed returns;
- MCA name vs GST name mismatch;
- registered office vs GST address mismatch;
- active company with no active GST registration.

## Positive Indicator

Recent filings submitted on time.

---

# 33. Section 25 – EPFO Analysis

## Data Points

- Establishment ID
- Establishment Status
- Wage Month
- Employee Count
- Contribution Amount
- Due Date
- Payment Date
- Payment Status

## Derived Metrics

- Latest Headcount
- 3M Average Headcount
- 6M Average
- 12M Average
- Headcount Change %
- Delayed Payment Count
- Average Delay
- Maximum Delay

## Flags

### Employee Count Decline

Example:

```text
Headcount decline >10%
```

### Material Workforce Reduction

Example:

```text
Decline >20%
```

### EPFO Payment Delay

### Repeated EPFO Delay

### Missing Contribution Month

### Establishment Inactive

---

# 34. Section 26 – Workforce + Business Trend

AI shall combine EPFO and financial information.

Example:

```text
Revenue ↓
+
Employee Count ↓
```

Output:

> Business contraction indicators observed.

If:

```text
Revenue ↑
+
Employees ↑
```

Output:

> Operating scale appears to be expanding.

---

# 35. Section 27 – Auditor Analysis

## Data Points

- Auditor Name
- Audit Firm
- FRN
- Opinion
- Qualification
- Reservation
- Adverse Remark
- Going Concern
- Internal Control Observation
- Statutory Dues
- Fraud Observation
- Receivable Observation
- Inventory Observation
- Related Party Observation

## Flags

High Priority:

- Qualified Opinion
- Adverse Opinion
- Disclaimer
- Going Concern
- Material Weakness
- Default Observation
- Fraud Reported
- Statutory Dues Outstanding
- Receivable Recoverability Concern

## Positive Flag

No qualification / adverse remark in recent years.

---

# 36. Section 28 – Related Party Transactions

## Data Points

- Related Party
- Relationship
- Transaction Type
- Transaction Amount
- Loans
- Advances
- Receivables
- Payables
- Guarantees
- Purchases
- Sales

## Derived Metrics

- RPT / Revenue
- Related Party Receivables / Total Receivables
- Related Party Loans / Net Worth

## Flags

- material increase in RPT;
- related-party loans increasing;
- guarantees to group entities;
- large related-party receivables;
- material RPT while business performance deteriorates.

---

# 37. Section 29 – Litigation Analysis

## Data Points

- Case Number
- Court
- Filing Date
- Parties
- Company Role
- Case Type
- Law / Provision
- Claim Amount
- Current Status
- Last Hearing
- Disposal

## Classification

- Recovery
- Commercial
- Tax
- Labour
- Insolvency
- Cheque Dishonour
- Contractual
- Criminal
- Regulatory

## Critical Rule

Cases shall be separated into:

```text
Cases Filed By Company
Cases Filed Against Company
```

## Flags

- recovery case against company;
- lender litigation;
- DRT;
- SARFAESI;
- IBC;
- cheque dishonour;
- material claim amount;
- increasing litigation count;
- multiple financial disputes.

---

# 38. Section 30 – Litigation Materiality

The system should compare claim amount against:

- Revenue
- Net Worth
- EBITDA
- Debt

Example:

```text
Case Amount / Revenue
```

## Classification

- Immaterial
- Low
- Moderate
- Material
- Highly Material

A ₹3 lakh dispute should not receive the same severity as a ₹30 crore recovery case.

---

# 39. Section 31 – Corporate Event Timeline

The system shall generate a chronological timeline combining:

- Incorporation
- Director Appointment
- Director Cessation
- Shareholder Changes
- Securities Allotment
- Charge Creation
- Charge Modification
- Charge Satisfaction
- Auditor Change
- GST Change
- Revenue Deterioration
- Legal Cases
- Major Compliance Events

Example:

```text
2003 – Company Incorporated
2021 – Kotak Charge ₹2 Cr
2022 – Charge Increased ₹5.5 Cr
2023 – Charge Increased ₹15 Cr
2024 – Director Ceased
2025 – Revenue Declined 25%
2026 – MSME Dues Reported ₹1.56 Cr
```

---

# 40. Section 32 – Change Since Previous Report

Where earlier MCA/ROC reports are available, automatically detect changes.

## Changes to Detect

- new director;
- director cessation;
- new shareholder;
- promoter dilution;
- new charge;
- charge modification;
- charge satisfaction;
- new lender;
- GST cancellation;
- new auditor;
- audit qualification;
- new litigation;
- revenue deterioration;
- new MSME dues;
- employee decline;
- company-status change.

## Output

```text
SINCE PREVIOUS REVIEW

Revenue       ↓ 25%
Director      1 cessation
New Charge    ₹20 Lakh
MSME Due      ₹1.56 Cr
GST           Filing Regular
Auditor       No New Qualification
```

---

# 41. Section 33 – Cross-Section AI Analysis

This shall be the primary AI intelligence layer.

The engine should combine independent flags to identify broader themes.

---

## 41.1 Business Contraction

Trigger combination:

```text
Revenue ↓
+
EBITDA ↓
+
Employee Count ↓
+
Fixed Assets ↓
```

Output:

> Business contraction indicators identified.

---

## 41.2 Liquidity Pressure

Trigger combination:

```text
Payable Days ↑
+
MSME Dues ↑
+
EPFO Delays
+
Cash ↓
```

Output:

> Potential liquidity/payment pressure requires review.

---

## 41.3 Working Capital Stress

Trigger:

```text
Debtor Days ↑
+
Inventory Days ↑
+
Working Capital Borrowing ↑
```

Output:

> Increasing dependence on working-capital financing.

---

## 41.4 Debt Service Deterioration

Trigger:

```text
Debt/EBITDA ↑
+
Interest Coverage ↓
+
EBITDA Margin ↓
```

Output:

> Debt-service capacity is weakening.

---

## 41.5 Potential Governance Transition

Trigger:

```text
Promoter Holding ↓
+
Promoter Director Exit
+
Auditor Change
+
New Borrowing
```

Output:

> Material ownership/governance transition requires enhanced review.

---

## 41.6 Collateral Priority Risk

Trigger:

```text
Same Property
+
Multiple Open Charges
+
Different Lenders
+
Priority Unclear
```

Output:

> Collateral priority review required.

---

# 42. Section 34 – Positive Indicators

The system must separately identify positive signals.

Examples:

- Active / Compliant company;
- stable promoter holding;
- long-serving directors;
- no auditor qualification;
- timely GST filing;
- improving current ratio;
- declining debt;
- positive CFO;
- improving EBITDA;
- charge satisfaction;
- no material litigation;
- no significant director churn.

This section prevents the report from becoming unnecessarily negative.

---

# 43. Section 35 – Counter-Signal Logic

AI should identify situations where one adverse indicator is offset by another.

Example:

```text
Debtor Days High
+
Payable Days High
+
CCC Normal
```

Output:

> Although collection and payment cycles are individually elevated, the overall cash conversion cycle remains broadly in line with peers.

Another example:

```text
MSME Dues High
+
Current Ratio Strong
+
Cash Balance High
```

Output:

> Supplier-payment indicators warrant review despite strong reported accounting liquidity.

This logic is critical to avoid false or exaggerated red flags.

---

# 44. Section 36 – AI Finding Format

Every AI finding must follow the same structure.

## Mandatory Fields

### Flag Name

Example:

```text
Material Revenue Decline
```

### Category

Example:

```text
Financial Performance
```

### Severity

```text
Critical / High / Watch / Information / Positive
```

### Current / Historical

Example:

```text
Current
```

### Evidence

Example:

```text
FY24 Revenue = ₹48.62 Cr
FY25 Revenue = ₹36.46 Cr
```

### Derived Value

```text
YoY Change = -25%
```

### Trend

```text
Declining
```

### Why It Matters

Short lender-oriented explanation.

### Counter-Indicators

Where applicable.

### Recommended Review

Example:

> Obtain management explanation and latest provisional financials.

---

# 45. Section 37 – Manual Review Queue

The report should generate a final list of analyst actions.

## Priority 1

High-impact items.

Example:

- review revenue contraction;
- verify debt-service ability;
- review open security position;
- verify auditor concern.

## Priority 2

Moderate issues.

Example:

- obtain receivable ageing;
- review MSME dues;
- verify director changes.

## Priority 3

Informational verification.

Example:

- confirm cancelled GST registration;
- verify related-company association.

---

# 46. Section 38 – Report Layout

The recommended output sequence is:

```text
1. Executive Dashboard
2. AI Executive Summary
3. Risk Matrix
4. Changes Since Last Report
5. Company Profile
6. Management & Directors
7. Director Network
8. Ownership & Shareholding
9. Financial Performance
10. Balance Sheet
11. Financial Ratios
12. Working Capital
13. Cash Flow
14. Peer Comparison
15. ROC Charge Summary
16. Charge Lifecycle
17. Lender Exposure
18. Security / Property Register
19. Property Encumbrance Analysis
20. MSME
21. GST
22. EPFO
23. Auditor Analysis
24. Related Parties
25. Litigation
26. Corporate Timeline
27. Cross-Section AI Findings
28. Positive Indicators
29. Manual Review Checklist
30. Source / Disclaimer
```

---

# 47. Dashboard Navigation

Recommended portal menu:

```text
OVERVIEW
 ├─ Executive Summary
 ├─ Risk Matrix
 └─ Changes Since Last Review

CORPORATE
 ├─ Company Profile
 ├─ Directors
 ├─ Director Network
 ├─ Ownership
 └─ Corporate Timeline

FINANCIAL
 ├─ Financial Performance
 ├─ Balance Sheet
 ├─ Ratios
 ├─ Cash Flow
 ├─ Working Capital
 └─ Peer Comparison

BORROWING & SECURITY
 ├─ Charges
 ├─ Charge Lifecycle
 ├─ Lender Exposure
 ├─ Property Register
 └─ Encumbrance Analysis

COMPLIANCE
 ├─ MCA
 ├─ Auditor
 ├─ GST
 ├─ EPFO
 └─ MSME

OTHER RISKS
 ├─ Related Parties
 ├─ Litigation
 └─ Connected Entities

AI ANALYSIS
 ├─ Critical Flags
 ├─ Review Flags
 ├─ Positive Indicators
 ├─ Cross-Section Findings
 └─ Manual Review
```

---

# 48. Data Confidence

Each field should retain:

- extracted value;
- source section;
- source page/sheet;
- extraction confidence;
- validation status.

Example:

```text
Field: Revenue FY25
Value: ₹36.46 Cr
Source: Financial Statements
Confidence: High
Validation: Confirmed
```

For AI-derived values:

```text
Field: Business Contraction
Type: AI Derived
Confidence: Medium
Supporting Signals:
- Revenue decline
- EBITDA decline
- Employee decline
```

---

# 49. Source Traceability

Every flag must provide evidence traceability.

Example:

```text
Flag:
Material Charge Enhancement

Source:
Charge Report
Charge ID: 100426748

Evidence:
Creation Amount: ₹2 Cr
Latest Amount: ₹15 Cr
```

Users should be able to click **View Source** from the flag.

---

# 50. AI Guardrails

The AI shall not:

- state that a company defaulted solely because a charge remains open;
- assume ROC charge amount equals current loan outstanding;
- treat historical director association as current association;
- treat all director cessations as adverse;
- treat high promoter holding as automatically negative;
- treat GST cancellation as adverse without checking other active registrations;
- treat cases filed by the company as cases against the borrower;
- classify similar survey numbers as the same property without checking extent and other identifiers;
- state that a title defect exists solely due to property-description mismatch;
- state that CIRP of a connected entity means the borrower is under CIRP;
- automatically issue Approve / Reject recommendations.

---

# 51. Threshold Configuration

The following thresholds should be configurable:

```text
Revenue Decline %
EBITDA Margin
PAT Loss Years
Current Ratio
Quick Ratio
Debt / Equity
Debt / EBITDA
Interest Coverage
Debtor Days
Inventory Days
Payable Days
Promoter Dilution %
Director Exit Count
Board Turnover %
Charge Enhancement %
Charge Filing Delay
MSME Materiality
Litigation Materiality
EPFO Delay Count
Employee Count Decline %
```

---

# 52. Example Flag Object

The backend may store each finding in a standard structure.

```json
{
  "flag_code": "FIN_REV_DECLINE",
  "flag_name": "Material Revenue Decline",
  "category": "Financial Performance",
  "severity": "Critical",
  "status": "Current",
  "period": "FY2025",
  "current_value": 36.46,
  "previous_value": 48.62,
  "change_percentage": -25.0,
  "unit": "INR Crore",
  "evidence": [
    "FY2024 Revenue: ₹48.62 Cr",
    "FY2025 Revenue: ₹36.46 Cr"
  ],
  "why_it_matters": "Material decline in operating scale.",
  "manual_review": true
}
```

---

# 53. Example Property Flag Object

```json
{
  "flag_code": "SEC_MULTIPLE_CHARGE",
  "flag_name": "Same Property Appears in Multiple Open Charges",
  "category": "Property / Security",
  "severity": "Critical",
  "property_id": "PROP-001",
  "charge_ids": [
    "100001",
    "100245"
  ],
  "lenders": [
    "Bank A",
    "Bank B"
  ],
  "match_type": "Strong Match",
  "matched_fields": [
    "Village",
    "Survey Number",
    "Plot Number",
    "Extent"
  ],
  "manual_review": true
}
```

---

# 54. Report Download

The system should support:

- HTML
- PDF
- Excel data extraction
- JSON API output

The report shall preserve:

- sections;
- severity colours;
- tables;
- trend charts;
- source references;
- AI findings.

---

# 55. Final Report Disclaimer

The report shall contain the following type of disclaimer:

> This report is an analytical interpretation of information extracted from the supplied MCA/ROC and related statutory records. AI-generated observations are intended to assist credit and risk review and should be verified against original MCA filings, audited financial statements, lender statements, security documents and other primary records before making any lending, investment or legal decision.

---

# 56. Success Criteria

The solution will be considered successful where:

1. all major report sections are automatically identified;
2. key data is extracted and normalised;
3. multi-year trends are calculated correctly;
4. director appointments and cessations are identified;
5. promoter/shareholder changes are identified;
6. financial red flags are correctly calculated;
7. peer comparison is generated where data exists;
8. charge lifecycle is consolidated by Charge ID;
9. charge modifications are not double-counted as separate debt;
10. security/property descriptions are structured;
11. same-property charge matches can be identified;
12. historical and current events are clearly separated;
13. compliance indicators are analysed;
14. cross-section AI findings are evidence-based;
15. positive indicators are also displayed;
16. every finding includes supporting evidence;
17. manual review items are generated;
18. the output can be understood by a BFSI analyst without reading the complete raw MCA report.

---

# 57. Recommended Product Principle

The portal should follow:

```text
RAW MCA DATA
      ↓
STRUCTURED CORPORATE DATA
      ↓
TRENDS & CALCULATIONS
      ↓
RED FLAGS + POSITIVE SIGNALS
      ↓
CROSS-SECTION INTELLIGENCE
      ↓
LENDER REVIEW ACTIONS
```

The product should not behave like an MCA report summariser.

It should behave like a **Corporate Credit Intelligence Engine** that tells the lender:

> What happened, what changed, why it matters, what evidence supports it, what contradicts it, and what should be checked next.