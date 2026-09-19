# DBA Operational Runbook: Enabling READ_COMMITTED_SNAPSHOT (RCSI) for CompanyMaster Delta Sync

## Purpose
This document outlines the procedure for Database Administrators (DBAs) to enable `READ_COMMITTED_SNAPSHOT` (RCSI) on the `MCAROC_Analysis` database.

Enabling RCSI ensures that reader queries (such as portal searches in `AutoFetchController.Search`) are non-blocking and read from row versioning in `tempdb` while periodic batched delta sync updates (`dbo.CompanyMasterRecords`) are committed in bounded batches.

> [!CAUTION]
> **Never execute this command from within application migrations or during active business hours.**
> Enabling RCSI requires an exclusive lock on the database to flip the catalog bit. Running it with `ROLLBACK IMMEDIATE` will terminate active transactions. Always execute this during a planned maintenance window.

---

## Pre-Requisites & Capacity Planning

1. **TempDB Sizing**:
   - RCSI stores row versions in `tempdb` for active transactions.
   - Recommended initial `tempdb` data file sizing: minimum 10 GB free space.
   - Ensure autogrowth is enabled with fixed chunk sizes (e.g. 512 MB).

2. **Maintenance Window**:
   - Schedule a 5-minute maintenance window when user traffic and background workers are quiet.

---

## Step-by-Step Procedure

### Step 1: Verify Current Status
Run the following query to check if RCSI is already enabled:
```sql
SELECT name, is_read_committed_snapshot_on, snapshot_isolation_state_desc
FROM sys.databases
WHERE name = 'MCAROC_Analysis';
```
If `is_read_committed_snapshot_on = 1`, no action is required.

### Step 2: Enable RCSI
Execute during the maintenance window:
```sql
USE master;
GO

-- Set single user mode to drain existing connections cleanly, or use rollback immediate
ALTER DATABASE [MCAROC_Analysis] 
SET READ_COMMITTED_SNAPSHOT ON 
WITH ROLLBACK AFTER 10 SECONDS;
GO

ALTER DATABASE [MCAROC_Analysis] 
SET MULTI_USER;
GO
```

### Step 3: Verification
Verify that the flag is set:
```sql
SELECT name, is_read_committed_snapshot_on
FROM sys.databases
WHERE name = 'MCAROC_Analysis';
```
Expected output: `is_read_committed_snapshot_on = 1`.

### Step 4: Monitor Lock Escalation and TempDB Under Batch Promotion
During sync worker execution, monitor `sys.dm_tran_locks` and `tempdb` usage:
```sql
-- Check active locks held on CompanyMasterRecords
SELECT resource_type, request_mode, request_status, count(*) as LockCount
FROM sys.dm_tran_locks
WHERE resource_database_id = DB_ID('MCAROC_Analysis')
GROUP BY resource_type, request_mode, request_status;

-- Check tempdb version store usage
SELECT 
    DB_NAME(database_id) AS DatabaseName,
    reserved_page_count * 8 / 1024.0 AS ReservedMb
FROM sys.dm_tran_top_version_generators;
```
If table lock escalation (`X` lock on `TAB`) is observed, reduce the `CompanyMasterSync:PromotionBatchSize` setting in `appsettings.json` (e.g. from 4000 to 2000).
