/*
  #425 — one-time cleanup of litigation cases stored twice because of #388.

  Rows imported before #388 have no provider record id (ProviderCaseId IS NULL). A later report that
  returned the same record with its id could not match them, so the case was stored again. Each such pair
  is merged into the newer row, which is the one current reports list:

    - the legacy row's source-report links move to the kept row (report provenance is not lost);
    - its case analyses move to the kept row for runs where the kept row has none (older runs keep their
      history); analyses in runs that already analysed the kept row are exact duplicates and are deleted;
    - a legacy order is deleted (with its document, chunks and classifications) only when the kept row has
      the same order: same PDF URL, date and type, or same date and type with byte-identical downloaded
      content. Every other legacy order moves to the kept row with its document, chunks and classifications,
      so no order exists only in the deleted row;
    - the legacy row is deleted.

  A pair is merged only when, in the same request, exactly one legacy row and exactly one row with an id
  share the CSP, proceeding type, case number and year — all present on both rows, so blank fields never
  reduce the match to CSP alone — and their CNRs do not conflict (both present and different). Anything
  ambiguous is left alone and listed.

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
 AND k.ProceedingType = l.ProceedingType
 AND UPPER(LTRIM(RTRIM(k.CaseNumber))) = UPPER(LTRIM(RTRIM(l.CaseNumber)))
 AND LTRIM(RTRIM(k.CaseYear)) = LTRIM(RTRIM(l.CaseYear))
 AND (k.Cnr IS NULL OR l.Cnr IS NULL OR k.Cnr = l.Cnr)
WHERE l.ProviderCaseId IS NULL
  AND l.CspId IS NOT NULL
  AND l.ProceedingType IS NOT NULL
  AND LTRIM(RTRIM(ISNULL(l.CaseNumber, ''))) <> ''
  AND LTRIM(RTRIM(ISNULL(l.CaseYear, ''))) <> ''
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

-- 3. Orders only the legacy row holds move to the kept row with their document, chunks and classifications.
--    An order counts as already held when the kept row has the same date and type and either the same PDF URL
--    (null-safe), the same URL once the pre-signing parameters are ignored (the import's own #362 rule:
--    same address and same remaining query parameters), or a downloaded document with the same content hash.
DECLARE @MovedOrders TABLE (LitigationCaseOrderId bigint PRIMARY KEY, KeptId bigint);
INSERT @MovedOrders
SELECT o.LitigationCaseOrderId, p.KeptId
FROM LitigationCaseOrders o
JOIN @Pairs p ON p.LegacyId = o.LitigationCaseId
WHERE NOT EXISTS (
    SELECT 1 FROM LitigationCaseOrders k
    WHERE k.LitigationCaseId = p.KeptId
      AND ISNULL(k.OrderDate, '') = ISNULL(o.OrderDate, '')
      AND ISNULL(k.OrderType, '') = ISNULL(o.OrderType, '')
      AND (k.PdfUrl = o.PdfUrl
           OR (k.PdfUrl IS NULL AND o.PdfUrl IS NULL)
           OR (LEFT(k.PdfUrl, CHARINDEX('?', k.PdfUrl + '?') - 1) = LEFT(o.PdfUrl, CHARINDEX('?', o.PdfUrl + '?') - 1)
               AND NOT EXISTS (
                   (SELECT q.value FROM STRING_SPLIT(SUBSTRING(k.PdfUrl, CHARINDEX('?', k.PdfUrl + '?') + 1, LEN(k.PdfUrl)), '&') q
                    WHERE q.value <> '' AND LOWER(LEFT(q.value, CHARINDEX('=', q.value + '=') - 1)) NOT IN
                      ('x-amz-algorithm', 'x-amz-credential', 'x-amz-date', 'x-amz-expires', 'x-amz-signedheaders',
                       'x-amz-signature', 'x-amz-security-token', 'awsaccesskeyid', 'signature', 'expires')
                    EXCEPT
                    SELECT q.value FROM STRING_SPLIT(SUBSTRING(o.PdfUrl, CHARINDEX('?', o.PdfUrl + '?') + 1, LEN(o.PdfUrl)), '&') q)
                   UNION ALL
                   (SELECT q.value FROM STRING_SPLIT(SUBSTRING(o.PdfUrl, CHARINDEX('?', o.PdfUrl + '?') + 1, LEN(o.PdfUrl)), '&') q
                    WHERE q.value <> '' AND LOWER(LEFT(q.value, CHARINDEX('=', q.value + '=') - 1)) NOT IN
                      ('x-amz-algorithm', 'x-amz-credential', 'x-amz-date', 'x-amz-expires', 'x-amz-signedheaders',
                       'x-amz-signature', 'x-amz-security-token', 'awsaccesskeyid', 'signature', 'expires')
                    EXCEPT
                    SELECT q.value FROM STRING_SPLIT(SUBSTRING(k.PdfUrl, CHARINDEX('?', k.PdfUrl + '?') + 1, LEN(k.PdfUrl)), '&') q)))
           OR EXISTS (SELECT 1 FROM LitigationOrderDocuments kd
                      JOIN LitigationOrderDocuments od ON od.LitigationCaseOrderId = o.LitigationCaseOrderId
                      WHERE kd.LitigationCaseOrderId = k.LitigationCaseOrderId
                        AND kd.FileHash IS NOT NULL AND kd.FileHash = od.FileHash)));

UPDATE o SET o.LitigationCaseId = m.KeptId
FROM LitigationCaseOrders o JOIN @MovedOrders m ON m.LitigationCaseOrderId = o.LitigationCaseOrderId;
SELECT 'orders held only by the legacy row, moved (documents follow)' AS step, @@ROWCOUNT AS rows;

UPDATE k SET k.LitigationCaseId = m.KeptId
FROM LitigationOrderChunks k JOIN @MovedOrders m ON m.LitigationCaseOrderId = k.LitigationCaseOrderId;
SELECT 'order chunks moved with them' AS step, @@ROWCOUNT AS rows;

UPDATE c SET c.LitigationCaseId = m.KeptId
FROM LitigationOrderClassifications c JOIN @MovedOrders m ON m.LitigationCaseOrderId = c.LitigationCaseOrderId;
SELECT 'order classifications moved with them' AS step, @@ROWCOUNT AS rows;

-- 4. The remaining legacy order-level rows duplicate the kept row's (no foreign key cascades to these).
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

-- 5. Duplicate orders (their documents cascade), then the legacy cases (remaining links cascade).
DELETE o FROM LitigationCaseOrders o JOIN @Pairs p ON p.LegacyId = o.LitigationCaseId;
SELECT 'duplicate orders deleted (documents cascade)' AS step, @@ROWCOUNT AS rows;

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
