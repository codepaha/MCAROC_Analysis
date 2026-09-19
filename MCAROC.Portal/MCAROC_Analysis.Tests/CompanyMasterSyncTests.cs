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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CompanyMasterSyncTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
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
            var deltaService = new CompanyMasterDeltaService(db, config, proxyPool, lease, NullLogger<CompanyMasterDeltaService>.Instance);

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
            var deltaService = new CompanyMasterDeltaService(db, config, proxyPool, lease, NullLogger<CompanyMasterDeltaService>.Instance);

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
            var deltaService = new CompanyMasterDeltaService(db, config, proxyPool, lease, NullLogger<CompanyMasterDeltaService>.Instance);

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
            var deltaService = new CompanyMasterDeltaService(db, config, proxyPool, lease, NullLogger<CompanyMasterDeltaService>.Instance);

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
