using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class ChunkStreamingAndRecoveryTests : IAsyncLifetime
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
    public async Task ChunkAppend_TruncatesUncommittedTail_UnderWriteLease()
    {
        await using var db = CreateContext();
        var options = Options.Create(new LargeArchiveUploadOptions { ChunkSizeBytes = 100 });
        var service = new ChunkStreamingService(db, options, NullLogger<ChunkStreamingService>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), "chunk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var stagingPart = Path.Combine(tempDir, "archive.part");

        var token = "token123";
        var hashedToken = ChunkStreamingService.ComputeTokenHash(token);
        var sessionId = Guid.NewGuid();

        var session = new LargeArchiveUploadSession
        {
            SessionId = sessionId,
            RequestId = 1,
            HashedCapabilityToken = hashedToken,
            OriginalFileName = "archive.zip",
            TotalExpectedSizeBytes = 200,
            NextExpectedOffset = 0,
            ExpectedFullSha256 = new string('a', 64),
            Status = LargeArchiveUploadSessionStatus.Uploading,
            StagingFilePath = stagingPart,
            DestinationStoragePath = Path.Combine(tempDir, "archive.zip"),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        };

        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync();

        try
        {
            // 1. Simulate uncommitted tail: archive.part has 50 bytes of garbage from prior crashed append
            await File.WriteAllBytesAsync(stagingPart, new byte[50]);

            // 2. Prepare legitimate chunk 1 (100 bytes of 0x01)
            var chunkData = new byte[100];
            Array.Fill(chunkData, (byte)1);
            var sha = Convert.ToHexString(SHA256.HashData(chunkData)).ToLowerInvariant();

            using var stream = new MemoryStream(chunkData);
            var res = await service.WriteChunkAsync(sessionId, token, 0, 100, sha, stream, CancellationToken.None);

            Assert.True(res.Success, res.Error);
            Assert.Equal(100, res.AcceptedOffset);

            // Staging file should now be exactly 100 bytes (the 50 garbage bytes were truncated under lease)
            var fileBytes = await File.ReadAllBytesAsync(stagingPart);
            Assert.Equal(100, fileBytes.Length);
            Assert.All(fileBytes, b => Assert.Equal(1, b));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.LargeArchiveUploadSessions.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }
}
