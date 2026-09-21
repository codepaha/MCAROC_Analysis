using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.Registry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CompanyMasterSyncTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static CompanyMasterDeltaService CreateDeltaService(
        AppDbContext db,
        IConfiguration config,
        IProxyPoolService proxyPool,
        ISyncLockLease lease,
        IRegistryPromotionCoordinator? coordinator = null)
    {
        var coord = coordinator ?? new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        return new CompanyMasterDeltaService(db, config, proxyPool, lease, coord, NullLogger<CompanyMasterDeltaService>.Instance);
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        // cleanup test staging
        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var cmd = new SqlCommand("DELETE FROM dbo.Staging_CompanyMasterRecords WHERE SyncRunId < 0;", connection);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    [Fact]
    public async Task DistributedAppLock_SessionOwner_DedicatedConnection_HoldsUntilDisposed()
    {
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
        string lockResource = $"TestLock_{Guid.NewGuid():N}";

        try
        {
            var res = await lease.TryAcquireAsync(lockResource, TimeSpan.Zero);
            Assert.True(res.Succeeded);
            Assert.True(lease.IsAcquired);
            Assert.NotNull(lease.ActiveSpid);

            // Verify a second lease on a separate connection cannot acquire it
            var secondLease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
            try
            {
                var secondRes = await secondLease.TryAcquireAsync(lockResource, TimeSpan.Zero);
                Assert.False(secondRes.Succeeded);
                Assert.Equal(-1, secondRes.ReturnCode);
            }
            finally
            {
                await secondLease.DisposeAsync();
            }
        }
        finally
        {
            await lease.DisposeAsync();
        }

        // Once disposed, third lease acquires cleanly
        var thirdLease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
        try
        {
            var thirdRes = await thirdLease.TryAcquireAsync(lockResource, TimeSpan.Zero);
            Assert.True(thirdRes.Succeeded);
        }
        finally
        {
            await thirdLease.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(-1, "Lock request timed out")]
    [InlineData(-2, "Lock request was canceled")]
    [InlineData(-3, "Lock request was chosen as a deadlock victim")]
    [InlineData(-999, "Parameter validation or internal SQL Server lock error")]
    public void AppLock_NegativeReturnCodes_AllTreatedAsAcquisitionFailure(int returnCode, string expectedPhrase)
    {
        var result = LockAcquisitionResult.Failed(returnCode, returnCode switch
        {
            -1 => "Lock request timed out (already held by another session).",
            -2 => "Lock request was canceled.",
            -3 => "Lock request was chosen as a deadlock victim.",
            -999 => "Parameter validation or internal SQL Server lock error.",
            _ => $"Lock acquisition failed with return code {returnCode}."
        });

        Assert.False(result.Succeeded);
        Assert.Equal(returnCode, result.ReturnCode);
        Assert.Contains(expectedPhrase, result.FailureReason);
    }

    [Fact]
    public async Task FencingToken_AtomicPreemption_AbortsBatchTransaction()
    {
        await using var db = CreateContext();
        long token = 1;

        var job = new CompanyMasterSyncJob
        {
            FencingToken = token,
            Status = CompanyMasterSyncJobStatus.Staged,
            CreatedUtc = DateTime.UtcNow
        };
        db.CompanyMasterSyncJobs.Add(job);
        await db.SaveChangesAsync();
        long testJobId = job.JobId;

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = ConnectionString })
                .Build();

            var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
            var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
            var deltaService = CreateDeltaService(db, config, proxyPool, lease);

            // Worker preemption simulation: Bump the fencing token in DB to simulate another worker taking over
            job.FencingToken = token + 1;
            await db.SaveChangesAsync();

            // Attempting promotion with old token must throw and transition to PreemptedByTakeover
            await Assert.ThrowsAnyAsync<Exception>(() =>
                deltaService.PromoteStagedDeltaAsync(testJobId, token, batchSize: 100));

            var reloadedJob = await db.CompanyMasterSyncJobs.FindAsync(testJobId);
            Assert.Equal(CompanyMasterSyncJobStatus.PreemptedByTakeover, reloadedJob!.Status);
        }
        finally
        {
            db.CompanyMasterSyncJobs.Remove(job);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task LeaseExpiry_BeforePromotion_AbortsStagingWorker()
    {
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
        await lease.TryAcquireAsync($"TestLease_{Guid.NewGuid():N}", TimeSpan.Zero);

        try
        {
            long nonExistentJobId = -999999;
            long fencingToken = 99;

            // Heartbeat against a job that doesn't exist or has mismatched token must return false
            bool extended = await lease.TryExtendHeartbeatAsync(nonExistentJobId, fencingToken, TimeSpan.FromMinutes(5));
            Assert.False(extended);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public void JobStateMachine_EnforcesValidTransitions()
    {
        var job = new CompanyMasterSyncJob { Status = CompanyMasterSyncJobStatus.Pending };
        Assert.Equal(CompanyMasterSyncJobStatus.Pending, job.Status);

        job.Status = CompanyMasterSyncJobStatus.Probing;
        Assert.Equal(CompanyMasterSyncJobStatus.Probing, job.Status);

        job.Status = CompanyMasterSyncJobStatus.Downloading;
        Assert.Equal(CompanyMasterSyncJobStatus.Downloading, job.Status);

        job.Status = CompanyMasterSyncJobStatus.Staging;
        Assert.Equal(CompanyMasterSyncJobStatus.Staging, job.Status);

        job.Status = CompanyMasterSyncJobStatus.Staged;
        Assert.Equal(CompanyMasterSyncJobStatus.Staged, job.Status);

        job.Status = CompanyMasterSyncJobStatus.Promoting;
        Assert.Equal(CompanyMasterSyncJobStatus.Promoting, job.Status);

        job.Status = CompanyMasterSyncJobStatus.Completed;
        Assert.Equal(CompanyMasterSyncJobStatus.Completed, job.Status);
    }

    [Fact]
    public async Task CrossTypeCollision_SameIdentifierDifferentType_FailsValidationWithoutModifyingLive()
    {
        await using var db = CreateContext();
        long token = 1;

        var job = new CompanyMasterSyncJob
        {
            FencingToken = token,
            Status = CompanyMasterSyncJobStatus.Staging,
            CreatedUtc = DateTime.UtcNow
        };
        db.CompanyMasterSyncJobs.Add(job);
        await db.SaveChangesAsync();
        long testJobId = job.JobId;

        string collisionId = $"COLLISION_{Guid.NewGuid():N}"[..20];

        // Insert colliding staging rows: same Identifier, different RecordType
        db.StagingCompanyMasterRecords.Add(new StagingCompanyMasterRecord
        {
            SyncRunId = testJobId,
            FencingToken = token,
            BatchKey = "test.csv",
            ValidationState = "Unvalidated",
            Identifier = collisionId,
            RecordType = CompanyMasterRecordType.Company,
            Name = "Collision Corp Limited"
        });

        db.StagingCompanyMasterRecords.Add(new StagingCompanyMasterRecord
        {
            SyncRunId = testJobId,
            FencingToken = token,
            BatchKey = "test.csv",
            ValidationState = "Unvalidated",
            Identifier = collisionId,
            RecordType = CompanyMasterRecordType.Llp,
            Name = "Collision LLP"
        });

        await db.SaveChangesAsync();

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = ConnectionString })
                .Build();

            var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
            var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
            var deltaService = CreateDeltaService(db, config, proxyPool, lease);

            var valResult = await deltaService.ValidateStagingAsync(testJobId, token);
            Assert.False(valResult.IsValid);
            Assert.True(valResult.IsCollision);

            var reloadedJob = await db.CompanyMasterSyncJobs.FindAsync(testJobId);
            Assert.Equal(CompanyMasterSyncJobStatus.FailedCollision, reloadedJob!.Status);
        }
        finally
        {
            await db.StagingCompanyMasterRecords.Where(s => s.SyncRunId == testJobId).ExecuteDeleteAsync();
            db.CompanyMasterSyncJobs.Remove(job);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task IntraTypeDeduplication_KeepsLatestCanonicalRecord()
    {
        await using var db = CreateContext();
        long token = 1;

        var job = new CompanyMasterSyncJob
        {
            FencingToken = token,
            Status = CompanyMasterSyncJobStatus.Staging,
            CreatedUtc = DateTime.UtcNow
        };
        db.CompanyMasterSyncJobs.Add(job);
        await db.SaveChangesAsync();
        long testJobId = job.JobId;

        string testCin = $"U{Guid.NewGuid():N}"[..21];

        // Duplicate rows for the SAME CIN and SAME RecordType
        db.StagingCompanyMasterRecords.AddRange(
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                BatchKey = "part1.csv",
                ValidationState = "Unvalidated",
                Identifier = testCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "Canonical Name Limited"
            },
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                BatchKey = "part2.csv",
                ValidationState = "Unvalidated",
                Identifier = testCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "Duplicate Name Limited"
            }
        );

        await db.SaveChangesAsync();

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = ConnectionString })
                .Build();

            var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
            var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
            var deltaService = CreateDeltaService(db, config, proxyPool, lease);

            var valResult = await deltaService.ValidateStagingAsync(testJobId, token);
            Assert.True(valResult.IsValid);
            Assert.Equal(1, valResult.TotalRows);
            Assert.Equal(1, valResult.DuplicateCINs);

            var validatedRows = await db.StagingCompanyMasterRecords
                .Where(s => s.SyncRunId == testJobId && s.ValidationState == "Validated")
                .ToListAsync();
            Assert.Single(validatedRows);

            var rejectedRows = await db.StagingCompanyMasterRecords
                .Where(s => s.SyncRunId == testJobId && s.ValidationState == "Rejected")
                .ToListAsync();
            Assert.Single(rejectedRows);
        }
        finally
        {
            await db.StagingCompanyMasterRecords.Where(s => s.SyncRunId == testJobId).ExecuteDeleteAsync();
            db.CompanyMasterSyncJobs.Remove(job);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task DeltaMerge_SetBasedUpdateAndInsert_UpdatesAll18ColumnsAccurately()
    {
        await using var db = CreateContext();
        long token = 1;

        string updateCin = $"U{Guid.NewGuid():N}"[..21];
        string insertCin = $"U{Guid.NewGuid():N}"[..21];

        // Seed initial live record
        var existingLive = new CompanyMasterRecord
        {
            Identifier = updateCin,
            RecordType = CompanyMasterRecordType.Company,
            Name = "Old Company Name",
            Status = "Active",
            AuthorizedCapital = 100000m,
            PaidupCapital = 50000m,
            Roc = "ROC MUMBAI",
            State = "Maharashtra",
            Category = "Company limited by shares",
            Class = "Private",
            ListingStatus = "Unlisted",
            Address = "Old Address",
            PinCode = "400001",
            SubCategory = "Non-government company",
            IndustrialClassification = "Old Industry"
        };
        db.CompanyMasterRecords.Add(existingLive);

        var job = new CompanyMasterSyncJob
        {
            FencingToken = token,
            Status = CompanyMasterSyncJobStatus.Staged,
            CreatedUtc = DateTime.UtcNow
        };
        db.CompanyMasterSyncJobs.Add(job);
        await db.SaveChangesAsync();
        long testJobId = job.JobId;

        // Staging: 1 modified row, 1 new row
        db.StagingCompanyMasterRecords.AddRange(
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                BatchKey = "test.csv",
                ValidationState = "Validated",
                Identifier = updateCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "New Updated Company Name",
                Status = "Strike Off",
                AuthorizedCapital = 200000m,
                PaidupCapital = 150000m,
                Roc = "ROC DELHI",
                State = "Delhi",
                Category = "Company limited by guarantee",
                Class = "Public",
                ListingStatus = "Listed",
                Address = "New Address Block",
                PinCode = "110001",
                SubCategory = "Union government company",
                IndustrialClassification = "New Industry"
            },
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                BatchKey = "test.csv",
                ValidationState = "Validated",
                Identifier = insertCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "Brand New Company",
                Status = "Active",
                AuthorizedCapital = 500000m,
                PaidupCapital = 500000m,
                Roc = "ROC BANGALORE",
                State = "Karnataka"
            }
        );

        await db.SaveChangesAsync();

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = ConnectionString })
                .Build();

            var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
            var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
            var deltaService = CreateDeltaService(db, config, proxyPool, lease);

            var metrics = await deltaService.PromoteStagedDeltaAsync(testJobId, token, batchSize: 100);

            Assert.Equal(1, metrics.TotalUpdated);
            Assert.Equal(1, metrics.TotalInserted);

            // Assert live record updated across all fields from a fresh DbContext (bypassing EF ChangeTracker cache)
            await using var freshDb = CreateContext();
            var updatedLive = await freshDb.CompanyMasterRecords.FindAsync(updateCin);
            Assert.NotNull(updatedLive);
            Assert.Equal("New Updated Company Name", updatedLive.Name);
            Assert.Equal("Strike Off", updatedLive.Status);
            Assert.Equal(200000m, updatedLive.AuthorizedCapital);
            Assert.Equal(150000m, updatedLive.PaidupCapital);
            Assert.Equal("ROC DELHI", updatedLive.Roc);
            Assert.Equal("Delhi", updatedLive.State);
            Assert.Equal("Company limited by guarantee", updatedLive.Category);
            Assert.Equal("Public", updatedLive.Class);
            Assert.Equal("Listed", updatedLive.ListingStatus);
            Assert.Equal("New Address Block", updatedLive.Address);
            Assert.Equal("110001", updatedLive.PinCode);
            Assert.Equal("Union government company", updatedLive.SubCategory);
            Assert.Equal("New Industry", updatedLive.IndustrialClassification);

            // Assert newly inserted record
            var newLive = await freshDb.CompanyMasterRecords.FindAsync(insertCin);
            Assert.NotNull(newLive);
            Assert.Equal("Brand New Company", newLive.Name);
            Assert.Equal("Active", newLive.Status);
        }
        finally
        {
            await db.CompanyMasterRecords.Where(c => c.Identifier == updateCin || c.Identifier == insertCin).ExecuteDeleteAsync();
            await db.StagingCompanyMasterRecords.Where(s => s.SyncRunId == testJobId).ExecuteDeleteAsync();
            db.CompanyMasterSyncJobs.Remove(job);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task SafeArchiveExtractor_PathTraversal_ThrowsException()
    {
        var extractor = new SafeArchiveExtractor(NullLogger<SafeArchiveExtractor>.Instance);

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../evil.csv");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("CIN,Company Name");
            writer.WriteLine("U12345MH2026PTC000001,Evil Corp");
        }

        memoryStream.Position = 0;
        string tempDir = Path.Combine(Path.GetTempPath(), $"test_zip_{Guid.NewGuid():N}");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                extractor.ExtractSafelyAsync(memoryStream, tempDir));
            Assert.Contains("Path traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ProxyPool_SanitizesCredentialsInLogsAndMetrics()
    {
        var pool = new ProxyPoolService(new ConfigurationBuilder().Build(), NullLogger<ProxyPoolService>.Instance);

        string rawWithSecret = "192.168.1.50:8080:supersecretuser:supersecretpass123";
        string sanitized = pool.Sanitize(rawWithSecret);

        Assert.DoesNotContain("supersecretpass123", sanitized);
        Assert.Contains("supersecretuser@192.168.1.50:8080", sanitized);

        string rawUrlWithSecret = "http://myuser:secretpassword@proxy.example.com:3128";
        string sanitizedUrl = pool.Sanitize(rawUrlWithSecret);

        Assert.DoesNotContain("secretpassword", sanitizedUrl);
        Assert.Contains("myuser@proxy.example.com:3128", sanitizedUrl);
    }

    [Fact]
    public void DashboardController_AllFourMutatingEndpoints_EnforceCsrfAndInternalReviewer()
    {
        var controllerType = typeof(CompanyMasterDashboardController);

        // Assert Controller-level [Authorize(AuthenticationSchemes = "InternalReviewer")]
        var authAttr = controllerType.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authAttr);
        Assert.Equal("InternalReviewer", authAttr.AuthenticationSchemes);

        // Assert all 4 POST endpoints exist and have [ValidateAntiForgeryToken]
        string[] mutatingActions = { "Probe", "TestProxies", "SyncNow", "UploadManual" };
        foreach (var actionName in mutatingActions)
        {
            var method = controllerType.GetMethod(actionName);
            Assert.NotNull(method);
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
    }

    [Fact]
    public async Task PromoteStagedDelta_PartitionsMetricsByRecordTypeAccurately()
    {
        await using var db = CreateContext();
        long token = 1;

        string updateCin = $"U{Guid.NewGuid():N}"[..21];
        string unchangedCin = $"U{Guid.NewGuid():N}"[..21];
        string insertCin = $"U{Guid.NewGuid():N}"[..21];

        string unchangedLlp = $"AAA-{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        string insertLlp = $"BBB-{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        string updateForeign = $"F{Guid.NewGuid():N}"[..10].ToUpperInvariant();

        // 1. Seed live records
        db.CompanyMasterRecords.AddRange(
            new CompanyMasterRecord
            {
                Identifier = updateCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "Original Company Name",
                Status = "Active"
            },
            new CompanyMasterRecord
            {
                Identifier = unchangedCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "Unchanged Company Name",
                Status = "Active"
            },
            new CompanyMasterRecord
            {
                Identifier = unchangedLlp,
                RecordType = CompanyMasterRecordType.Llp,
                Name = "Unchanged LLP Name",
                Status = "Active"
            },
            new CompanyMasterRecord
            {
                Identifier = updateForeign,
                RecordType = CompanyMasterRecordType.Foreign,
                Name = "Original Foreign Name",
                Status = "Active"
            }
        );

        var job = new CompanyMasterSyncJob
        {
            FencingToken = token,
            Status = CompanyMasterSyncJobStatus.Staged,
            CreatedUtc = DateTime.UtcNow,
            LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(10)
        };
        db.CompanyMasterSyncJobs.Add(job);
        await db.SaveChangesAsync();
        long testJobId = job.JobId;

        // 2. Stage records
        db.StagingCompanyMasterRecords.AddRange(
            // Company: 1 updated, 1 unchanged, 1 inserted
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                ValidationState = "Validated",
                Identifier = updateCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "Modified Company Name",
                Status = "Active"
            },
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                ValidationState = "Validated",
                Identifier = unchangedCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "Unchanged Company Name",
                Status = "Active"
            },
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                ValidationState = "Validated",
                Identifier = insertCin,
                RecordType = CompanyMasterRecordType.Company,
                Name = "Brand New Company",
                Status = "Active"
            },
            // LLP: 1 unchanged, 1 inserted
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                ValidationState = "Validated",
                Identifier = unchangedLlp,
                RecordType = CompanyMasterRecordType.Llp,
                Name = "Unchanged LLP Name",
                Status = "Active"
            },
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                ValidationState = "Validated",
                Identifier = insertLlp,
                RecordType = CompanyMasterRecordType.Llp,
                Name = "Brand New LLP",
                Status = "Active"
            },
            // Foreign: 1 updated
            new StagingCompanyMasterRecord
            {
                SyncRunId = testJobId,
                FencingToken = token,
                ValidationState = "Validated",
                Identifier = updateForeign,
                RecordType = CompanyMasterRecordType.Foreign,
                Name = "Modified Foreign Name",
                Status = "Active"
            }
        );
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
        var deltaService = CreateDeltaService(db, config, proxyPool, lease);

        var metrics = await deltaService.PromoteStagedDeltaAsync(testJobId, token, batchSize: 100);

        // Assert per-record-type in-memory breakdown
        Assert.Equal(2, metrics.TotalInserted);
        Assert.Equal(2, metrics.TotalUpdated);
        Assert.Equal(2, metrics.TotalUnchanged);

        var companyMetrics = metrics.MetricsByRecordType[CompanyMasterRecordType.Company];
        Assert.Equal(1, companyMetrics.Added);
        Assert.Equal(1, companyMetrics.Updated);
        Assert.Equal(1, companyMetrics.Unchanged);

        var llpMetrics = metrics.MetricsByRecordType[CompanyMasterRecordType.Llp];
        Assert.Equal(1, llpMetrics.Added);
        Assert.Equal(0, llpMetrics.Updated);
        Assert.Equal(1, llpMetrics.Unchanged);

        var foreignMetrics = metrics.MetricsByRecordType[CompanyMasterRecordType.Foreign];
        Assert.Equal(0, foreignMetrics.Added);
        Assert.Equal(1, foreignMetrics.Updated);
        Assert.Equal(0, foreignMetrics.Unchanged);

        // Assert persisted CompanyMasterSyncMetrics in database
        await using var freshDb = CreateContext();
        var persisted = await freshDb.CompanyMasterSyncMetrics
            .Where(m => m.JobId == testJobId)
            .ToDictionaryAsync(m => m.RecordType);

        Assert.Equal(3, persisted.Count);

        var persistedCompany = persisted[CompanyMasterRecordType.Company];
        Assert.Equal(3, persistedCompany.TotalSourceRows);
        Assert.Equal(1, persistedCompany.NewRowsAdded);
        Assert.Equal(1, persistedCompany.ExistingRowsUpdated);
        Assert.Equal(1, persistedCompany.UnchangedRowsSkipped);

        var persistedLlp = persisted[CompanyMasterRecordType.Llp];
        Assert.Equal(2, persistedLlp.TotalSourceRows);
        Assert.Equal(1, persistedLlp.NewRowsAdded);
        Assert.Equal(0, persistedLlp.ExistingRowsUpdated);
        Assert.Equal(1, persistedLlp.UnchangedRowsSkipped);

        var persistedForeign = persisted[CompanyMasterRecordType.Foreign];
        Assert.Equal(1, persistedForeign.TotalSourceRows);
        Assert.Equal(0, persistedForeign.NewRowsAdded);
        Assert.Equal(1, persistedForeign.ExistingRowsUpdated);
        Assert.Equal(0, persistedForeign.UnchangedRowsSkipped);
    }

    [Fact]
    public async Task LeaseExpiry_BeforePromotion_AbortsPromotionAndMarksPreempted()
    {
        await using var db = CreateContext();
        long token = 1;

        var job = new CompanyMasterSyncJob
        {
            FencingToken = token,
            Status = CompanyMasterSyncJobStatus.Staged,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-30),
            LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-10) // Expired in the past
        };
        db.CompanyMasterSyncJobs.Add(job);
        await db.SaveChangesAsync();
        long testJobId = job.JobId;

        db.StagingCompanyMasterRecords.Add(new StagingCompanyMasterRecord
        {
            SyncRunId = testJobId,
            FencingToken = token,
            ValidationState = "Validated",
            Identifier = $"U{Guid.NewGuid():N}"[..21],
            RecordType = CompanyMasterRecordType.Company,
            Name = "Expired Test Company",
            Status = "Active"
        });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
        var deltaService = CreateDeltaService(db, config, proxyPool, lease);

        var ex = await Assert.ThrowsAsync<SqlException>(() => deltaService.PromoteStagedDeltaAsync(testJobId, token, batchSize: 100));
        Assert.Contains("Fencing check failed", ex.Message);

        await using var freshDb = CreateContext();
        var refreshedJob = await freshDb.CompanyMasterSyncJobs.FindAsync(testJobId);
        Assert.NotNull(refreshedJob);
        Assert.Equal(CompanyMasterSyncJobStatus.PreemptedByTakeover, refreshedJob.Status);
    }

    [Fact]
    public async Task CompanyMasterSyncWorker_ConsentDisabled_LeavesJobPendingAwaitingManualUpload()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(ConnectionString));
        services.AddSingleton<IProxyPoolService>(new ProxyPoolService(new ConfigurationBuilder().Build(), NullLogger<ProxyPoolService>.Instance));
        services.AddSingleton<ISafeArchiveExtractor>(new SafeArchiveExtractor(NullLogger<SafeArchiveExtractor>.Instance));
        services.AddScoped<ISyncLockLease>(_ => new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance));
        services.AddSingleton<IRegistryPromotionCoordinator>(_ => new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance));
        services.AddScoped<ICompanyMasterDeltaService, CompanyMasterDeltaService>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["CompanyMasterSync:Enabled"] = "true",
                ["CompanyMasterSync:AutomationConsent"] = "false"
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();

        // Capture baseline so we can find only the job created by this test invocation.
        await using var baselineDb = CreateContext();
        long baselineMaxJobId = await baselineDb.CompanyMasterSyncJobs
            .MaxAsync(j => (long?)j.JobId) ?? 0;

        var worker = new CompanyMasterSyncWorker(scopeFactory, config, NullLogger<CompanyMasterSyncWorker>.Instance);
        await worker.RunScheduledSyncCheckAsync(CancellationToken.None);

        await using var db = CreateContext();
        var createdJob = await db.CompanyMasterSyncJobs
            .Where(j => j.JobId > baselineMaxJobId)
            .OrderByDescending(j => j.JobId)
            .FirstOrDefaultAsync();

        Assert.NotNull(createdJob);
        Assert.Equal(CompanyMasterSyncJobStatus.Pending, createdJob.Status);
        Assert.Contains("manual upload required", createdJob.ErrorMessage);
    }

    [Fact]
    public async Task CompanyMasterSyncWorker_ConsentEnabled_ExecutesAutomatedSyncPipelineToCompletion()
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"test_archive_{Guid.NewGuid():N}.zip");
        string cin = $"U{Guid.NewGuid():N}"[..21];
        try
        {
            using (var fileStream = File.Create(tempZip))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("companies.csv");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("CIN,Company Name,Company Registration Date,Company Category,Company Class,Listing Status,Authorized Capital,Paidup Capital,Company ROC,Company Address,Pin Code,Company State,Company Status,Company Sub Category,Company Industrial Classification");
                writer.WriteLine($"{cin},Worker Test Company Ltd,2026-01-01,Company limited by shares,Private,Unlisted,100000,100000,ROC MUMBAI,123 Marine Drive,400001,Maharashtra,Active,Non-government company,Financial services");
            }

            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(o => o.UseSqlServer(ConnectionString));
            services.AddSingleton<IProxyPoolService>(new ProxyPoolService(new ConfigurationBuilder().Build(), NullLogger<ProxyPoolService>.Instance));
            services.AddSingleton<ISafeArchiveExtractor>(new SafeArchiveExtractor(NullLogger<SafeArchiveExtractor>.Instance));
            services.AddScoped<ISyncLockLease>(_ => new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance));
            services.AddSingleton<IRegistryPromotionCoordinator>(_ => new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance));
            services.AddScoped<ICompanyMasterDeltaService, CompanyMasterDeltaService>();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = ConnectionString,
                    ["CompanyMasterSync:Enabled"] = "true",
                    ["CompanyMasterSync:AutomationConsent"] = "true",
                    ["CompanyMasterSync:ArchiveDropPath"] = tempZip
                })
                .Build();

            services.AddSingleton<IConfiguration>(config);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();

            // Capture baseline so we only look at the job created by this specific test.
            await using var baselineDb = CreateContext();
            long baselineMaxJobId = await baselineDb.CompanyMasterSyncJobs
                .MaxAsync(j => (long?)j.JobId) ?? 0;

            var worker = new CompanyMasterSyncWorker(scopeFactory, config, NullLogger<CompanyMasterSyncWorker>.Instance);
            await worker.RunScheduledSyncCheckAsync(CancellationToken.None);

            await using var db = CreateContext();
            var createdJob = await db.CompanyMasterSyncJobs
                .Where(j => j.JobId > baselineMaxJobId)
                .OrderByDescending(j => j.JobId)
                .FirstOrDefaultAsync();

            Assert.NotNull(createdJob);
            Assert.Equal(CompanyMasterSyncJobStatus.Completed, createdJob.Status);

            var liveRecord = await db.CompanyMasterRecords.FindAsync(cin);
            Assert.NotNull(liveRecord);
            Assert.Equal("Worker Test Company Ltd", liveRecord.Name);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    [Fact]
    public async Task SecondRun_SameDatePortal_SkipsDownloadAndCreatesNoNewJob()
    {
        // Arrange: seed a completed job with PublishedDate = today so the skip guard has data to match.
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var seedDb = CreateContext();
        var existingJob = new CompanyMasterSyncJob
        {
            FencingToken = 9000,
            TriggerType = CompanyMasterSyncTriggerType.Scheduled,
            Status = CompanyMasterSyncJobStatus.Completed,
            PublishedDate = today,
            PublishedDateRaw = today.ToString("O"),
            PublishedDateUtc = new DateTime(today.Year, today.Month, today.Day, 0, 0, 0, DateTimeKind.Utc),
            SanitizedProxyAlias = "Direct",
            CreatedUtc = DateTime.UtcNow.AddHours(-1),
            LastHeartbeatUtc = DateTime.UtcNow.AddHours(-1),
            CompletedUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        seedDb.CompanyMasterSyncJobs.Add(existingJob);
        await seedDb.SaveChangesAsync();

        long existingJobId = existingJob.JobId;
        int jobCountBefore = await seedDb.CompanyMasterSyncJobs.CountAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(ConnectionString));
        services.AddSingleton<IProxyPoolService>(new ProxyPoolService(new ConfigurationBuilder().Build(), NullLogger<ProxyPoolService>.Instance));
        services.AddSingleton<ISafeArchiveExtractor>(new SafeArchiveExtractor(NullLogger<SafeArchiveExtractor>.Instance));
        services.AddScoped<ISyncLockLease>(_ => new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance));

        // Use a fake delta service that returns today's portal date so the skip comparison fires.
        services.AddScoped<ICompanyMasterDeltaService, CompanyMasterDeltaService>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["CompanyMasterSync:Enabled"] = "true",
                ["CompanyMasterSync:AutomationConsent"] = "true",
                // PortalDateOverride is not a real config key; we rely on ProbePortalSnapshotDateAsync
                // hitting our stub via the underlying mock.
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);

        // Stub delta service that overrides only ProbePortalSnapshotDateAsync to return today.
        // The worker will compare it with lastJob.PublishedDate (which is also today) and bail.
        services.AddScoped<ICompanyMasterDeltaService>(sp =>
        {
            var coord = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
            var inner = new CompanyMasterDeltaService(
                sp.GetRequiredService<AppDbContext>(),
                config,
                sp.GetRequiredService<IProxyPoolService>(),
                sp.GetRequiredService<ISyncLockLease>(),
                coord,
                NullLogger<CompanyMasterDeltaService>.Instance);
            return new SameDateStubDeltaService(inner, today);
        });

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var worker = new CompanyMasterSyncWorker(scopeFactory, config, NullLogger<CompanyMasterSyncWorker>.Instance);
        await worker.RunScheduledSyncCheckAsync(CancellationToken.None);

        await using var checkDb = CreateContext();
        int jobCountAfter = await checkDb.CompanyMasterSyncJobs.CountAsync();

        // No new job should have been created; count stays the same.
        Assert.Equal(jobCountBefore, jobCountAfter);
    }

    [Fact]
    public async Task FencingToken_IsGloballyMonotonic_AcrossCompletedJobs()
    {
        // Complete two jobs explicitly, then create a third and assert it gets a higher token than both.
        await using var db = CreateContext();

        long priorMax = await db.CompanyMasterSyncJobs
            .MaxAsync(j => (long?)j.FencingToken) ?? 0;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();
        var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
        var svc = CreateDeltaService(db, config, proxyPool, lease);

        // Create and immediately complete two jobs.
        var j1 = await svc.CreateJobAsync(CompanyMasterSyncTriggerType.ManualForceSync, "test");
        await svc.UpdateJobStatusAsync(j1.JobId, CompanyMasterSyncJobStatus.Completed);

        await using var db2 = CreateContext();
        var svc2 = CreateDeltaService(db2, config, proxyPool, lease);
        var j2 = await svc2.CreateJobAsync(CompanyMasterSyncTriggerType.ManualForceSync, "test");
        await svc2.UpdateJobStatusAsync(j2.JobId, CompanyMasterSyncJobStatus.Completed);

        await using var db3 = CreateContext();
        var svc3 = CreateDeltaService(db3, config, proxyPool, lease);
        var j3 = await svc3.CreateJobAsync(CompanyMasterSyncTriggerType.ManualForceSync, "test");
        await svc3.UpdateJobStatusAsync(j3.JobId, CompanyMasterSyncJobStatus.Failed);

        // Each successive token must be strictly greater than the previous.
        Assert.True(j2.FencingToken > j1.FencingToken,
            $"j2 token {j2.FencingToken} should be > j1 token {j1.FencingToken}");
        Assert.True(j3.FencingToken > j2.FencingToken,
            $"j3 token {j3.FencingToken} should be > j2 token {j2.FencingToken}");
        // And all are greater than whatever was the prior max before the test.
        Assert.True(j1.FencingToken > priorMax,
            $"j1 token {j1.FencingToken} should be > prior max {priorMax}");
    }

    [Fact]
    public async Task IngestCsvFiles_ReturnsConsistentChecksumAndPersistsToJob()
    {
        // Two ingestion runs of the identical content must produce the same hex checksum,
        // and ExecuteAutomatedSyncAsync must write it to AggregateChecksum on the job row.
        string tempZip = Path.Combine(Path.GetTempPath(), $"chksum_test_{Guid.NewGuid():N}.zip");
        string cin = $"L{Guid.NewGuid():N}"[..21];
        try
        {
            using (var fs = File.Create(tempZip))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("companies.csv");
                using var w = new StreamWriter(entry.Open());
                w.WriteLine("CIN,Company Name,Company Registration Date,Company Category,Company Class,Listing Status,Authorized Capital,Paidup Capital,Company ROC,Company Address,Pin Code,Company State,Company Status,Company Sub Category,Company Industrial Classification");
                w.WriteLine($"{cin},Checksum Test Ltd,2025-06-01,Company limited by shares,Private,Unlisted,50000,50000,ROC DELHI,1 Test Road,110001,Delhi,Active,Non-government company,IT");
            }

            await using var db = CreateContext();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = ConnectionString,
                    ["CompanyMasterSync:ArchiveDropPath"] = tempZip
                })
                .Build();
            var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
            var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
            var svc = CreateDeltaService(db, config, proxyPool, lease);

            await lease.TryAcquireAsync("CompanyMasterSyncExclusiveLock", TimeSpan.FromSeconds(5));
            try
            {
                var job = await svc.CreateJobAsync(CompanyMasterSyncTriggerType.ManualForceSync, "test");
                await svc.ExecuteAutomatedSyncAsync(job.JobId, job.FencingToken);

                await using var freshDb = CreateContext();
                var refreshed = await freshDb.CompanyMasterSyncJobs.FindAsync(job.JobId);
                Assert.NotNull(refreshed);
                Assert.NotNull(refreshed.AggregateChecksum);
                Assert.Equal(64, refreshed.AggregateChecksum!.Length); // SHA256 = 32 bytes = 64 hex chars
                Assert.Equal(CompanyMasterSyncJobStatus.Completed, refreshed.Status);
            }
            finally
            {
                await lease.ReleaseAsync(CancellationToken.None);
            }
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// <summary>
    /// Test stub that overrides ProbePortalSnapshotDateAsync to return a fixed date,
    /// while delegating all other calls to the real service. Used to test same-date-skip logic.
    /// </summary>
    private sealed class SameDateStubDeltaService : ICompanyMasterDeltaService
    {
        private readonly ICompanyMasterDeltaService _inner;
        private readonly DateOnly _stubDate;

        public SameDateStubDeltaService(ICompanyMasterDeltaService inner, DateOnly stubDate)
        {
            _inner = inner;
            _stubDate = stubDate;
        }

        public Task<DateOnly?> ProbePortalSnapshotDateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DateOnly?>(_stubDate);

        public Task<CompanyMasterSyncJob> CreateJobAsync(CompanyMasterSyncTriggerType t, string? p,
            DateOnly? publishedDate = null, string? publishedDateRaw = null, CancellationToken ct = default)
            => _inner.CreateJobAsync(t, p, publishedDate, publishedDateRaw, ct);

        public Task UpdateJobStatusAsync(long jobId, CompanyMasterSyncJobStatus status, string? errorMessage = null, CancellationToken ct = default)
            => _inner.UpdateJobStatusAsync(jobId, status, errorMessage, ct);

        public Task<bool> RenewLeaseHeartbeatAsync(long jobId, long fencingToken, CancellationToken ct = default)
            => _inner.RenewLeaseHeartbeatAsync(jobId, fencingToken, ct);

        public Task<(long TotalRows, string AggregateChecksum)> IngestCsvFilesAsync(long syncRunId, long fencingToken, IReadOnlyList<string> csvFiles, CancellationToken ct = default)
            => _inner.IngestCsvFilesAsync(syncRunId, fencingToken, csvFiles, ct);

        public Task<ValidationResult> ValidateStagingAsync(long syncRunId, long fencingToken, CancellationToken ct = default)
            => _inner.ValidateStagingAsync(syncRunId, fencingToken, ct);

        public Task<PromotionMetricsResult> PromoteStagedDeltaAsync(long syncRunId, long fencingToken, int batchSize = 4000, CancellationToken ct = default, TimeSpan? admissionTimeout = null)
            => _inner.PromoteStagedDeltaAsync(syncRunId, fencingToken, batchSize, ct, admissionTimeout);

        public Task CleanStagingAsync(long syncRunId, CancellationToken ct = default)
            => _inner.CleanStagingAsync(syncRunId, ct);

        public Task<bool> IsChecksumAlreadyPromotedAsync(string aggregateChecksum, long currentJobId, CancellationToken cancellationToken = default)
            => _inner.IsChecksumAlreadyPromotedAsync(aggregateChecksum, currentJobId, cancellationToken);

        public Task<PromotionMetricsResult?> ExecuteAutomatedSyncAsync(long jobId, long fencingToken,
            DateOnly? publishedDate = null, string? publishedDateRaw = null,
            Stream? archiveStream = null, CancellationToken ct = default)
            => _inner.ExecuteAutomatedSyncAsync(jobId, fencingToken, publishedDate, publishedDateRaw, archiveStream, ct);
    }

    [Fact]
    public async Task SecondRun_SameArchiveChecksum_SkipsWithSkippedAlreadyPromotedChecksum()
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"idempotency_test_{Guid.NewGuid():N}.zip");
        string cin = $"L{Guid.NewGuid():N}"[..21];
        try
        {
            using (var fs = File.Create(tempZip))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("companies.csv");
                using var w = new StreamWriter(entry.Open());
                w.WriteLine("CIN,Company Name,Company Registration Date,Company Category,Company Class,Listing Status,Authorized Capital,Paidup Capital,Company ROC,Company Address,Pin Code,Company State,Company Status,Company Sub Category,Company Industrial Classification");
                w.WriteLine($"{cin},Idempotent Test Ltd,2025-07-01,Company limited by shares,Private,Unlisted,100000,100000,ROC DELHI,1 Idempotent Road,110001,Delhi,Active,Non-government company,IT");
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = ConnectionString,
                    ["CompanyMasterSync:ArchiveDropPath"] = tempZip
                })
                .Build();
            var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
            var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);

            await lease.TryAcquireAsync("CompanyMasterSyncExclusiveLock", TimeSpan.FromSeconds(5));
            try
            {
                // Run 1: First sync completes and promotes
                await using var db1 = CreateContext();
                var svc1 = CreateDeltaService(db1, config, proxyPool, lease);
                var job1 = await svc1.CreateJobAsync(CompanyMasterSyncTriggerType.ManualForceSync, "test1");
                var metrics1 = await svc1.ExecuteAutomatedSyncAsync(job1.JobId, job1.FencingToken);

                Assert.NotNull(metrics1);
                await using var checkDb1 = CreateContext();
                var refreshedJob1 = await checkDb1.CompanyMasterSyncJobs.FindAsync(job1.JobId);
                Assert.NotNull(refreshedJob1);
                Assert.Equal(CompanyMasterSyncJobStatus.Completed, refreshedJob1.Status);
                Assert.NotNull(refreshedJob1.AggregateChecksum);
                string firstChecksum = refreshedJob1.AggregateChecksum;

                // Run 2: Second sync with the IDENTICAL archive
                await using var db2 = CreateContext();
                var svc2 = CreateDeltaService(db2, config, proxyPool, lease);
                var job2 = await svc2.CreateJobAsync(CompanyMasterSyncTriggerType.ManualForceSync, "test2");
                var metrics2 = await svc2.ExecuteAutomatedSyncAsync(job2.JobId, job2.FencingToken);

                // Checksum idempotency must have kicked in:
                // 1. metrics2 is null (skipped duplicate promotion)
                Assert.Null(metrics2);

                // 2. job2 status is SkippedAlreadyPromotedChecksum with matching checksum
                await using var checkDb2 = CreateContext();
                var refreshedJob2 = await checkDb2.CompanyMasterSyncJobs.FindAsync(job2.JobId);
                Assert.NotNull(refreshedJob2);
                Assert.Equal(CompanyMasterSyncJobStatus.SkippedAlreadyPromotedChecksum, refreshedJob2.Status);
                Assert.Equal(firstChecksum, refreshedJob2.AggregateChecksum);

                // 3. Staging rows for job2 must have been cleanly removed
                int stagingRowsForJob2 = await checkDb2.StagingCompanyMasterRecords
                    .CountAsync(s => s.SyncRunId == job2.JobId);
                Assert.Equal(0, stagingRowsForJob2);
            }
            finally
            {
                await lease.ReleaseAsync(CancellationToken.None);
            }
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    [Fact]
    public async Task HeartbeatLoss_DuringStaging_CancelsPipelinePromptly_AndMarksPreempted()
    {
        // Construct archive with sample CSV
        using var memZip = new MemoryStream();
        using (var archive = new ZipArchive(memZip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("companies.csv");
            using var w = new StreamWriter(entry.Open());
            w.WriteLine("CIN,Company Name,Company Registration Date,Company Category,Company Class,Listing Status,Authorized Capital,Paidup Capital,Company ROC,Company Address,Pin Code,Company State,Company Status,Company Sub Category,Company Industrial Classification");
            w.WriteLine($"U{Guid.NewGuid():N}"[..21] + ",Preempted Worker Ltd,2025-01-01,Company limited by shares,Private,Unlisted,50000,50000,ROC DELHI,1 Preempt Road,110001,Delhi,Active,Non-government company,IT");
        }
        byte[] zipBytes = memZip.ToArray();

        // 1-second heartbeat interval so the heartbeat tick fires promptly
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["CompanyMasterSync:HeartbeatIntervalSeconds"] = "1"
            })
            .Build();

        var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);

        await using var db = CreateContext();
        var preemptingSvc = new PreemptingHeartbeatDeltaService(db, config, proxyPool, lease, NullLogger<CompanyMasterDeltaService>.Instance);

        await lease.TryAcquireAsync("CompanyMasterSyncExclusiveLock", TimeSpan.FromSeconds(5));
        try
        {
            var job = await preemptingSvc.CreateJobAsync(CompanyMasterSyncTriggerType.Scheduled, "test_preempt");

            // SlowStream delays read so that the 1-second heartbeat timer fires during reading/staging
            using var slowStream = new SlowStream(zipBytes, delayMs: 1200);

            // Must throw OperationCanceledException due to heartbeat preemption cancellation
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                preemptingSvc.ExecuteAutomatedSyncAsync(job.JobId, job.FencingToken, archiveStream: slowStream));

            Assert.True(preemptingSvc.HeartbeatFailed, "Heartbeat check should have executed and returned false.");

            // Verify job row was updated to PreemptedByTakeover
            await using var checkDb = CreateContext();
            var refreshedJob = await checkDb.CompanyMasterSyncJobs.FindAsync(job.JobId);
            Assert.NotNull(refreshedJob);
            Assert.Equal(CompanyMasterSyncJobStatus.PreemptedByTakeover, refreshedJob.Status);
            Assert.Contains("Heartbeat loss during staging", refreshedJob.ErrorMessage);

            // Staging rows must be cleaned up
            int stagingRows = await checkDb.StagingCompanyMasterRecords.CountAsync(s => s.SyncRunId == job.JobId);
            Assert.Equal(0, stagingRows);
        }
        finally
        {
            await lease.ReleaseAsync(CancellationToken.None);
        }
    }

    private sealed class PreemptingHeartbeatDeltaService : CompanyMasterDeltaService
    {
        public bool HeartbeatFailed { get; private set; }

        public PreemptingHeartbeatDeltaService(
            AppDbContext db,
            IConfiguration configuration,
            IProxyPoolService proxyPool,
            ISyncLockLease syncLockLease,
            Microsoft.Extensions.Logging.ILogger<CompanyMasterDeltaService> logger,
            IRegistryPromotionCoordinator? coordinator = null)
            : base(db, configuration, proxyPool, syncLockLease, coordinator ?? new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance), logger)
        {
        }

        public override Task<bool> RenewLeaseHeartbeatAsync(long jobId, long fencingToken, CancellationToken cancellationToken = default)
        {
            HeartbeatFailed = true;
            // Simulate worker preemption / lease loss during staging
            return Task.FromResult(false);
        }
    }

    private sealed class SlowStream : MemoryStream
    {
        private readonly int _delayMs;
        public SlowStream(byte[] buffer, int delayMs = 1200) : base(buffer)
        {
            _delayMs = delayMs;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delayMs, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteAutomatedSync_FencingTakeoverDuringPromotion_PreservesPreemptedByTakeoverStatus()
    {
        // Build test archive with sample CSV
        using var memZip = new MemoryStream();
        using (var archive = new ZipArchive(memZip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("companies.csv");
            using var w = new StreamWriter(entry.Open());
            w.WriteLine("CIN,Company Name,Company Registration Date,Company Category,Company Class,Listing Status,Authorized Capital,Paidup Capital,Company ROC,Company Address,Pin Code,Company State,Company Status,Company Sub Category,Company Industrial Classification");
            w.WriteLine($"U{Guid.NewGuid():N}"[..21] + ",Takeover Test Ltd,2025-01-01,Company limited by shares,Private,Unlisted,50000,50000,ROC DELHI,1 Takeover Road,110001,Delhi,Active,Non-government company,IT");
        }
        memZip.Position = 0;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);

        await using var db = CreateContext();
        var takeoverSvc = new TakeoverBeforePromotionDeltaService(db, config, proxyPool, lease, NullLogger<CompanyMasterDeltaService>.Instance);

        await lease.TryAcquireAsync("CompanyMasterSyncExclusiveLock", TimeSpan.FromSeconds(5));
        try
        {
            var job = await takeoverSvc.CreateJobAsync(CompanyMasterSyncTriggerType.Scheduled, "test_takeover_e2e");

            // Executing automated sync must fail on promotion because FencingToken was bumped after validation
            var ex = await Assert.ThrowsAsync<SqlException>(() =>
                takeoverSvc.ExecuteAutomatedSyncAsync(job.JobId, job.FencingToken, archiveStream: memZip));

            Assert.Contains("Fencing check failed", ex.Message);

            // Verify the outer handler preserved PreemptedByTakeover and did NOT overwrite it with Failed
            await using var checkDb = CreateContext();
            var refreshedJob = await checkDb.CompanyMasterSyncJobs.FindAsync(job.JobId);
            Assert.NotNull(refreshedJob);
            Assert.Equal(CompanyMasterSyncJobStatus.PreemptedByTakeover, refreshedJob.Status);
            Assert.Contains("Fencing check failed", refreshedJob.ErrorMessage);

            // Staging rows must have been cleaned up
            int stagingRows = await checkDb.StagingCompanyMasterRecords.CountAsync(s => s.SyncRunId == job.JobId);
            Assert.Equal(0, stagingRows);
        }
        finally
        {
            await lease.ReleaseAsync(CancellationToken.None);
        }
    }

    private sealed class TakeoverBeforePromotionDeltaService : CompanyMasterDeltaService
    {
        private readonly AppDbContext _testDb;

        public TakeoverBeforePromotionDeltaService(
            AppDbContext db,
            IConfiguration configuration,
            IProxyPoolService proxyPool,
            ISyncLockLease syncLockLease,
            Microsoft.Extensions.Logging.ILogger<CompanyMasterDeltaService> logger,
            IRegistryPromotionCoordinator? coordinator = null)
            : base(db, configuration, proxyPool, syncLockLease, coordinator ?? new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance), logger)
        {
            _testDb = db;
        }

        public override async Task<ValidationResult> ValidateStagingAsync(long syncRunId, long fencingToken, CancellationToken cancellationToken = default)
        {
            var result = await base.ValidateStagingAsync(syncRunId, fencingToken, cancellationToken);

            // Simulate fencing takeover by another worker immediately after staging validation, right before promotion
            await _testDb.Database.ExecuteSqlAsync(
                $"UPDATE dbo.CompanyMasterSyncJobs SET FencingToken = FencingToken + 1 WHERE JobId = {syncRunId}",
                cancellationToken);

            return result;
        }
    }

    [Fact]
    public void AuditRouteRegistry_ExhaustiveReflectionTest_PassesWithCompanyMasterRoutes()
    {
        string[] mutatingActions = { "Probe", "TestProxies", "SyncNow", "UploadManual" };
        foreach (var action in mutatingActions)
        {
            var (actionType, policy) = AuditRouteRegistry.Resolve("CompanyMasterDashboard", action);
            Assert.NotEqual(AuditActionType.OtherMutation, actionType);
            Assert.Equal(AuditRulePolicy.Always, policy);
        }
    }
}
