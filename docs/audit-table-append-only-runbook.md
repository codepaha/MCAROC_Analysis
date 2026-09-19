# Operational Runbook: AuditLogs Append-Only Enforcement & Role Permissions

This document outlines the operational deployment, permission architecture, in-database immutability controls, and retention maintenance procedures for the **Redacted Internal Audit Framework** (`dbo.AuditLogs`).

---

## 1. Security Architecture & Threat Model

The `dbo.AuditLogs` table provides an internal forensic record of mutating user actions and background chunking worker lifecycle events. To satisfy data integrity standards and prevent tampering by application-level bugs or compromised operational accounts, the table is protected by a defense-in-depth model:

1. **In-Engine Immutability Trigger (`dbo.TR_AuditLogs_AppendOnly`)**:
   - Deployed directly via EF migration `20260919081002_AddAuditFrameworkAndCorrelation`.
   - Fires on `INSTEAD OF UPDATE, DELETE` across all connections, raising a severity 16 error and rolling back any transaction attempting to mutate or delete existing rows.
   - Operates regardless of connection string user privileges.

2. **Least-Privilege Database Role (`McaRocAppRole`)**:
   - The application pool connects using a dedicated database user assigned to `McaRocAppRole`.
   - Explicit `GRANT SELECT, INSERT` allows normal forensic event writes and authorized internal reviewer queries.
   - Explicit `DENY UPDATE, DELETE` guarantees that even if the trigger were disabled or bypassed, the application principal cannot modify or purge audit entries.

---

## 2. Deployment Instructions

### Automated EF Migration
On application startup or CI/CD deployment:
```bash
dotnet ef database update
```
The migration automatically provisions the table schema, indexes, and creates the `TR_AuditLogs_AppendOnly` trigger.

### Database Role Permissions Configuration
A DBA or release engineer executes [`docs/scripts/deploy_audit_append_only_permissions.sql`](file:///e:/MCAROC_Analysis/docs/scripts/deploy_audit_append_only_permissions.sql) against the target database:

```sql
-- 1. Ensure trigger is active
IF OBJECT_ID('dbo.TR_AuditLogs_AppendOnly', 'TR') IS NULL
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_AuditLogs_AppendOnly]
    ON [dbo].[AuditLogs] INSTEAD OF UPDATE, DELETE AS
    BEGIN
        SET NOCOUNT ON;
        RAISERROR(''Table dbo.AuditLogs is append-only. UPDATE and DELETE operations are forbidden.'', 16, 1);
        ROLLBACK TRANSACTION;
    END;');
END

-- 2. Provision application role
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'McaRocAppRole' AND type = 'R')
    CREATE ROLE [McaRocAppRole];

-- 3. Grant append and read, deny update and delete
GRANT SELECT, INSERT ON [dbo].[AuditLogs] TO [McaRocAppRole];
DENY UPDATE, DELETE ON [dbo].[AuditLogs] TO [McaRocAppRole];

-- 4. Add the application database user to the role
ALTER ROLE [McaRocAppRole] ADD MEMBER [<AppDatabaseUser>];
```

---

## 3. Environment Verification Procedure

Run the following verification script in staging or post-deployment to validate that the controls are operational:

```sql
-- Test 1: INSERT must succeed
INSERT INTO [dbo].[AuditLogs] (
    TimestampUtc, CorrelationId, ActorType, ActorId, Action, EventKind, Status, EventPayloadJson
) VALUES (
    SYSUTCDATETIME(), 'test_verification_000000000001', 'SystemWorker', 'DeploymentVerification',
    'OtherMutation', 'HttpMutation', 'Success', '{"verification": true}'
);
-- Expected: (1 row affected)

-- Test 2: UPDATE must fail
UPDATE [dbo].[AuditLogs]
SET Status = 'Failure'
WHERE CorrelationId = 'test_verification_000000000001';
-- Expected: Msg 50000, Level 16: Table dbo.AuditLogs is append-only. UPDATE and DELETE operations are forbidden.

-- Test 3: DELETE must fail
DELETE FROM [dbo].[AuditLogs]
WHERE CorrelationId = 'test_verification_000000000001';
-- Expected: Msg 50000, Level 16: Table dbo.AuditLogs is append-only. UPDATE and DELETE operations are forbidden.
```

---

## 4. Retention & Archival Runbook (Privileged Maintenance Only)

Corporate compliance policies (Companies Act 2013 / MCA records retention) require retaining audit logs for a minimum statutory period (typically 7 to 8 years).

When purging records older than the statutory retention limit, **the application never performs deletes**. Retention is managed via an external, privileged SQL Agent maintenance job or DBA script:

```sql
-- Must be executed by a sysadmin or db_owner principal during a scheduled maintenance window:
BEGIN TRANSACTION;

-- Temporarily disable the append-only trigger
ALTER TABLE [dbo].[AuditLogs] DISABLE TRIGGER [TR_AuditLogs_AppendOnly];

-- Archive to cold storage / partition table if needed, then purge expired records
DELETE FROM [dbo].[AuditLogs]
WHERE TimestampUtc < DATEADD(YEAR, -8, SYSUTCDATETIME());

-- Re-enable the append-only trigger
ALTER TABLE [dbo].[AuditLogs] ENABLE TRIGGER [TR_AuditLogs_AppendOnly];

COMMIT TRANSACTION;
```
