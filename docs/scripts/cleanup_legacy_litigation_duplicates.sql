/*
  #425 — one-time cleanup of litigation cases stored twice because of #388.

  Rows imported before #388 have no provider record id (ProviderCaseId IS NULL). A later report that
  returned the same record with its id could not match them, so the case was stored again. Each such pair
  is merged into the newer row, which is the one current reports list:

    - the legacy row's source-report links move to the kept row (report provenance is not lost);
    - its case analyses move to the kept row for runs where the kept row has none (older runs keep their
      history); analyses in runs that already analysed the kept row are exact duplicates and are deleted;
    - its orders, order documents, order chunks and order classifications are deleted; the kept row holds
      the same orders from the newer report;
    - the legacy row is deleted.

  A pair is merged only when, in the same request, exactly one legacy row and exactly one row with an id
  share the CSP, proceeding type, case number and year. Anything ambiguous is left alone and listed.

  The deleted order documents' PDF paths are returned as the last result set, so their files can be removed
  from disk afterwards. No other document uses those paths: check before deleting files.

  Run with @DryRun = 1 first (everything rolls back), then with @DryRun = 0.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON; -- sqlcmd defaults it OFF; the filtered unique indexes need it
SET ANSI_NULLS ON;

DECLARE @DryRun bit = 1;
DECLARE @RequestId bigint = NULL; -- NULL = every request

BEGIN TRANSACTION;

DECLARE @Candidates TABLE (LegacyId bigint, KeptId bigint, RequestId bigint);
INSERT @Candidates (LegacyId, KeptId, RequestId)
SELECT l.LitigationCaseId, k.LitigationCaseId, l.RequestId
FROM LitigationCases l
JOIN LitigationCases k
  ON k.RequestId = l.RequestId
 AND k.ProviderCaseId IS NOT NULL
 AND k.CspId = l.CspId
 AND ISNULL(k.ProceedingType, '') = ISNULL(l.ProceedingType, '')
 AND UPPER(LTRIM(RTRIM(ISNULL(k.CaseNumber, '')))) = UPPER(LTRIM(RTRIM(ISNULL(l.CaseNumber, ''))))
 AND LTRIM(RTRIM(ISNULL(k.CaseYear, ''))) = LTRIM(RTRIM(ISNULL(l.CaseYear, '')))
WHERE l.ProviderCaseId IS NULL
  AND l.CspId IS NOT NULL
  AND (@RequestId IS NULL OR l.RequestId = @RequestId);

DECLARE @Pairs TABLE (LegacyId bigint PRIMARY KEY, KeptId bigint UNIQUE, RequestId bigint);
INSERT @Pairs
SELECT c.LegacyId, c.KeptId, c.RequestId
FROM @Candidates c
WHERE (SELECT COUNT(*) FROM @Candidates x WHERE x.LegacyId = c.LegacyId) = 1
  AND (SELECT COUNT(*) FROM @Candidates x WHERE x.KeptId = c.KeptId) = 1;

SELECT 'pairs to merge' AS step, COUNT(*) AS rows FROM @Pairs
UNION ALL
SELECT 'ambiguous legacy rows left alone', COUNT(DISTINCT LegacyId) FROM @Candidates WHERE LegacyId NOT IN (SELECT LegacyId FROM @Pairs);

-- 1. Report provenance moves to the kept row.
UPDATE s SET s.LitigationCaseId = p.KeptId
FROM LitigationCaseSourceReports s
JOIN @Pairs p ON p.LegacyId = s.LitigationCaseId
WHERE NOT EXISTS (SELECT 1 FROM LitigationCaseSourceReports k
                  WHERE k.LitigationCaseId = p.KeptId AND k.LitigationReportSnapshotId = s.LitigationReportSnapshotId);
SELECT 'source-report links moved' AS step, @@ROWCOUNT AS rows;

-- 2. Case analyses: keep older runs' history on the kept row; drop exact duplicates.
UPDATE a SET a.LitigationCaseId = p.KeptId
FROM LitigationCaseAiAnalyses a
JOIN @Pairs p ON p.LegacyId = a.LitigationCaseId
WHERE NOT EXISTS (SELECT 1 FROM LitigationCaseAiAnalyses k
                  WHERE k.LitigationCaseId = p.KeptId AND k.LitigationAiAnalysisRunId = a.LitigationAiAnalysisRunId);
SELECT 'analyses moved (runs without the kept row)' AS step, @@ROWCOUNT AS rows;

DELETE a FROM LitigationCaseAiAnalyses a JOIN @Pairs p ON p.LegacyId = a.LitigationCaseId;
SELECT 'duplicate analyses deleted' AS step, @@ROWCOUNT AS rows;

-- 3. Order-level rows of the legacy case (no foreign key cascades to these).
DELETE c FROM LitigationOrderClassifications c JOIN @Pairs p ON p.LegacyId = c.LitigationCaseId;
SELECT 'order classifications deleted' AS step, @@ROWCOUNT AS rows;

DELETE k FROM LitigationOrderChunks k JOIN @Pairs p ON p.LegacyId = k.LitigationCaseId;
SELECT 'order chunks deleted' AS step, @@ROWCOUNT AS rows;

DECLARE @Files TABLE (StoragePath nvarchar(max));
INSERT @Files
SELECT d.StoragePath
FROM LitigationOrderDocuments d
JOIN LitigationCaseOrders o ON o.LitigationCaseOrderId = d.LitigationCaseOrderId
JOIN @Pairs p ON p.LegacyId = o.LitigationCaseId
WHERE d.StoragePath IS NOT NULL;

-- 4. Orders (their documents cascade), then the legacy cases (remaining links cascade).
DELETE o FROM LitigationCaseOrders o JOIN @Pairs p ON p.LegacyId = o.LitigationCaseId;
SELECT 'orders deleted (documents cascade)' AS step, @@ROWCOUNT AS rows;

DELETE c FROM LitigationCases c JOIN @Pairs p ON p.LegacyId = c.LitigationCaseId;
SELECT 'legacy cases deleted' AS step, @@ROWCOUNT AS rows;

SELECT RequestId, COUNT(*) AS cases_after FROM LitigationCases
WHERE RequestId IN (SELECT RequestId FROM @Pairs) GROUP BY RequestId;

SELECT f.StoragePath AS file_to_remove FROM @Files f
WHERE NOT EXISTS (SELECT 1 FROM LitigationOrderDocuments d WHERE d.StoragePath = f.StoragePath);

IF @DryRun = 1
BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'Dry run: rolled back.';
END
ELSE
BEGIN
    COMMIT TRANSACTION;
    PRINT 'Committed.';
END
