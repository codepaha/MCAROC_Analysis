# Reference Tool Unlock, Refresh, and Lifecycle Contract

**Specification & Reverse-Engineered Contract for Pipeline Automation (PR R / §5.7)**  
*Confidential — Internal Documentation. All credentials, personal names, emails, user IDs, tokens, session cookies, and vendor identifiers are redacted.*

---

## Executive Summary & Action Contract

| Stage in Pipeline | Vendor Service Path | HTTP Method | Action Name | Key Parameters | Success Criterion / Contract Shape | Cost |
|---|---|---|---|---|---|---|
| **Lock Status Check** | `server/user/service.php` | `GET` | `getAssetTeams` | `bid` | `teams[0].addedAt != null` ⇒ Unlocked<br>`teams[0].addedAt == null` ⇒ Locked / Expired | 0 credits |
| **Free Identity Preview** | `server/common/api/service.php` | `GET` | `getCompanyPreview` | `bid` | `about_company.summary.cin == cin` (verifies pre-unlock identity) | 0 credits |
| **Pre-Unlock Check** | `server/common/mcastatus/service.php` | `GET` | `getUpgradeStatusForUnlockingAsset` | None | `mca_status == "NORMAL"` | 0 credits |
| **Billed Unlock** | `server/user/service.php` | `POST` | `addAsset` | `teamId`, `bid`, `legalName` | `statusCode == true` (immediately sets `addedAt`) | **1 credit** |
| **Pre-Refresh Check** | `server/common/mcastatus/service.php` | `GET` | `getUpgradeStatusForCompanies` | None | `mca_status == "NORMAL"` | 0 credits |
| **Trigger Refresh** | `server/common/urs/service.php` | `GET` | `requestProbeDataUpdate` | `bid`, `userName`, `platform="b2c"` | `status == "PENDING"` or `status == "REQUESTED"` | 0 credits |
| **Poll Refresh** | `server/common/urs/service.php` | `GET` | `getDataEntryRequestStatus` | `bid`, `platform="b2c"` | `status == "REQUESTED"` ⇒ in progress<br>`status == "NO PENDING REQUEST"` ⇒ complete | 0 credits |
| **Stage Tracker** | `server/common/api/probeRequestService.php` | `GET` | `getStatus` | `id` (`req_id`) | Returns 6 pipeline execution stages | 0 credits |
| **Freshness Check** | `server/common/api/service.php` | `GET` | `getDataStatus` | `bid` | `download_status.status == "AVAILABLE"`, `qa.date` | 0 credits |

---

## 1. Baseline & Authentication Protocol

Every API call to the vendor system is authenticated via HTTP session cookies (`PHPSESSID`, `user`) and signed with an HMAC-SHA256 (HS256) JSON Web Token (JWT).

### 1.1 JWT Signing Secret Acquisition
- **Method:** `GET`
- **URL:** `server/common/jwt/service.php?action=getJwtToken&v=3.1.6&cv=8.1.26`
- **Response Headers:** `Content-Type: application/json`
- **Response Body:**
  ```json
  {
    "jwtToken": "<HEX_ENCODED_32_BYTE_SECRET>"
  }
  ```
- **Signing Rule:** Every API request encapsulates its JSON action payload in an HS256 JWT passed via the `qp` query parameter (for GET) or `pp` form body parameter (for POST).

### 1.2 Authentication & Login
- **Method:** `POST`
- **URL:** `server/user/login.php?v=3.1.6&cv=8.1.26`
- **Content-Type:** `application/x-www-form-urlencoded`
- **Body:** `action=login&u=<REDACTED_USERNAME>&p=<REDACTED_PASSWORD>&mcc=91&rememberMe=true` (or signed via `pp=<JWT>`)
- **Response Headers:** `Set-Cookie: PHPSESSID=<REDACTED>; ...`, `Set-Cookie: user=<REDACTED>; ...`
- **Response Body:**
  ```json
  {
    "id": <REDACTED_USER_ID>,
    "user_name": "<REDACTED_USER_NAME>",
    "email": "<REDACTED_EMAIL>",
    "authentication_token": "<REDACTED_TOKEN>",
    "custom_data": ["ref_doc", "probe_score", "pnp_access", "cersai_access"],
    "preferred_currency": "INR"
  }
  ```

---

## 2. Locked vs. Unlocked Determination

Today's code previously attempted to detect locked status by parsing HTTP 200 responses for embedded HTML error fragments. The vendor API exposes a completely explicit, unambiguous call.

### 2.1 Authoritative Status Check: `getAssetTeams`
This is the single source of truth for whether a corporate is unlocked, by which team, and when.

- **Method:** `GET`
- **URL:** `server/user/service.php?qp=<JWT>&v=3.1.6&cv=8.1.26`
- **Action:** `getAssetTeams`
- **Request Payload:**
  ```json
  {
    "action": "getAssetTeams",
    "bid": "<64_HEX_BID>"
  }
  ```

#### Response when LOCKED (or Expired):
```json
{
  "teams": [
    {
      "teamId": <REDACTED_TEAM_ID>,
      "addedAt": null,
      "creditUsed": false,
      "created_by": <REDACTED_USER_ID>
    }
  ]
}
```

#### Response when UNLOCKED:
```json
{
  "teams": [
    {
      "teamId": <REDACTED_TEAM_ID>,
      "addedAt": "2026-09-22T09:41:59+05:30",
      "creditUsed": true,
      "created_by": <REDACTED_USER_ID>,
      "srcTeamId": <REDACTED_TEAM_ID>
    }
  ]
}
```

#### Contract Rules for Automation:
1. `teams[0].addedAt != null`: The company is **Unlocked**. `addedAt` provides the exact original unlock timestamp as an ISO 8601 string (`YYYY-MM-DDTHH:mm:ss+05:30`).
2. `teams[0].addedAt == null`: The company is **Locked** (or Expired).
3. `teams[0].creditUsed`: Indicates whether a billed unlock credit was deducted for this team.
4. **Zero-Spend Optimization:** Automation must call `getAssetTeams(bid)` before spending: if `addedAt != null`, adopt the report immediately with 0 credits consumed.

### 2.2 Paid Endpoints on Locked Companies
When `teams[0].addedAt == null`:
- `server/common/docService/service.php` (`referenceDocs`): Returns **HTTP 404** (`Not Found`).
- `server/common/api/service.php` (`financeData`): Returns **HTTP 404** (`Not Found`).
- `server/common/publishing/service.php` (`publishProbedData` / Workbook export): Returns **HTTP 404** (`Not Found`).
- `server/common/api/service.php` (`getKeyStats`): Returns **HTTP 200** with basic MCA registration master metadata only.

### 2.3 Free Pre-Unlock Identity Verification: `getCompanyPreview`
Before spending a credit, the pipeline can verify company identity (CIN, legal name, address, capital) for 0 credits:
- **Method:** `GET`
- **URL:** `server/common/api/service.php?qp=<JWT>&v=3.1.6&cv=8.1.26`
- **Action:** `getCompanyPreview`
- **Request Payload:**
  ```json
  {
    "action": "getCompanyPreview",
    "bid": "<64_HEX_BID>"
  }
  ```
- **Response Body (HTTP 200, 0 credits consumed on both locked and unlocked companies):**
  ```json
  {
    "about_company": {
      "summary": {
        "cin": "L22210MH1995PLC084781",
        "bid": "<64_HEX_BID>",
        "type": "COMPANY",
        "legal_name": "<LEGAL_NAME>",
        "incorporation_date": "1995-01-19",
        "paid_up_capital": 3618087518,
        "sum_of_charges": 3309300000,
        "authorized_capital": 5650770000,
        "company_llp_status": "Active",
        "active_compliance": "Active Compliant",
        "registered_address": {
          "full_address": "<REDACTED_ADDRESS>",
          "city": "<CITY>",
          "state": "<STATE>",
          "pincode": "<PINCODE>"
        }
      }
    }
  }
  ```

---

## 3. Unlock Action (Asset Procurement) — Live Verified

Verified live on production session on 2026-09-22 spending exactly 1 credit.

### 3.1 Pre-Unlock Upgrade Check: `getUpgradeStatusForUnlockingAsset`
Before initiating procurement, the client checks for any MCA maintenance outage:
- **Method:** `GET`
- **URL:** `server/common/mcastatus/service.php?qp=<JWT>&v=3.1.6&cv=8.1.26`
- **Action:** `getUpgradeStatusForUnlockingAsset`
- **Request Payload:**
  ```json
  {
    "action": "getUpgradeStatusForUnlockingAsset"
  }
  ```
- **Response (HTTP 200):**
  ```json
  {
    "mca_status": "NORMAL"
  }
  ```

### 3.2 Unlock Execution: `addAsset`
The mutation that deducts 1 credit to unlock a corporate.
- **Method:** `POST`
- **URL:** `server/user/service.php?v=3.1.6&cv=8.1.26`
- **Content-Type:** `application/x-www-form-urlencoded`
- **Body:** `pp=<JWT>`
- **Request Payload:**
  ```json
  {
    "action": "addAsset",
    "teamId": <REDACTED_TEAM_ID>,
    "bid": "<64_HEX_BID>",
    "legalName": "<LEGAL_NAME>"
  }
  ```
- **Response (HTTP 200):**
  ```json
  {
    "statusCode": true
  }
  ```

### 3.3 Verification of State & Balance Post-Unlock
1. **Credit Decrement:** Credits decrease synchronously by 1 credit (e.g., from 3 to 2, verified via `server/user/service.php?action=getTeams`). `assets_count` increments by 1.
2. **Authoritative Timestamp:** `getAssetTeams(bid)` immediately returns `addedAt: "2026-09-22T09:41:59+05:30"` and `creditUsed: true`.
3. **Unlock Validity Window:**
   - Strictly **12 months** from `addedAt`.
   - Stored in active assets as `validTill = addedAt + 1 year - 1 day`.
   - A subsequent Refresh **never** extends or resets `addedAt` or `validTill`.
4. **Immediate Tab Access:**
   - `server/common/docService/service.php` (`referenceDocs`): transitions from 404 to **HTTP 200**.
   - `server/common/api/service.php` (`financeData`): transitions from 404 to **HTTP 200**.
   - UI renders green banner: `Unlocked on 22 Sep, 2026`.

---

## 4. Refresh Trigger & Polling Workflow — Live Verified

Verified live on an unlocked company with data >24 hours stale.

### 4.1 Cost & Billing Rule
- **Refresh Cost:** **0 credits (Completely Free)**.
- Triggering a refresh never deducts credits.

### 4.2 Pre-Refresh Upgrade Check: `getUpgradeStatusForCompanies`
- **Method:** `GET`
- **URL:** `server/common/mcastatus/service.php?qp=<JWT>&v=3.1.6&cv=8.1.26`
- **Action:** `getUpgradeStatusForCompanies`
- **Request Payload:**
  ```json
  {
    "action": "getUpgradeStatusForCompanies"
  }
  ```
- **Response (HTTP 200):**
  ```json
  {
    "mca_status": "NORMAL"
  }
  ```

### 4.3 Refresh Trigger: `requestProbeDataUpdate`
- **Method:** `GET`
- **URL:** `server/common/urs/service.php?qp=<JWT>&v=3.1.6&cv=8.1.26`
- **Action:** `requestProbeDataUpdate` (or `requestProbeData` if first probe)
- **Request Payload:**
  ```json
  {
    "action": "requestProbeDataUpdate",
    "bid": "<64_HEX_BID>",
    "userName": "<REDACTED_USER_NAME>",
    "platform": "b2c"
  }
  ```
- **Response (HTTP 200):**
  ```json
  {
    "id": "<NUMERIC_REQUEST_ID>",
    "status_id": "<NUMERIC_REQUEST_ID>",
    "req_id": null,
    "user_id": "<REDACTED_USER_ID>",
    "user_name": "<REDACTED_USER_NAME>",
    "bid": "<64_HEX_BID>",
    "comp_legal_name": null,
    "requested_on_time": "2026-09-22T09:36:56+05:30",
    "status": "PENDING",
    "platform": "b2c"
  }
  ```

### 4.4 Lightweight Status Poll: `getDataEntryRequestStatus`
The periodic check used by automation to track probe completion.
- **Method:** `GET`
- **URL:** `server/common/urs/service.php?qp=<JWT>&v=3.1.6&cv=8.1.26`
- **Action:** `getDataEntryRequestStatus`
- **Request Payload:**
  ```json
  {
    "action": "getDataEntryRequestStatus",
    "bid": "<64_HEX_BID>",
    "platform": "b2c"
  }
  ```
- **Live Response while IN-PROGRESS:**
  ```json
  {
    "id": "<NUMERIC_REQUEST_ID>",
    "status_id": "<NUMERIC_REQUEST_ID>",
    "req_id": "<HEX_REQUEST_ID>",
    "user_id": "<REDACTED_USER_ID>",
    "user_name": "<REDACTED_USER_NAME>",
    "bid": "<64_HEX_BID>",
    "comp_legal_name": "<LEGAL_NAME>",
    "requested_on_time": "2026-09-22T09:36:56+05:30",
    "status": "REQUESTED",
    "platform": "b2c"
  }
  ```
- **Live Response when COMPLETED / IDLE:**
  ```json
  {
    "status": "NO PENDING REQUEST"
  }
  ```

### 4.5 6-Stage Progress Tracker: `probeRequestService`
When a request is in flight (`req_id != null`), the frontend queries the tracker:
- **Method:** `GET`
- **URL:** `server/common/api/probeRequestService.php?qp=<JWT>&v=3.1.6&cv=8.1.26`
- **Action:** `getStatus`
- **Request Payload:**
  ```json
  {
    "action": "getStatus",
    "id": "<HEX_REQUEST_ID>"
  }
  ```
- **Live Response:**
  ```json
  {
    "DataEntryRequest": {
      "createdTime": "1790050016",
      "completedTime": "1790050016",
      "status": "Done"
    },
    "DocCollected": {
      "createdTime": null,
      "completedTime": null,
      "status": "Yet_to_start"
    },
    "FESGroup": {
      "createdTime": null,
      "completedTime": null,
      "status": "Yet_to_start"
    },
    "CESGroup": {
      "createdTime": null,
      "completedTime": null,
      "status": "Yet_to_start"
    },
    "DataCurationGroup": {
      "createdTime": null,
      "completedTime": null,
      "status": "Yet_to_start"
    },
    "DataDistributionGroup": {
      "createdTime": null,
      "completedTime": null,
      "status": "Yet_to_start"
    }
  }
  ```

### 4.6 Completion & Freshness Indicator: `getDataStatus`
Once `getDataEntryRequestStatus` returns `"NO PENDING REQUEST"`, query `getDataStatus`:
- **Method:** `GET`
- **URL:** `server/common/api/service.php?qp=<JWT>&v=3.1.6&cv=8.1.26`
- **Action:** `getDataStatus`
- **Key Completion Indicators:**
  - `download_status`: `{"status": "AVAILABLE", "updatedAt": "..."}`
  - `downloaded`: ISO timestamp of the latest document crawl.
  - `qa.date`: Date string (`YYYY-MM-DD`) indicating verified completion.

---

## 5. Expired / 12-Month Elapsed Company Behavior

Verified on an asset unlocked >12 months ago:
- `getAssetTeams(bid)` returns:
  ```json
  {
    "teams": [
      {
        "teamId": <REDACTED_TEAM_ID>,
        "addedAt": null,
        "creditUsed": false,
        "created_by": <REDACTED_USER_ID>
      }
    ]
  }
  ```
- **Behavior:** Expired companies are treated **identically to locked companies**.
- Paid endpoints (`referenceDocs`, `financeData`, `publishProbedData`) return **HTTP 404**.
- **Conclusion for Pipeline:** Automation can treat any company where `teams[0].addedAt == null` as requiring an unlock spend, whether it is newly discovered or expired.

---

## 6. Implementation Blueprint for PR R

```csharp
// 1. Check if unlocked
var assetTeams = await GetAssetTeamsAsync(bid, ct);
var isUnlocked = assetTeams.Teams.Any(t => t.AddedAt != null);

if (!isUnlocked)
{
    // 2. Pre-unlock identity preview verification (0 credits)
    var preview = await GetCompanyPreviewAsync(bid, ct);
    if (!string.Equals(preview.Cin, expectedCin, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"CIN mismatch before unlock: expected {expectedCin}, found {preview.Cin}");
    }

    // 3. Pre-unlock MCA upgrade status check
    var upgradeStatus = await GetUpgradeStatusForUnlockingAssetAsync(ct);
    if (upgradeStatus.McaStatus == "ALERT")
    {
        throw new McaUpgradeAlertException(upgradeStatus.AlertText);
    }

    // 4. Billed Unlock (deducts 1 credit)
    var unlockResult = await AddAssetAsync(teamId, bid, preview.LegalName, ct);
    if (!unlockResult.StatusCode)
    {
        throw new ReferenceToolException("Failed to unlock asset.");
    }
}

// 5. Freshness check (< 24h rule)
var dataStatus = await GetDataStatusAsync(bid, ct);
var isStale = dataStatus.Downloaded == null || (DateTimeOffset.UtcNow - dataStatus.Downloaded.Value).TotalHours > 24;

if (isStale)
{
    // 6. Check if refresh is already pending
    var status = await GetDataEntryRequestStatusAsync(bid, ct);
    if (status.Status != "REQUESTED")
    {
        await RequestProbeDataUpdateAsync(bid, userName, ct);
    }

    // 7. Poll until completion (free, 0 credits)
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        var currentStatus = await GetDataEntryRequestStatusAsync(bid, ct);
        if (currentStatus.Status == "NO PENDING REQUEST")
        {
            break;
        }
    }
}

// 8. Ingestion: referenceDocs, financeData, and publishProbedData are now accessible.
```

---

## 7. Captured Artifacts Inventory

All raw JSON traces and screenshots are maintained outside the repository at `E:\Downloads\ref-app-unlock-refresh\`:

| File | Format | Contents |
|---|---|---|
| `01-unlocked-coastal-calls.json` | JSON | Network calls for unlocked stale corporate. |
| `01-unlocked-coastal.png` | PNG | UI view before refresh. |
| `02-expired-nambi-calls.json` | JSON | Network calls for expired corporate (>12 months). |
| `02-expired-nambi.png` | PNG | UI view of expired corporate showing locked modal. |
| `03-locked-infosys-calls.json` | JSON | Network calls for locked corporate. |
| `03-locked-infosys.png` | PNG | UI view of locked corporate. |
| `05-refresh-coastal-ui-calls.json` | JSON | Network calls from UI click of Refresh button. |
| `05-coastal-updating-header.png` | PNG | Header showing "Updating" spinner state. |
| `05-coastal-tracker-modal.png` | PNG | Dialog showing 6-stage tracker. |
| `06-tcs-unlock-calls.json` | JSON | Live network calls for 1-credit unlock. |
| `06-tcs-unlocked-page.png` | PNG | Unlocked view post-spend showing "Unlocked on 22 Sep, 2026". |
| `credits_info.json` | JSON | Credit balance and tier structures. |
