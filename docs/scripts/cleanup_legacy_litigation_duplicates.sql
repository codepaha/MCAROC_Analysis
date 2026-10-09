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

  Stop the portal while it runs: a pending document it drops may already be queued in memory.

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

-- 3. Map each legacy order to the kept row's copy of it, if there is one. An order counts as already held when
--    the kept row has the same date and type and either the same PDF URL (null-safe), the same URL once the
--    pre-signing parameters are ignored (the import's own #362 rule: same address and same remaining query
--    parameters), or a downloaded document with the same content hash.
DECLARE @OrderMap TABLE (LegacyOrderId bigint PRIMARY KEY, KeptId bigint, KeptOrderId bigint NULL);
INSERT @OrderMap
SELECT o.LitigationCaseOrderId, p.KeptId, m.LitigationCaseOrderId
FROM LitigationCaseOrders o
JOIN @Pairs p ON p.LegacyId = o.LitigationCaseId
OUTER APPLY (
    SELECT TOP 1 k.LitigationCaseOrderId FROM LitigationCaseOrders k
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
                        AND kd.FileHash IS NOT NULL AND kd.FileHash = od.FileHash))
    ORDER BY k.LitigationCaseOrderId) m;

-- 3a. Orders only the legacy row holds move to the kept row with their document, chunks and classifications.
UPDATE o SET o.LitigationCaseId = m.KeptId
FROM LitigationCaseOrders o JOIN @OrderMap m ON m.LegacyOrderId = o.LitigationCaseOrderId
WHERE m.KeptOrderId IS NULL;
SELECT 'orders held only by the legacy row, moved (documents follow)' AS step, @@ROWCOUNT AS rows;

UPDATE k SET k.LitigationCaseId = m.KeptId
FROM LitigationOrderChunks k JOIN @OrderMap m ON m.LegacyOrderId = k.LitigationCaseOrderId
WHERE m.KeptOrderId IS NULL;
SELECT 'order chunks moved with them' AS step, @@ROWCOUNT AS rows;

UPDATE c SET c.LitigationCaseId = m.KeptId
FROM LitigationOrderClassifications c JOIN @OrderMap m ON m.LegacyOrderId = c.LitigationCaseOrderId
WHERE m.KeptOrderId IS NULL;
SELECT 'order classifications moved with them' AS step, @@ROWCOUNT AS rows;

-- 3b. A duplicate order whose legacy document holds evidence (downloaded, extracted text or chunks) while the
--     kept copy's document holds none (e.g. still pending, failed, or never admitted): the legacy document, its
--     chunks and classifications are handed to the kept order and the empty document is dropped, so deleting
--     the duplicate never deletes the only usable evidence. At most one hand-over per kept order.
DECLARE @Evidence TABLE (DocId bigint PRIMARY KEY);
INSERT @Evidence
SELECT d.LitigationOrderDocumentId
FROM LitigationOrderDocuments d
WHERE d.LitigationCaseOrderId IN (SELECT LegacyOrderId FROM @OrderMap UNION SELECT KeptOrderId FROM @OrderMap WHERE KeptOrderId IS NOT NULL)
  AND (d.Status = 'Downloaded' OR d.ExtractedText IS NOT NULL
       OR EXISTS (SELECT 1 FROM LitigationOrderChunks k WHERE k.LitigationOrderDocumentId = d.LitigationOrderDocumentId));

DECLARE @Handover TABLE (LegacyDocId bigint PRIMARY KEY, KeptId bigint, KeptOrderId bigint UNIQUE, KeptDocId bigint NULL);
INSERT @Handover
SELECT x.LegacyDocId, x.KeptId, x.KeptOrderId, x.KeptDocId
FROM (
    SELECT od.LitigationOrderDocumentId AS LegacyDocId, m.KeptId, m.KeptOrderId, kd.LitigationOrderDocumentId AS KeptDocId,
           ROW_NUMBER() OVER (PARTITION BY m.KeptOrderId ORDER BY od.LitigationOrderDocumentId) AS rn
    FROM @OrderMap m
    JOIN LitigationOrderDocuments od ON od.LitigationCaseOrderId = m.LegacyOrderId
    LEFT JOIN LitigationOrderDocuments kd ON kd.LitigationCaseOrderId = m.KeptOrderId
    WHERE m.KeptOrderId IS NOT NULL
      AND od.LitigationOrderDocumentId IN (SELECT DocId FROM @Evidence)
      AND (kd.LitigationOrderDocumentId IS NULL OR kd.LitigationOrderDocumentId NOT IN (SELECT DocId FROM @Evidence))
) x
WHERE x.rn = 1;

DECLARE @Files TABLE (StoragePath nvarchar(max));
INSERT @Files SELECT d.StoragePath FROM LitigationOrderDocuments d JOIN @Handover h ON h.KeptDocId = d.LitigationOrderDocumentId WHERE d.StoragePath IS NOT NULL;

DELETE c FROM LitigationOrderClassifications c JOIN @Handover h ON h.KeptDocId = c.LitigationOrderDocumentId;
DELETE k FROM LitigationOrderChunks k JOIN @Handover h ON h.KeptDocId = k.LitigationOrderDocumentId;
DELETE d FROM LitigationOrderDocuments d JOIN @Handover h ON h.KeptDocId = d.LitigationOrderDocumentId;
SELECT 'empty kept documents replaced by the legacy evidence' AS step, @@ROWCOUNT AS rows;

UPDATE d SET d.LitigationCaseOrderId = h.KeptOrderId
FROM LitigationOrderDocuments d JOIN @Handover h ON h.LegacyDocId = d.LitigationOrderDocumentId;
SELECT 'legacy documents with evidence handed to the kept order' AS step, @@ROWCOUNT AS rows;

UPDATE k SET k.LitigationCaseId = h.KeptId, k.LitigationCaseOrderId = h.KeptOrderId
FROM LitigationOrderChunks k JOIN @Handover h ON h.LegacyDocId = k.LitigationOrderDocumentId;
SELECT 'order chunks handed over with them' AS step, @@ROWCOUNT AS rows;

UPDATE c SET c.LitigationCaseId = h.KeptId, c.LitigationCaseOrderId = h.KeptOrderId
FROM LitigationOrderClassifications c JOIN @Handover h ON h.LegacyDocId = c.LitigationOrderDocumentId;
SELECT 'order classifications handed over with them' AS step, @@ROWCOUNT AS rows;

-- 4. The remaining legacy order-level rows duplicate the kept row's (no foreign key cascades to these).
DELETE c FROM LitigationOrderClassifications c JOIN @Pairs p ON p.LegacyId = c.LitigationCaseId;
SELECT 'order classifications deleted' AS step, @@ROWCOUNT AS rows;

DELETE k FROM LitigationOrderChunks k JOIN @Pairs p ON p.LegacyId = k.LitigationCaseId;
SELECT 'order chunks deleted' AS step, @@ROWCOUNT AS rows;

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
