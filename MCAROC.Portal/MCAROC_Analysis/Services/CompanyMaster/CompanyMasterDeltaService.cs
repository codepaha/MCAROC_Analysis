using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.CompanyMaster;

public partial class CompanyMasterDeltaService : ICompanyMasterDeltaService
{
    private readonly AppDbContext _db;
    private readonly string _connectionString;
    private readonly IProxyPoolService _proxyPool;
    private readonly ISyncLockLease _lockLease;
    private readonly ISafeArchiveExtractor _archiveExtractor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CompanyMasterDeltaService> _logger;

    [GeneratedRegex(@"Company\s+Master\s+Details\s+As\s+on\s+([0-9]{1,2}(?:st|nd|rd|th)?\s+[A-Za-z]+\s+[0-9]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PortalDateRegex();

    public CompanyMasterDeltaService(
        AppDbContext db,
        IConfiguration configuration,
        IProxyPoolService proxyPool,
        ISyncLockLease lockLease,
        ILogger<CompanyMasterDeltaService> logger,
        ISafeArchiveExtractor? archiveExtractor = null)
    {
        _db = db;
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("Default") 
            ?? configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        _proxyPool = proxyPool;
        _lockLease = lockLease;
        _archiveExtractor = archiveExtractor ?? new SafeArchiveExtractor(Microsoft.Extensions.Logging.Abstractions.NullLogger<SafeArchiveExtractor>.Instance);
        _logger = logger;
    }

    public async Task<CompanyMasterSyncJob> CreateJobAsync(CompanyMasterSyncTriggerType triggerType, string? proxyAlias, CancellationToken cancellationToken = default)
    {
        // Reclaim orphaned jobs if lease expired
        var activeJobs = await _db.CompanyMasterSyncJobs
            .Where(j => j.Status == CompanyMasterSyncJobStatus.Pending
                     || j.Status == CompanyMasterSyncJobStatus.Probing
                     || j.Status == CompanyMasterSyncJobStatus.Downloading
                     || j.Status == CompanyMasterSyncJobStatus.Staging
                     || j.Status == CompanyMasterSyncJobStatus.Staged
                     || j.Status == CompanyMasterSyncJobStatus.Promoting)
            .ToListAsync(cancellationToken);

        long highestToken = 0;
        foreach (var job in activeJobs)
        {
            if (job.FencingToken > highestToken) highestToken = job.FencingToken;

            if (job.LeaseExpiresUtc.HasValue && job.LeaseExpiresUtc.Value < DateTime.UtcNow)
            {
                job.Status = CompanyMasterSyncJobStatus.FailedOrphaned;
                job.ErrorMessage = "Worker lease expired without heartbeats; reclaimed by new worker.";
                _logger.LogWarning("Reclaiming orphaned job {JobId} (expired at {Expiry})", job.JobId, job.LeaseExpiresUtc);
            }
        }

        long nextFencingToken = highestToken + 1;

        var newJob = new CompanyMasterSyncJob
        {
            FencingToken = nextFencingToken,
            TriggerType = triggerType,
            Status = CompanyMasterSyncJobStatus.Pending,
            SanitizedProxyAlias = proxyAlias ?? "Direct",
            CreatedUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow,
            LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            StartedUtc = DateTime.UtcNow
        };

        _db.CompanyMasterSyncJobs.Add(newJob);
        await _db.SaveChangesAsync(cancellationToken);

        return newJob;
    }

    public async Task UpdateJobStatusAsync(long jobId, CompanyMasterSyncJobStatus status, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var job = await _db.CompanyMasterSyncJobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            job.Status = status;
            job.LastHeartbeatUtc = DateTime.UtcNow;
            if (errorMessage != null)
            {
                job.ErrorMessage = errorMessage.Length > 2000 ? errorMessage[..2000] : errorMessage;
            }
            if (status == CompanyMasterSyncJobStatus.Completed || status == CompanyMasterSyncJobStatus.Failed ||
                status == CompanyMasterSyncJobStatus.FailedCollision || status == CompanyMasterSyncJobStatus.FailedValidation ||
                status == CompanyMasterSyncJobStatus.SkippedAlreadyPromotedChecksum || status == CompanyMasterSyncJobStatus.PreemptedByTakeover)
            {
                job.CompletedUtc = DateTime.UtcNow;
                if (job.StartedUtc.HasValue)
                {
                    job.Duration = job.CompletedUtc.Value - job.StartedUtc.Value;
                }
            }
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<long> IngestCsvFilesAsync(long syncRunId, long fencingToken, IReadOnlyList<string> csvFiles, CancellationToken cancellationToken = default)
    {
        await UpdateJobStatusAsync(syncRunId, CompanyMasterSyncJobStatus.Staging, cancellationToken: cancellationToken);

        long totalLoaded = 0;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var sha256 = SHA256.Create();

        foreach (var file in csvFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordType = DetermineRecordType(file);

            _logger.LogInformation("Ingesting {RecordType} from {File} into staging (Job: {JobId}, FencingToken: {Token})",
                recordType, Path.GetFileName(file), syncRunId, fencingToken);

            var table = CreateStagingDataTable();
            long rowsInBatch = 0;

            var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                MissingFieldFound = null,
                BadDataFound = null,
                HeaderValidated = null
            };

            using var reader = new StreamReader(file);
            using var csv = new CsvReader(reader, csvConfig);

            if (!await csv.ReadAsync()) continue;
            csv.ReadHeader();

            while (await csv.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = ParseRecord(csv, recordType);
                if (record == null) continue;

                totalLoaded++;
                rowsInBatch++;

                AddStagingRow(table, record, syncRunId, fencingToken, Path.GetFileName(file), totalLoaded);

                if (table.Rows.Count >= 10000)
                {
                    await BulkInsertStagingAsync(connection, table, cancellationToken);
                    table.Clear();
                    await _lockLease.TryExtendHeartbeatAsync(syncRunId, fencingToken, TimeSpan.FromMinutes(5), cancellationToken);
                }
            }

            if (table.Rows.Count > 0)
            {
                await BulkInsertStagingAsync(connection, table, cancellationToken);
                table.Clear();
            }
        }

        return totalLoaded;
    }

    private static CompanyMasterRecordType DetermineRecordType(string filePath)
    {
        var name = Path.GetFileName(filePath).ToUpperInvariant();
        if (name.Contains("LLP")) return CompanyMasterRecordType.Llp;
        if (name.Contains("FOREIGN") || name.Contains("FCRN")) return CompanyMasterRecordType.Foreign;
        return CompanyMasterRecordType.Company;
    }

    private static DataTable CreateStagingDataTable()
    {
        var table = new DataTable();
        table.Columns.Add("SyncRunId", typeof(long));
        table.Columns.Add("FencingToken", typeof(long));
        table.Columns.Add("BatchKey", typeof(string));
        table.Columns.Add("ValidationState", typeof(string));
        table.Columns.Add("IsPromoted", typeof(bool));
        table.Columns.Add("RowIndex", typeof(long));
        table.Columns.Add("Identifier", typeof(string));
        table.Columns.Add("RecordType", typeof(string));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("RegistrationDate", typeof(DateTime));
        table.Columns.Add("Category", typeof(string));
        table.Columns.Add("Class", typeof(string));
        table.Columns.Add("ListingStatus", typeof(string));
        table.Columns.Add("AuthorizedCapital", typeof(decimal));
        table.Columns.Add("PaidupCapital", typeof(decimal));
        table.Columns.Add("Roc", typeof(string));
        table.Columns.Add("Address", typeof(string));
        table.Columns.Add("PinCode", typeof(string));
        table.Columns.Add("State", typeof(string));
        table.Columns.Add("District", typeof(string));
        table.Columns.Add("Country", typeof(string));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("SubCategory", typeof(string));
        table.Columns.Add("IndustrialClassification", typeof(string));
        return table;
    }

    private static void AddStagingRow(DataTable table, CompanyMasterRecord r, long syncRunId, long fencingToken, string batchKey, long rowIndex)
    {
        var row = table.NewRow();
        row["SyncRunId"] = syncRunId;
        row["FencingToken"] = fencingToken;
        row["BatchKey"] = batchKey;
        row["ValidationState"] = "Unvalidated";
        row["IsPromoted"] = false;
        row["RowIndex"] = rowIndex;

        row["Identifier"] = r.Identifier;
        row["RecordType"] = r.RecordType.ToString();
        row["Name"] = r.Name;
        row["RegistrationDate"] = r.RegistrationDate.HasValue ? r.RegistrationDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        row["Category"] = (object?)r.Category ?? DBNull.Value;
        row["Class"] = (object?)r.Class ?? DBNull.Value;
        row["ListingStatus"] = (object?)r.ListingStatus ?? DBNull.Value;
        row["AuthorizedCapital"] = (object?)r.AuthorizedCapital ?? DBNull.Value;
        row["PaidupCapital"] = (object?)r.PaidupCapital ?? DBNull.Value;
        row["Roc"] = (object?)r.Roc ?? DBNull.Value;
        row["Address"] = (object?)r.Address ?? DBNull.Value;
        row["PinCode"] = (object?)r.PinCode ?? DBNull.Value;
        row["State"] = (object?)r.State ?? DBNull.Value;
        row["District"] = (object?)r.District ?? DBNull.Value;
        row["Country"] = (object?)r.Country ?? DBNull.Value;
        row["Status"] = (object?)r.Status ?? DBNull.Value;
        row["SubCategory"] = (object?)r.SubCategory ?? DBNull.Value;
        row["IndustrialClassification"] = (object?)r.IndustrialClassification ?? DBNull.Value;

        table.Rows.Add(row);
    }

    private static async Task BulkInsertStagingAsync(SqlConnection connection, DataTable table, CancellationToken cancellationToken)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = "dbo.Staging_CompanyMasterRecords",
            BulkCopyTimeout = 120,
            BatchSize = table.Rows.Count
        };

        foreach (DataColumn col in table.Columns)
        {
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }

        await bulk.WriteToServerAsync(table, cancellationToken);
    }

    private static CompanyMasterRecord? ParseRecord(CsvReader csv, CompanyMasterRecordType recordType)
    {
        string? identifier = recordType switch
        {
            CompanyMasterRecordType.Company => Field(csv, "CIN")?.Trim().ToUpperInvariant(),
            CompanyMasterRecordType.Llp => Field(csv, "LLPin")?.Trim().ToUpperInvariant(),
            CompanyMasterRecordType.Foreign => Field(csv, "FCRN")?.Trim().ToUpperInvariant(),
            _ => null
        };

        string? name = recordType switch
        {
            CompanyMasterRecordType.Company => Field(csv, "Company Name")?.Trim(),
            CompanyMasterRecordType.Llp => (Field(csv, "LLP  Name") ?? Field(csv, "LLP Name"))?.Trim(),
            CompanyMasterRecordType.Foreign => (Field(csv, "Company  Name") ?? Field(csv, "Company Name"))?.Trim(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(name))
            return null;

        return new CompanyMasterRecord
        {
            Identifier = identifier,
            RecordType = recordType,
            Name = name,
            RegistrationDate = ParseDate(Field(csv, "Company Registration Date")),
            Category = Field(csv, "Company Category")?.Trim(),
            Class = Field(csv, "Company Class")?.Trim(),
            ListingStatus = Field(csv, "Listing Status")?.Trim(),
            AuthorizedCapital = ParseDecimal(Field(csv, "Authorized Capital")),
            PaidupCapital = ParseDecimal(Field(csv, "Paidup Capital")),
            Roc = Field(csv, "Company ROC")?.Trim(),
            Address = Field(csv, "Company Address")?.Trim(),
            PinCode = Field(csv, "Pin Code")?.Trim(),
            State = Field(csv, "Company State")?.Trim(),
            District = Field(csv, "Company District")?.Trim(),
            Country = Field(csv, "Company Country")?.Trim(),
            Status = Field(csv, "Company Status")?.Trim(),
            SubCategory = Field(csv, "Company Sub Category")?.Trim(),
            IndustrialClassification = Field(csv, "Company Industrial Classification")?.Trim()
        };
    }

    private static string? Field(CsvReader csv, string name)
    {
        return csv.TryGetField<string>(name, out var val) && !string.IsNullOrWhiteSpace(val) ? val : null;
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] formats = { "dd-MM-yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy" };
        if (DateOnly.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateOnly.TryParse(raw.Trim(), out var parsed))
            return parsed;
        return null;
    }

    private static decimal? ParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = raw.Replace(",", string.Empty).Trim();
        if (decimal.TryParse(clean, NumberStyles.Number | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dec))
            return dec;
        return null;
    }

    public async Task<ValidationResult> ValidateStagingAsync(long syncRunId, long fencingToken, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // 1. Cross-Type Collision Gate (Fail-Closed)
        const string collisionSql = @"
            SELECT COUNT(1) FROM (
                SELECT Identifier
                FROM dbo.Staging_CompanyMasterRecords
                WHERE SyncRunId = @SyncRunId AND FencingToken = @FencingToken
                GROUP BY Identifier
                HAVING COUNT(DISTINCT RecordType) > 1
            ) c;";

        await using (var collisionCmd = new SqlCommand(collisionSql, connection))
        {
            collisionCmd.Parameters.AddWithValue("@SyncRunId", syncRunId);
            collisionCmd.Parameters.AddWithValue("@FencingToken", fencingToken);
            int collisions = (int)(await collisionCmd.ExecuteScalarAsync(cancellationToken) ?? 0);

            if (collisions > 0)
            {
                string msg = $"Cross-type collision detected: {collisions} identifier(s) appear under multiple distinct RecordTypes.";
                _logger.LogError(msg);
                await UpdateJobStatusAsync(syncRunId, CompanyMasterSyncJobStatus.FailedCollision, msg, cancellationToken);
                return new ValidationResult(false, 0, 0, msg, IsCollision: true);
            }
        }

        // 2. Intra-Type Deduplication & Validation Marking
        const string dedupeSql = @"
            WITH CTE AS (
                SELECT StagingId, ROW_NUMBER() OVER (PARTITION BY Identifier, RecordType ORDER BY StagingId) as rn
                FROM dbo.Staging_CompanyMasterRecords
                WHERE SyncRunId = @SyncRunId AND FencingToken = @FencingToken
            )
            UPDATE s
            SET ValidationState = CASE WHEN c.rn = 1 THEN 'Validated' ELSE 'Rejected' END
            FROM dbo.Staging_CompanyMasterRecords s
            INNER JOIN CTE c ON s.StagingId = c.StagingId;";

        await using (var dedupeCmd = new SqlCommand(dedupeSql, connection))
        {
            dedupeCmd.Parameters.AddWithValue("@SyncRunId", syncRunId);
            dedupeCmd.Parameters.AddWithValue("@FencingToken", fencingToken);
            dedupeCmd.CommandTimeout = 120;
            await dedupeCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 3. Count validated rows & duplicates
        const string countSql = @"
            SELECT 
                SUM(CASE WHEN ValidationState = 'Validated' THEN 1 ELSE 0 END) as ValidatedCount,
                SUM(CASE WHEN ValidationState = 'Rejected' THEN 1 ELSE 0 END) as DuplicateCount
            FROM dbo.Staging_CompanyMasterRecords
            WHERE SyncRunId = @SyncRunId AND FencingToken = @FencingToken;";

        int validatedCount = 0;
        int duplicateCount = 0;

        await using (var countCmd = new SqlCommand(countSql, connection))
        {
            countCmd.Parameters.AddWithValue("@SyncRunId", syncRunId);
            countCmd.Parameters.AddWithValue("@FencingToken", fencingToken);
            await using var reader = await countCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                validatedCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                duplicateCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            }
        }

        _logger.LogInformation("Staging validated: {Valid:N0} valid rows, {Dups:N0} intra-type duplicates discarded.",
            validatedCount, duplicateCount);

        await UpdateJobStatusAsync(syncRunId, CompanyMasterSyncJobStatus.Staged, cancellationToken: cancellationToken);
        return new ValidationResult(true, validatedCount, duplicateCount, null);
    }

    public async Task<PromotionMetricsResult> PromoteStagedDeltaAsync(long syncRunId, long fencingToken, int batchSize = 4000, CancellationToken cancellationToken = default)
    {
        await UpdateJobStatusAsync(syncRunId, CompanyMasterSyncJobStatus.Promoting, cancellationToken: cancellationToken);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        int totalUpdated = 0;
        int totalInserted = 0;

        const string batchSql = @"
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            -- 0. Atomically verify worker fencing token ownership, job status, and refresh heartbeat
            UPDATE dbo.CompanyMasterSyncJobs
            SET LastHeartbeatUtc = SYSUTCDATETIME(),
                LeaseExpiresUtc = DATEADD(minute, 5, SYSUTCDATETIME())
            WHERE JobId = @SyncRunId
              AND FencingToken = @FencingToken
              AND Status IN ('Staged', 'Promoting')
              AND (LeaseExpiresUtc IS NULL OR LeaseExpiresUtc >= SYSUTCDATETIME());

            IF @@ROWCOUNT = 0
            BEGIN
                ROLLBACK TRANSACTION;
                RAISERROR('Fencing check failed: Job %I64d has been preempted, superseded, expired, or is no longer in promotable status.', 16, 1, @SyncRunId);
                RETURN;
            END

            DECLARE @BatchIds TABLE (StagingId bigint PRIMARY KEY);

            -- 1. Select deterministic batch of StagingIds
            INSERT INTO @BatchIds (StagingId)
            SELECT TOP (@BatchSize) s.StagingId
            FROM dbo.Staging_CompanyMasterRecords s
            WHERE s.SyncRunId = @SyncRunId 
              AND s.FencingToken = @FencingToken 
              AND s.ValidationState = 'Validated'
              AND s.IsPromoted = 0
            ORDER BY s.StagingId;

            DECLARE @SelectedCount int = @@ROWCOUNT;

            IF @SelectedCount = 0
            BEGIN
                COMMIT TRANSACTION;
                SELECT 0 as SelectedCount, 0 as UpdatedCount, 0 as InsertedCount;
                RETURN;
            END

            -- 2. Identify rows requiring UPDATE in this batch
            DECLARE @UpdatedIds TABLE (StagingId bigint PRIMARY KEY);

            INSERT INTO @UpdatedIds (StagingId)
            SELECT s.StagingId
            FROM dbo.Staging_CompanyMasterRecords s
            INNER JOIN dbo.CompanyMasterRecords c
                ON c.Identifier = s.Identifier AND c.RecordType = s.RecordType
            INNER JOIN @BatchIds b
                ON s.StagingId = b.StagingId
            WHERE (
                 ISNULL(c.Name, '') <> ISNULL(s.Name, '')
              OR ISNULL(c.RegistrationDate, '1900-01-01') <> ISNULL(s.RegistrationDate, '1900-01-01')
              OR ISNULL(c.Category, '') <> ISNULL(s.Category, '')
              OR ISNULL(c.Class, '') <> ISNULL(s.Class, '')
              OR ISNULL(c.ListingStatus, '') <> ISNULL(s.ListingStatus, '')
              OR ISNULL(c.AuthorizedCapital, -1) <> ISNULL(s.AuthorizedCapital, -1)
              OR ISNULL(c.PaidupCapital, -1) <> ISNULL(s.PaidupCapital, -1)
              OR ISNULL(c.Roc, '') <> ISNULL(s.Roc, '')
              OR ISNULL(c.Address, '') <> ISNULL(s.Address, '')
              OR ISNULL(c.PinCode, '') <> ISNULL(s.PinCode, '')
              OR ISNULL(c.State, '') <> ISNULL(s.State, '')
              OR ISNULL(c.District, '') <> ISNULL(s.District, '')
              OR ISNULL(c.Country, '') <> ISNULL(s.Country, '')
              OR ISNULL(c.Status, '') <> ISNULL(s.Status, '')
              OR ISNULL(c.SubCategory, '') <> ISNULL(s.SubCategory, '')
              OR ISNULL(c.IndustrialClassification, '') <> ISNULL(s.IndustrialClassification, '')
            );

            UPDATE c
            SET c.Name = s.Name,
                c.RegistrationDate = s.RegistrationDate,
                c.Category = s.Category,
                c.Class = s.Class,
                c.ListingStatus = s.ListingStatus,
                c.AuthorizedCapital = s.AuthorizedCapital,
                c.PaidupCapital = s.PaidupCapital,
                c.Roc = s.Roc,
                c.Address = s.Address,
                c.PinCode = s.PinCode,
                c.State = s.State,
                c.District = s.District,
                c.Country = s.Country,
                c.Status = s.Status,
                c.SubCategory = s.SubCategory,
                c.IndustrialClassification = s.IndustrialClassification
            FROM dbo.CompanyMasterRecords c
            INNER JOIN dbo.Staging_CompanyMasterRecords s
                ON c.Identifier = s.Identifier AND c.RecordType = s.RecordType
            INNER JOIN @UpdatedIds u
                ON s.StagingId = u.StagingId;

            DECLARE @UpdatedCount int = @@ROWCOUNT;

            -- 3. Identify rows requiring INSERT in this batch
            DECLARE @InsertedIds TABLE (StagingId bigint PRIMARY KEY);

            INSERT INTO @InsertedIds (StagingId)
            SELECT s.StagingId
            FROM dbo.Staging_CompanyMasterRecords s
            INNER JOIN @BatchIds b
                ON s.StagingId = b.StagingId
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.CompanyMasterRecords c
                WHERE c.Identifier = s.Identifier
            );

            INSERT INTO dbo.CompanyMasterRecords (
                Identifier, RecordType, Name, RegistrationDate, Category, Class, ListingStatus,
                AuthorizedCapital, PaidupCapital, Roc, Address, PinCode, State, District,
                Country, Status, SubCategory, IndustrialClassification
            )
            SELECT
                s.Identifier, s.RecordType, s.Name, s.RegistrationDate, s.Category, s.Class, s.ListingStatus,
                s.AuthorizedCapital, s.PaidupCapital, s.Roc, s.Address, s.PinCode, s.State, s.District,
                s.Country, s.Status, s.SubCategory, s.IndustrialClassification
            FROM dbo.Staging_CompanyMasterRecords s
            INNER JOIN @InsertedIds i
                ON s.StagingId = i.StagingId;

            DECLARE @InsertedCount int = @@ROWCOUNT;

            -- 4. Mark ONLY and EXACTLY this batch of StagingIds as promoted, tagging their exact transition state
            UPDATE s
            SET s.IsPromoted = 1,
                s.ValidationState = CASE 
                    WHEN u.StagingId IS NOT NULL THEN 'Updated'
                    WHEN i.StagingId IS NOT NULL THEN 'Inserted'
                    ELSE 'Unchanged'
                END
            FROM dbo.Staging_CompanyMasterRecords s
            INNER JOIN @BatchIds b
                ON s.StagingId = b.StagingId
            LEFT JOIN @UpdatedIds u
                ON s.StagingId = u.StagingId
            LEFT JOIN @InsertedIds i
                ON s.StagingId = i.StagingId;

            COMMIT TRANSACTION;

            SELECT @SelectedCount as SelectedCount, @UpdatedCount as UpdatedCount, @InsertedCount as InsertedCount;
        ";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var cmd = new SqlCommand(batchSql, connection) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@SyncRunId", syncRunId);
            cmd.Parameters.AddWithValue("@FencingToken", fencingToken);
            cmd.Parameters.AddWithValue("@BatchSize", batchSize);

            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    int selected = reader.GetInt32(0);
                    int updated = reader.GetInt32(1);
                    int inserted = reader.GetInt32(2);

                    if (selected == 0)
                    {
                        break; // all batches promoted
                    }

                    totalUpdated += updated;
                    totalInserted += inserted;
                }
            }
            catch (SqlException ex) when (ex.Message.Contains("Fencing check failed"))
            {
                _logger.LogError("Promotion aborted by atomic fencing check preemption for Job {JobId}", syncRunId);
                await UpdateJobStatusAsync(syncRunId, CompanyMasterSyncJobStatus.PreemptedByTakeover, ex.Message, cancellationToken);
                throw;
            }
        }

        // Record metrics grouped by RecordType from staging before cleaning up
        const string metricsSql = @"
            SELECT 
                RecordType,
                COUNT(1) as TotalSourceRows,
                SUM(CASE WHEN ValidationState = 'Inserted' THEN 1 ELSE 0 END) as NewRowsAdded,
                SUM(CASE WHEN ValidationState = 'Updated' THEN 1 ELSE 0 END) as ExistingRowsUpdated,
                SUM(CASE WHEN ValidationState = 'Unchanged' THEN 1 ELSE 0 END) as UnchangedRowsSkipped,
                SUM(CASE WHEN ValidationState = 'Rejected' THEN 1 ELSE 0 END) as CorruptedRowsSkipped
            FROM dbo.Staging_CompanyMasterRecords
            WHERE SyncRunId = @SyncRunId AND FencingToken = @FencingToken
            GROUP BY RecordType;";

        var metricsByRecordType = new Dictionary<CompanyMasterRecordType, (int Added, int Updated, int Unchanged)>();
        var metricsList = new List<CompanyMasterSyncMetric>();
        int grandTotalInserted = 0;
        int grandTotalUpdated = 0;
        int grandTotalUnchanged = 0;

        await using (var metricsCmd = new SqlCommand(metricsSql, connection))
        {
            metricsCmd.Parameters.AddWithValue("@SyncRunId", syncRunId);
            metricsCmd.Parameters.AddWithValue("@FencingToken", fencingToken);

            await using var reader = await metricsCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string rawType = reader.GetString(0);
                if (!Enum.TryParse<CompanyMasterRecordType>(rawType, true, out var recordType))
                {
                    continue;
                }

                int totalSource = reader.GetInt32(1);
                int added = reader.GetInt32(2);
                int updated = reader.GetInt32(3);
                int unchanged = reader.GetInt32(4);
                int corrupted = reader.GetInt32(5);

                var metric = new CompanyMasterSyncMetric
                {
                    JobId = syncRunId,
                    RecordType = recordType,
                    TotalSourceRows = totalSource,
                    NewRowsAdded = added,
                    ExistingRowsUpdated = updated,
                    UnchangedRowsSkipped = unchanged,
                    CorruptedRowsSkipped = corrupted
                };

                metricsList.Add(metric);
                metricsByRecordType[recordType] = (added, updated, unchanged);

                grandTotalInserted += added;
                grandTotalUpdated += updated;
                grandTotalUnchanged += unchanged;
            }
        }

        foreach (var recordType in Enum.GetValues<CompanyMasterRecordType>())
        {
            if (!metricsByRecordType.ContainsKey(recordType))
            {
                var metric = new CompanyMasterSyncMetric
                {
                    JobId = syncRunId,
                    RecordType = recordType,
                    TotalSourceRows = 0,
                    NewRowsAdded = 0,
                    ExistingRowsUpdated = 0,
                    UnchangedRowsSkipped = 0,
                    CorruptedRowsSkipped = 0
                };
                metricsList.Add(metric);
                metricsByRecordType[recordType] = (0, 0, 0);
            }
        }

        _db.CompanyMasterSyncMetrics.AddRange(metricsList);
        await _db.SaveChangesAsync(cancellationToken);

        await UpdateJobStatusAsync(syncRunId, CompanyMasterSyncJobStatus.Completed, cancellationToken: cancellationToken);
        await CleanStagingAsync(syncRunId, cancellationToken);

        return new PromotionMetricsResult(grandTotalUpdated, grandTotalInserted, grandTotalUnchanged, metricsByRecordType);
    }

    public async Task CleanStagingAsync(long syncRunId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = "DELETE FROM dbo.Staging_CompanyMasterRecords WHERE SyncRunId = @SyncRunId;";
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@SyncRunId", syncRunId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DateOnly?> ProbePortalSnapshotDateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var node = _proxyPool.GetNextHealthyNode();
            SocketsHttpHandler handler = new();
            if (node != null)
            {
                handler.Proxy = new System.Net.WebProxy(node.Endpoint)
                {
                    Credentials = node.Username != null ? new System.Net.NetworkCredential(node.Username, node.Password) : null
                };
            }

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var html = await client.GetStringAsync("https://mcacdm.nic.in/company-master-details", cancellationToken);
            var match = PortalDateRegex().Match(html);
            if (match.Success)
            {
                var rawDate = match.Groups[1].Value;
                // remove ordinal suffixes (st, nd, rd, th)
                var cleanDate = Regex.Replace(rawDate, @"(?<=\d)(st|nd|rd|th)", string.Empty);
                if (DateOnly.TryParse(cleanDate, CultureInfo.InvariantCulture, out var d))
                {
                    return d;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe MCA CDM portal date.");
        }
        return null;
    }

    public async Task<bool> RenewLeaseHeartbeatAsync(long jobId, long fencingToken, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = @"
            UPDATE dbo.CompanyMasterSyncJobs
            SET LastHeartbeatUtc = SYSUTCDATETIME(),
                LeaseExpiresUtc = DATEADD(minute, 5, SYSUTCDATETIME())
            WHERE JobId = @SyncRunId
              AND FencingToken = @FencingToken
              AND Status IN ('Probing', 'Downloading', 'Staging');";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SyncRunId", jobId);
        cmd.Parameters.AddWithValue("@FencingToken", fencingToken);

        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<PromotionMetricsResult?> ExecuteAutomatedSyncAsync(
        long jobId, 
        long fencingToken, 
        Stream? archiveStream = null, 
        CancellationToken cancellationToken = default)
    {
        string tempExtractDir = Path.Combine(Path.GetTempPath(), $"mca_auto_{Guid.NewGuid():N}");
        Stream? localStreamToDispose = null;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;

        try
        {
            await UpdateJobStatusAsync(jobId, CompanyMasterSyncJobStatus.Downloading, cancellationToken: cancellationToken);

            // Heartbeat loop during long download & staging
            heartbeatTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (await timer.WaitForNextTickAsync(heartbeatCts.Token))
                        {
                            bool ok = await RenewLeaseHeartbeatAsync(jobId, fencingToken, heartbeatCts.Token);
                            if (!ok)
                            {
                                _logger.LogWarning("Heartbeat check failed for Job {JobId}; worker preemption detected.", jobId);
                                heartbeatCts.Cancel();
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Heartbeat renewal error for Job {JobId}", jobId);
                    }
                }
            }, cancellationToken);

            Stream streamToUse;
            if (archiveStream != null)
            {
                streamToUse = archiveStream;
            }
            else
            {
                string? dropPath = _configuration["CompanyMasterSync:ArchiveDropPath"];
                if (!string.IsNullOrWhiteSpace(dropPath) && File.Exists(dropPath))
                {
                    _logger.LogInformation("Using configured archive drop file at {Path}", dropPath);
                    localStreamToDispose = File.OpenRead(dropPath);
                    streamToUse = localStreamToDispose;
                }
                else
                {
                    string? downloadUrl = _configuration["CompanyMasterSync:ArchiveDownloadUrl"] ?? "https://mcacdm.nic.in/company-master-details/download";
                    _logger.LogInformation("Downloading archive from {Url}...", downloadUrl);

                    var node = _proxyPool.GetNextHealthyNode();
                    SocketsHttpHandler handler = new();
                    if (node != null)
                    {
                        handler.Proxy = new System.Net.WebProxy(node.Endpoint)
                        {
                            Credentials = node.Username != null ? new System.Net.NetworkCredential(node.Username, node.Password) : null
                        };
                    }

                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                    var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    var memoryStream = new MemoryStream();
                    await response.Content.CopyToAsync(memoryStream, cancellationToken);
                    memoryStream.Position = 0;
                    localStreamToDispose = memoryStream;
                    streamToUse = localStreamToDispose;
                }
            }

            heartbeatCts.Token.ThrowIfCancellationRequested();

            var extractor = _archiveExtractor ?? new SafeArchiveExtractor(Microsoft.Extensions.Logging.Abstractions.NullLogger<SafeArchiveExtractor>.Instance);
            var extractedFiles = await extractor.ExtractSafelyAsync(streamToUse, tempExtractDir, cancellationToken);
            var csvFiles = extractedFiles.Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).ToList();

            if (csvFiles.Count == 0)
            {
                await UpdateJobStatusAsync(jobId, CompanyMasterSyncJobStatus.Failed, "Archive contained zero CSV files.", cancellationToken);
                throw new InvalidOperationException("Archive contained zero CSV files.");
            }

            await IngestCsvFilesAsync(jobId, fencingToken, csvFiles, cancellationToken);

            var validation = await ValidateStagingAsync(jobId, fencingToken, cancellationToken);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Automated sync staging validation failed for Job {JobId}: {Error}", jobId, validation.ErrorMessage);
                return null;
            }

            heartbeatCts.Cancel();
            if (heartbeatTask != null)
            {
                try { await heartbeatTask; } catch { }
            }

            cancellationToken.ThrowIfCancellationRequested();

            int batchSize = _configuration.GetValue<int>("CompanyMasterSync:PromotionBatchSize", 4000);
            var metrics = await PromoteStagedDeltaAsync(jobId, fencingToken, batchSize, cancellationToken);
            return metrics;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Automated sync pipeline failed for Job {JobId}", jobId);
            await UpdateJobStatusAsync(jobId, CompanyMasterSyncJobStatus.Failed, ex.Message, cancellationToken);
            throw;
        }
        finally
        {
            heartbeatCts.Cancel();
            if (heartbeatTask != null)
            {
                try { await heartbeatTask; } catch { }
            }

            if (localStreamToDispose != null)
            {
                await localStreamToDispose.DisposeAsync();
            }

            try
            {
                if (Directory.Exists(tempExtractDir))
                {
                    Directory.Delete(tempExtractDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean temp extraction directory {Dir}", tempExtractDir);
            }
        }
    }
}
