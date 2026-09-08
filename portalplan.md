Yes. Based on your workflow, I would keep the portal intentionally simple: **request creation → Excel ingestion → AI processing → review → downloadable PDF report**.

The portal should feel more like an internal BFSI intelligence workbench than a large CRM.

# MCA ROC AI Analysis Portal

## 1. Overall Modules

I would keep the main navigation to these modules:

```text
Dashboard
New Search
Search History
Clients
```

And optionally later:

```text
Administration
Configuration
Audit Logs
```

For Phase 1, the first four modules are enough.

---

# 2. High-Level User Workflow

```text
Login
  ↓
Dashboard
  ↓
New Search
  ↓
Select Client
  ↓
Enter Company / LLP Details
  ↓
Create Request
  ↓
Upload MCA ROC Excel
  ↓
Upload Charge Excel
  ↓
Validate Files
  ↓
Extract & Normalize Data
  ↓
Store Request + Extracted Data
  ↓
Run Rule Engine
  ↓
Run Financial Calculations
  ↓
Run Trend Analysis
  ↓
Run AI Cross-Section Analysis
  ↓
Generate AI Analysis Report
  ↓
User Reviews Report
  ↓
Download PDF
```

---

# 3. Dashboard

The Dashboard should answer:

> How many searches are coming in, what is pending, what has failed, and what requires attention?

I would avoid putting financial analytics on the home dashboard because those belong inside an individual company analysis.

## Dashboard Layout

```text
┌───────────────────────────────────────────────────────────────┐
│ MCA ROC AI ANALYSIS                                          │
│ Corporate Credit Intelligence                               │
├───────────────────────────────────────────────────────────────┤
│ Dashboard | New Search | Search History | Clients            │
└───────────────────────────────────────────────────────────────┘


TODAY / CURRENT WORKLOAD

┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
│ Total       │ │ Processing  │ │ Completed   │ │ Failed /    │
│ Requests    │ │             │ │             │ │ Review      │
│ 1,284       │ │ 12          │ │ 1,248       │ │ 24          │
└─────────────┘ └─────────────┘ └─────────────┘ └─────────────┘


RECENT REQUESTS

Request ID | Client | Company | CIN/PAN | Created | Status | Action
-------------------------------------------------------------------
REQ-1025   | HDFC   | ABC Ltd | U123... | 18:30   | Done   | View
REQ-1024   | Kotak  | XYZ LLP | AAA...  | 18:21   | AI Run | View
REQ-1023   | AU Bank| PQR Ltd | U984... | 17:55   | Failed | Retry


PROCESSING STATUS

Excel Uploaded          8
Extraction Running      2
AI Analysis Running     2
Manual Review Required  4
```

---

# 4. Dashboard Cards

## Total Requests

Show:

* Total requests
* Today
* This month

---

## Processing

Requests currently at:

* File Upload
* Extraction
* Validation
* AI Analysis
* PDF Generation

---

## Completed

Number successfully analysed.

---

## Manual Review / Failed

Include:

* Extraction failed
* Missing mandatory section
* AI analysis failed
* File format issue
* Data conflict
* User review required

Clicking a card should open **Search History filtered by that status**.

---

# 5. Optional Dashboard Analytics

Useful Phase 2 widgets:

### Requests by Client

```text
HDFC       480
Kotak      310
AU Bank    184
Others     310
```

### Requests by Month

Simple trend chart.

### Average Processing Time

```text
Upload → Analysis Complete
Average: 3m 42s
```

### Failure Reasons

```text
Invalid Excel               34%
Missing Charge Data         26%
Unrecognised Format         18%
Missing Mandatory Fields    12%
AI Processing Error         10%
```

---

# 6. New Search Module

This is the main request creation screen.

The screen should be simple.

## New Search Form

### Client

**Client Name***
Dropdown

Example:

```text
Select Client ▼

HDFC Bank
Kotak Mahindra Bank
AU Small Finance Bank
Federal Bank
...
```

Clients should come from the **Clients Master**.

---

### Entity Details

**Entity Type***

```text
○ Company
○ LLP
```

---

### Company / LLP Name*

Text box.

```text
Vedic Cosmeceuticals Private Limited
```

---

### Identification

Instead of creating separate mandatory fields for every identifier, have:

**CIN / LLPIN**

and

**PAN**

At least one should be mandatory.

Example:

```text
CIN / LLPIN:
U24246DL2003PTC118255

PAN:
AABCV6369H
```

---

# 7. Suggested New Search Form

```text
NEW MCA ROC SEARCH

Client Name *
[ HDFC Bank                         ▼ ]

Entity Type *
(•) Company       ( ) LLP

Company / LLP Name *
[ Vedic Cosmeceuticals Private Limited             ]

CIN / LLPIN
[ U24246DL2003PTC118255                             ]

PAN
[ AABCV6369H                                        ]

----------------------------------------------------

DOCUMENT UPLOAD

MCA / ROC Report *
[ Upload Excel ]

Detailed Charge Report
[ Upload Excel ]

Additional Financial Document
[ Upload Excel ]      Optional

----------------------------------------------------

[ Cancel ]                         [ Create & Analyse ]
```

---

# 8. Identifier Validation

When Entity Type = Company:

Validate CIN format if provided.

When Entity Type = LLP:

Validate LLPIN format.

PAN:

Basic PAN format validation.

However, don't make the workflow overly strict because external source data can sometimes contain formatting differences.

---

# 9. Request Creation

Once user clicks:

**Create & Analyse**

the system should create the request immediately.

Example:

```text
Request ID:
MCA-20260908-000125
```

The request should be stored before analysis starts.

This is important because if processing fails later, the request history still exists.

---

# 10. Request Status Flow

I recommend the following statuses:

```text
Created
↓
Documents Uploaded
↓
Validating
↓
Extraction In Progress
↓
Data Extracted
↓
AI Analysis In Progress
↓
Analysis Completed
↓
PDF Generated
```

Exception statuses:

```text
Validation Failed
Extraction Failed
AI Analysis Failed
Manual Review Required
Cancelled
```

---

# 11. Excel Data Ingestion Workflow

Because you will provide the data through Excel, this pipeline is critical.

I would not send the entire Excel directly to the LLM and ask it to analyse.

Instead:

```text
Excel
 ↓
Excel Parser
 ↓
Sheet Identification
 ↓
Section Mapping
 ↓
Structured Data
 ↓
Validation
 ↓
Database
 ↓
Calculation Engine
 ↓
Rule Engine
 ↓
AI
```

This provides control and repeatability.

---

# 12. Step 1 – File Upload

Store:

* Original filename
* Document type
* File size
* Upload timestamp
* Uploaded by
* Request ID
* File hash
* Storage path

Example:

```text
Request:
MCA-20260908-000125

Documents:
Vedic Cosmeceuticals roc.xlsx
Vedic Cosmeceuticals-charge.xlsx
```

---

# 13. Step 2 – File Type Identification

System should determine whether file is:

```text
MCA ROC Main Report
Detailed Charge Report
Financial Report
Unknown
```

Detection can use:

* worksheet names
* known column names
* file naming
* internal markers.

---

# 14. Step 3 – Worksheet Detection

Your Excel contains many sections.

The ingestion service should map worksheet names to standard internal sections.

Example:

```text
Excel Sheet
            ↓
Internal Module

Company Master
            ↓
company_profile

Directors
            ↓
directors

Other Directorships
            ↓
director_network

Standalone Financial Data
            ↓
financials

Annexure - Financial Parameters
            ↓
financial_ratios

Charges
            ↓
charges

MSME
            ↓
msme

GST
            ↓
gst

EPFO
            ↓
epfo

Auditors Comments
            ↓
auditor_analysis
```

---

# 15. Step 4 – Data Normalisation

Raw Excel values should be converted into standard formats.

### Dates

```text
17/06/2023
17-Jun-23
2023-06-17
```

become:

```text
2023-06-17
```

### Amount

```text
15 Crore
₹15 Cr
150000000
```

become internally:

```text
150000000
```

Display:

```text
₹15.00 Cr
```

### Percentages

```text
99.93
99.93%
0.9993
```

normalized.

---

# 16. Step 5 – Data Validation

The system should perform validations before AI analysis.

Example:

### Company Validation

```text
Uploaded Company Name
vs
Excel Company Name
```

### CIN Validation

```text
Request CIN
vs
Excel CIN
```

### PAN Validation

```text
Request PAN
vs
Excel PAN
```

If mismatch:

```text
MANUAL REVIEW REQUIRED
```

Do not automatically analyse a potentially wrong company.

---

# 17. Cross-File Validation

If both ROC report and charge report are uploaded:

Verify:

```text
Company Name
CIN
PAN
```

match.

If:

```text
Main Report CIN ≠ Charge Report CIN
```

flag:

> Uploaded documents appear to belong to different entities.

Severity:

**Critical validation error**

---

# 18. SQL Server Express 2025 Storage

I would separate the database logically into four areas:

```text
MASTER DATA
REQUEST DATA
EXTRACTED DATA
ANALYSIS DATA
```

---

# 19. Core Request Table

## MCA_Request

Suggested fields:

```text
RequestId
RequestNumber

ClientId

EntityType
CompanyName
CIN
LLPIN
PAN

RequestStatus

CreatedBy
CreatedDate

UpdatedBy
UpdatedDate

AnalysisStartedDate
AnalysisCompletedDate

IsManualReviewRequired

FailureReason
```

---

# 20. Client Master

## Client

```text
ClientId
ClientCode
ClientName

ContactPerson
ContactEmail

IsActive

CreatedDate
UpdatedDate
```

Keep the client module simple initially.

---

# 21. Document Table

## RequestDocument

```text
DocumentId
RequestId

DocumentType

OriginalFileName
StoredFileName
StoragePath

FileSize
FileHash

UploadStatus

UploadedBy
UploadedDate
```

DocumentType examples:

```text
MCA_ROC_REPORT
CHARGE_REPORT
FINANCIAL_REPORT
OTHER
```

---

# 22. Extracted Company Table

Example:

## CompanyProfile

```text
RequestId

CompanyName
CIN
PAN
LLPIN

CompanyStatus
ComplianceStatus

IncorporationDate
RegisteredAddress

AuthorisedCapital
PaidUpCapital

Industry
BusinessActivity
```

---

# 23. Director Tables

## Director

```text
DirectorId
RequestId

DIN
DirectorName
Designation

AppointmentDate
CessationDate

DirectorStatus
DINStatus
KYCStatus

IsPromoter
```

---

## DirectorAssociation

```text
AssociationId
DirectorId

ConnectedCompany
ConnectedCIN

AppointmentDate
CessationDate

EntityStatus
AssociationType
```

---

# 24. Shareholding Table

## Shareholding

```text
ShareholdingId
RequestId

FinancialYear

ShareholderName
ShareholderType

SharesHeld
HoldingPercentage

IsPromoter
```

---

# 25. Financial Table

I recommend storing annual figures row-wise rather than creating one column for each year.

## FinancialYearData

```text
FinancialId
RequestId

FinancialYear

Revenue
OtherIncome

EBITDA
EBIT
PBT
PAT

NetWorth

CurrentAssets
CurrentLiabilities

Inventory
TradeReceivables
CashAndBank

TradePayables

LongTermBorrowings
ShortTermBorrowings
TotalDebt

FinanceCost

CFO
CFI
CFF
```

This makes future years easy to add.

---

# 26. Financial Ratios Table

## FinancialRatio

```text
RatioId
RequestId
FinancialYear

RatioCode
RatioName

Value

PeerMedian
PeerAverage

VarianceFromPeer
```

Example RatioCode:

```text
CURRENT_RATIO
QUICK_RATIO
DEBT_EQUITY
INTEREST_COVERAGE
DEBTOR_DAYS
PAYABLE_DAYS
CCC
ROE
ROCE
```

---

# 27. Charge Table

## ROCCharge

```text
ChargeId
RequestId

ROCChargeNumber

ChargeHolder

CreationDate
CurrentAmount

ChargeStatus

InstrumentType
InterestRate
RepaymentTerms

Priority

LatestModificationDate

SatisfactionDate
```

---

# 28. Charge Event Table

This is critical.

Don't overwrite modifications.

## ROCChargeEvent

```text
ChargeEventId
ChargeId

EventType

EventDate
FilingDate

ChargeAmount

SecurityDescription
InterestRate
RepaymentTerms

SourceRow
```

EventType:

```text
Creation
Modification
Satisfaction
```

This allows:

```text
₹2 Cr
→ ₹5.5 Cr
→ ₹15 Cr
```

to be reconstructed.

---

# 29. Security / Property Table

## ChargeSecurity

```text
SecurityId
ChargeId

SecurityType
AssetCategory

OwnerName

PropertyAddress

State
District
Taluka
Village

SurveyNumber
GatNumber
KhasraNumber
CTSNumber

PlotNumber
UnitNumber

Area
AreaUnit

Boundaries

VehicleNumber
ChassisNumber

ChargePriority
```

---

# 30. MSME Table

## MSMEPayment

```text
MSMEId
RequestId

ReportingPeriod

SupplierName
AmountDue

PaymentStatus
DelayDays
```

---

# 31. GST Table

## GSTRegistration

```text
GSTId
RequestId

GSTIN
State

Status

RegistrationDate
CancellationDate
```

## GSTFiling

```text
GSTFilingId
GSTId

ReturnType
TaxPeriod
DueDate
FilingDate

FilingStatus
DelayDays
```

---

# 32. EPFO Table

## EPFOContribution

```text
EPFOId
RequestId

EstablishmentId

WageMonth

EmployeeCount
ContributionAmount

DueDate
PaymentDate

PaymentStatus
DelayDays
```

---

# 33. Auditor Table

## AuditorObservation

```text
ObservationId
RequestId

FinancialYear

AuditorName

OpinionType

ObservationCategory
ObservationText

Severity
```

---

# 34. Litigation Table

## Litigation

```text
LitigationId
RequestId

CaseNumber
Court

CompanyRole

CaseType

FilingDate
CurrentStatus

CounterParty

ClaimAmount

LastHearingDate

IsProbableMatch
```

---

# 35. AI Finding Table

This becomes one of the main tables.

## AnalysisFinding

```text
FindingId
RequestId

FindingCode
FindingName

Category

Severity

FindingStatus

CurrentHistorical

Observation

Evidence

CurrentValue
PreviousValue

ChangePercentage

WhyItMatters

RecommendedReview

Confidence

SourceSection

ManualReviewRequired
```

---

# 36. Search History Module

This should be the main working queue.

## Search History Listing

```text
SEARCH HISTORY

Search [________________________]

Client [All ▼]
Status [All ▼]
Date From [ ] To [ ]

------------------------------------------------------------------

Request ID | Client | Company | CIN/PAN | Created | Status | Action
------------------------------------------------------------------
MCA-00125  | HDFC   | Vedic...| U242... | 08 Sep  | Completed | View
MCA-00124  | Kotak  | ABC Ltd | AAAC... | 08 Sep  | AI Running| View
MCA-00123  | HDFC   | XYZ LLP | AAA...  | 08 Sep  | Review    | View
```

---

# 37. Search Filters

Allow filter by:

* Request ID
* Company Name
* CIN
* LLPIN
* PAN
* Client
* Status
* Date Range

---

# 38. Search Status Badges

Use statuses such as:

```text
Created
Uploaded
Processing
AI Analysis
Completed
Manual Review
Failed
```

Example:

🟢 Completed
🔵 Processing
🟠 Review Required
🔴 Failed

---

# 39. View Request Page

When user clicks **View**, open:

```text
Request Details
```

This should be a separate full page.

Header:

```text
VEDIC COSMECEUTICALS PRIVATE LIMITED

Request ID: MCA-20260908-000125
Client: HDFC Bank
CIN: U24246DL2003PTC118255
PAN: AABCV6369H

Status: Analysis Completed
```

---

# 40. Request Detail Navigation

Use tabs:

```text
Overview
AI Analysis
Uploaded Documents
Processing Log
```

For Phase 1, you really need only:

```text
AI Analysis
Uploaded Documents
```

But Overview is useful.

---

# 41. Request Overview

Show:

### Request Information

* Request ID
* Client
* Company
* CIN
* PAN
* Entity Type
* Created By
* Created Date
* Status

### Document Status

```text
MCA ROC Report       Uploaded ✓
Charge Report        Uploaded ✓
```

### Processing

```text
Extraction           Completed
Validation           Completed
Rule Engine          Completed
AI Analysis          Completed
PDF Generation       Completed
```

---

# 42. AI Analysis Tab

This should embed the report structure we already designed.

Top navigation:

```text
Executive Summary
Corporate
Financial
Charges & Security
Compliance
Litigation
AI Findings
```

---

# 43. AI Analysis Header

Example:

```text
Corporate Credit Intelligence

Overall Review
HIGH ATTENTION

Critical Findings     3
Review Findings       5
Watch Items           2
Positive Indicators   7
```

Avoid labelling the entity itself as "High Risk" unless your clients explicitly want such a score.

Better:

```text
Review Priority: High
```

---

# 44. AI Analysis Sections

Inside the same page:

### Executive Summary

### Risk Matrix

### Company Profile

### Management & Directors

### Director Network

### Ownership

### Financial Performance

### Financial Ratios

### Working Capital

### Cash Flow

### Peer Comparison

### ROC Charges

### Charge Lifecycle

### Security / Property

### MSME

### GST

### EPFO

### Auditor

### Litigation

### Corporate Timeline

### Cross-Section Findings

### Manual Review

---

# 45. Finding UI

Each AI finding should appear as a card.

Example:

```text
🔴 MATERIAL REVENUE DECLINE

FY24 Revenue       ₹48.62 Cr
FY25 Revenue       ₹36.46 Cr

Change             -25%

Revenue declined for the second consecutive year.

Why this matters
Material reduction in business scale may negatively affect
cash generation and debt-service capacity.

Supporting Signals
• EBITDA Margin: -3.3%
• PAT Margin: -2.0%
• Employee headcount declining

Recommended Review
Obtain management explanation and latest provisional financials.

[ View Source ]
```

This is far better than displaying one long AI paragraph.

---

# 46. Uploaded Documents Tab

Display:

```text
UPLOADED DOCUMENTS

File                              Type                Uploaded
-----------------------------------------------------------------
Vedic Cosmeceuticals roc.xlsx     MCA ROC Report      08 Sep 2026
Vedic ... charge.xlsx             Charge Report       08 Sep 2026

[ View ] [ Download ]
```

Also show:

```text
File Status: Processed Successfully
```

---

# 47. Replace / Re-upload Document

Useful operationally.

Allow:

```text
Replace File
```

But require user confirmation.

After replacement:

```text
Re-run Analysis
```

Do not automatically overwrite completed analysis without version tracking.

---

# 48. Analysis Versioning

Very important.

If report is regenerated:

```text
Analysis Version 1
08 Sep 2026 18:50

Analysis Version 2
08 Sep 2026 19:12
```

Store:

```text
AnalysisVersion
PromptVersion
RuleVersion
GeneratedDate
GeneratedBy
```

This gives auditability.

---

# 49. PDF Download

At the top of the AI Analysis page:

```text
[ Download PDF ]
```

PDF should contain:

1. Executive Summary
2. Risk Matrix
3. Company Profile
4. Directors
5. Ownership
6. Financial Analysis
7. Charges & Security
8. Compliance
9. Litigation
10. Cross-Section AI Findings
11. Manual Review Checklist
12. Disclaimer

---

# 50. PDF File Naming

Recommended:

```text
MCA_ROC_AI_Analysis_
<Vendor/Company>_
<CIN>_
<YYYYMMDD>.pdf
```

Example:

```text
MCA_ROC_AI_Analysis_Vedic_Cosmeceuticals_U24246DL2003PTC118255_20260908.pdf
```

---

# 51. Clients Module

Simple client listing.

```text
CLIENTS

Client Code | Client Name | Active | Requests | Action
--------------------------------------------------------
HDFC        | HDFC Bank   | Yes    | 240      | View/Edit
KOTAK       | Kotak Bank  | Yes    | 182      | View/Edit
```

---

# 52. Add Client

Form:

```text
Client Name *
Client Code *

Contact Person
Contact Email

Status
Active / Inactive
```

Only active clients appear on New Search.

---

# 53. Client Detail

Show:

* Client Name
* Code
* Contact
* Total Requests
* Completed
* Failed
* Last Request Date

Optional future feature:

Client-specific risk thresholds.

Example:

```text
HDFC:
Material Revenue Decline = 20%

Kotak:
Material Revenue Decline = 25%
```

---

# 54. Processing Architecture

I would design the backend pipeline like this:

```text
                    ┌─────────────────────┐
                    │      Web Portal     │
                    └─────────┬───────────┘
                              │
                              ▼
                    ┌─────────────────────┐
                    │ Request Management  │
                    └─────────┬───────────┘
                              │
                 SQL Server Express 2025
                              │
                              ▼
                    ┌─────────────────────┐
                    │ Document Ingestion  │
                    └─────────┬───────────┘
                              │
                              ▼
                    ┌─────────────────────┐
                    │ Excel Parser        │
                    └─────────┬───────────┘
                              │
                              ▼
                    ┌─────────────────────┐
                    │ Normalisation       │
                    └─────────┬───────────┘
                              │
                              ▼
                    ┌─────────────────────┐
                    │ Validation Engine   │
                    └─────────┬───────────┘
                              │
              ┌───────────────┼────────────────┐
              ▼               ▼                ▼
      Financial Engine    Charge Engine   Compliance Engine
              │               │                │
              └───────────────┼────────────────┘
                              ▼
                    ┌─────────────────────┐
                    │ Rule / Flag Engine  │
                    └─────────┬───────────┘
                              │
                              ▼
                    ┌─────────────────────┐
                    │ AI Analysis Engine  │
                    └─────────┬───────────┘
                              │
                              ▼
                    ┌─────────────────────┐
                    │ Report Generator    │
                    └─────────┬───────────┘
                              │
                        HTML + PDF
```

---

# 55. Important Technical Principle

I strongly recommend **not making AI responsible for calculations**.

For example:

Don't ask AI:

> Calculate debtor days from this Excel.

Instead backend calculates:

```text
Debtor Days = 95
Peer Median = 55
Variance = +40 days
```

Then AI receives:

```text
Debtor Days: 95
Peer Median: 55
Trend: 82 → 93 → 95
```

and generates the interpretation.

Use AI for:

* interpretation;
* correlation;
* explanation;
* summarisation;
* manual-review recommendation.

Use backend rules for:

* calculation;
* matching;
* thresholds;
* dates;
* counts;
* percentages.

This will make your portal much more reliable.

---

# 56. Recommended Processing Services

Conceptually:

```text
RequestService
ClientService
DocumentService

ExcelIngestionService
DataNormalizationService
ValidationService

FinancialAnalysisService
DirectorAnalysisService
OwnershipAnalysisService
ChargeAnalysisService
PropertyMatchingService
GSTAnalysisService
EPFOAnalysisService
MSMEAnalysisService
LitigationAnalysisService

RuleEngineService
AIAnalysisService

ReportService
PdfService
```

Even if you eventually put all of these inside one application, maintain the separation logically.

---

# 57. Recommended Phase 1

Don't build everything at once.

I would implement Phase 1 as:

### Modules

* Dashboard
* New Search
* Search History
* Clients

### Ingestion

* Main ROC Excel
* Charge Excel

### Analysis

* Company Profile
* Directors
* Director Network
* Shareholding
* Financials
* Ratios
* Peer Comparison
* Charges
* Property/Security
* MSME
* GST
* EPFO
* Auditor
* Litigation
* Cross-Section AI

### Output

* Web report
* PDF report

This already gives you a very capable product.

---

# 58. Complete User Journey

The final user journey should look like this:

```text
USER LOGIN
    │
    ▼
DASHBOARD
    │
    ├──────────► SEARCH HISTORY
    │
    ├──────────► CLIENTS
    │
    ▼
NEW SEARCH
    │
    ▼
SELECT CLIENT
    │
    ▼
ENTER COMPANY DETAILS
    │
    ▼
UPLOAD ROC EXCEL
UPLOAD CHARGE EXCEL
    │
    ▼
CREATE REQUEST
    │
    ▼
REQUEST ID GENERATED
    │
    ▼
VALIDATE COMPANY/CIN/PAN
    │
    ├── Mismatch ──► MANUAL REVIEW
    │
    ▼
EXTRACT EXCEL DATA
    │
    ▼
NORMALIZE DATA
    │
    ▼
STORE STRUCTURED DATA IN SQL SERVER
    │
    ▼
RUN FINANCIAL CALCULATIONS
    │
    ▼
RUN DIRECTOR/OWNERSHIP RULES
    │
    ▼
RUN CHARGE & PROPERTY RULES
    │
    ▼
RUN COMPLIANCE RULES
    │
    ▼
GENERATE FLAGS
    │
    ▼
AI CROSS-SECTION ANALYSIS
    │
    ▼
GENERATE ANALYSIS REPORT
    │
    ▼
REQUEST STATUS = COMPLETED
    │
    ▼
SEARCH HISTORY
    │
    ▼
VIEW
    │
    ├── AI ANALYSIS
    │
    ├── UPLOADED DOCUMENTS
    │
    └── REQUEST DETAILS
    │
    ▼
DOWNLOAD PDF
```

## One change I would make to your original idea

Your modules of **New Search, Search History and Clients** are correct. I would only add **Dashboard** as the landing module because once the number of requests grows, users need an operational overview.

And inside `Search History → View`, I would use three tabs:

```text
Overview | AI Analysis | Uploaded Documents
```

rather than directly opening the report. This gives you space later for status, errors, re-processing, version history and audit information without redesigning the page.

This is a clean Phase-1 product architecture and will also fit well with **SQL Server Express 2025 + .NET application + Excel ingestion + AI analysis + PDF generation**.
