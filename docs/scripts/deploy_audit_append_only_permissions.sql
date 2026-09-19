-- ============================================================================
-- MCA ROC Analysis - AuditLogs Immutability & Least-Privilege Permissions Script
-- Resolves Issue #232 / PR #234 Operational Finding (P2)
--
-- PURPOSE:
-- 1. Enforces append-only immutability on dbo.AuditLogs via database trigger.
-- 2. Configures least-privilege database permissions for application connection users.
-- 3. Prohibits UPDATE and DELETE operations for the operational application role.
-- 4. Documents external retention / purge bypass pattern for privileged DBAs.
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. Idempotent In-Engine Trigger Enforcement
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.TR_AuditLogs_AppendOnly', 'TR') IS NULL
BEGIN
    EXEC(N'
    CREATE TRIGGER [dbo].[TR_AuditLogs_AppendOnly]
    ON [dbo].[AuditLogs]
    INSTEAD OF UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        RAISERROR(''Table dbo.AuditLogs is append-only. UPDATE and DELETE operations are forbidden.'', 16, 1);
        ROLLBACK TRANSACTION;
    END;
    ');
    PRINT 'Created trigger dbo.TR_AuditLogs_AppendOnly';
END
ELSE
BEGIN
    PRINT 'Trigger dbo.TR_AuditLogs_AppendOnly already exists.';
END
GO

-- ----------------------------------------------------------------------------
-- 2. Application Database Role & Least-Privilege Permissions
-- ----------------------------------------------------------------------------
-- Define application role if not already created
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'McaRocAppRole' AND type = 'R')
BEGIN
    CREATE ROLE [McaRocAppRole];
    PRINT 'Created role [McaRocAppRole]';
END
GO

-- Grant operational DML (INSERT, SELECT) on AuditLogs
GRANT SELECT, INSERT ON [dbo].[AuditLogs] TO [McaRocAppRole];
PRINT 'Granted SELECT, INSERT on dbo.AuditLogs to [McaRocAppRole]';

-- Explicitly DENY destructive DML (UPDATE, DELETE) on AuditLogs
DENY UPDATE, DELETE ON [dbo].[AuditLogs] TO [McaRocAppRole];
PRINT 'Denied UPDATE, DELETE on dbo.AuditLogs to [McaRocAppRole]';
GO

-- ----------------------------------------------------------------------------
-- 3. Verification Script (Run in Staging to Verify Controls)
-- ----------------------------------------------------------------------------
/*
-- Step 3a: Verify INSERT succeeds
INSERT INTO [dbo].[AuditLogs] (
    TimestampUtc, CorrelationId, ActorType, ActorId, Action, EventKind, Status, EventPayloadJson
) VALUES (
    SYSUTCDATETIME(), '00000000000000000000000000000001', 'SystemWorker', 'DeploymentVerification',
    'OtherMutation', 'HttpMutation', 'Success', '{"verification": true}'
);
PRINT 'Verification INSERT succeeded.';

-- Step 3b: Verify UPDATE is blocked
BEGIN TRY
    UPDATE [dbo].[AuditLogs]
    SET Status = 'Failure'
    WHERE CorrelationId = '00000000000000000000000000000001';
    PRINT 'ERROR: UPDATE succeeded but should have been blocked!';
END TRY
BEGIN CATCH
    PRINT 'SUCCESS: UPDATE was blocked as expected. Error: ' + ERROR_MESSAGE();
END CATCH;

-- Step 3c: Verify DELETE is blocked
BEGIN TRY
    DELETE FROM [dbo].[AuditLogs]
    WHERE CorrelationId = '00000000000000000000000000000001';
    PRINT 'ERROR: DELETE succeeded but should have been blocked!';
END TRY
BEGIN CATCH
    PRINT 'SUCCESS: DELETE was blocked as expected. Error: ' + ERROR_MESSAGE();
END CATCH;
*/
GO

-- ----------------------------------------------------------------------------
-- 4. Privileged Retention / Maintenance Window Pattern (DBA Only)
-- ----------------------------------------------------------------------------
-- When executing scheduled retention purges (e.g. 7-year regulatory retention)
-- under a privileged DBA account:
--
-- ALTER TABLE [dbo].[AuditLogs] DISABLE TRIGGER [TR_AuditLogs_AppendOnly];
--
-- DELETE FROM [dbo].[AuditLogs]
-- WHERE TimestampUtc < DATEADD(YEAR, -7, SYSUTCDATETIME());
--
-- ALTER TABLE [dbo].[AuditLogs] ENABLE TRIGGER [TR_AuditLogs_AppendOnly];
-- ============================================================================
