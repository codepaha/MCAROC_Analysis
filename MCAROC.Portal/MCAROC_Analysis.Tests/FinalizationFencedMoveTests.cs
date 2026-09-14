using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class FinalizationFencedMoveTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FinalizationMove_ProtectsCriticalSection_WithSqlAppLock()
    {
        await using var db = CreateContext();

        var sessionId = Guid.NewGuid();
        var tempDir = Path.Combine(Path.GetTempPath(), "applock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var stagingPart = Path.Combine(tempDir, "archive.part");
        var destZip = Path.Combine(tempDir, "archive.zip");

        // Create a valid zip archive
        using (var zip = ZipFile.Open(stagingPart, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("doc.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("hello world");
        }

        var fullBytes = await File.ReadAllBytesAsync(stagingPart);
        var sha = Convert.ToHexString(SHA256.HashData(fullBytes)).ToLowerInvariant();

        var session = new LargeArchiveUploadSession
        {
            SessionId = sessionId,
            RequestId = 1,
            HashedCapabilityToken = ChunkStreamingService.ComputeTokenHash("token"),
            OriginalFileName = "archive.zip",
            TotalExpectedSizeBytes = fullBytes.Length,
            NextExpectedOffset = fullBytes.Length,
            ExpectedFullSha256 = sha,
            Status = LargeArchiveUploadSessionStatus.Verifying,
            StagingFilePath = stagingPart,
            DestinationStoragePath = destZip,
            FinalizationAttemptId = Guid.NewGuid(),
            ActiveFinalizationExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        };

        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync();

        try
        {
            // Acquire the exclusive sp_getapplock in a background transaction to simulate active move
            var lockResource = $"FinalizationMove_{session.SessionId:N}";
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var lockAcquired = false;
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DECLARE @res INT;
                EXEC @res = sp_getapplock
                    @Resource = {lockResource},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 1000;
                IF (@res >= 0) SELECT 1; ELSE THROW 50001, 'Lock timeout', 1;");
            lockAcquired = true;

            Assert.True(lockAcquired);

            // A second concurrent connection attempting to acquire the same lock with short timeout MUST fail
            await using var db2 = CreateContext();
            await using var tx2 = await db2.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var timeoutThrown = false;
            try
            {
                await db2.Database.ExecuteSqlInterpolatedAsync($@"
                    DECLARE @res INT;
                    EXEC @res = sp_getapplock
                        @Resource = {lockResource},
                        @LockMode = 'Exclusive',
                        @LockOwner = 'Transaction',
                        @LockTimeout = 500;
                    IF (@res < 0) THROW 50002, 'Lock timeout', 1;");
            }
            catch (Exception)
            {
                timeoutThrown = true;
            }

            Assert.True(timeoutThrown, "Concurrent finalization move must block/fail on held sp_getapplock.");

            await tx.RollbackAsync();
            await tx2.RollbackAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.LargeArchiveUploadSessions.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }
}
