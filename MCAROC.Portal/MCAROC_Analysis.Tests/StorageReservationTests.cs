using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class StorageReservationTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CanReserve_TransitionsOwnershipToBatch_AndReleasesCleanly()
    {
        await using var db = CreateContext();
        var options = Options.Create(new LargeArchiveUploadOptions
        {
            MaxUncompressedSizeBytes = 10_000_000,
            MaxNestedTempBytes = 1_000_000,
            MinFreeDiskHeadroomBytes = 5_000_000
        });
        var mgr = new StorageReservationManager(db, options, NullLogger<StorageReservationManager>.Instance);

        var sessionId = Guid.NewGuid();
        var tempStaging = Path.Combine(Path.GetTempPath(), "staging-" + Guid.NewGuid().ToString("N"));
        var tempDest = Path.Combine(Path.GetTempPath(), "dest-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempStaging);
            Directory.CreateDirectory(tempDest);

            // 1. TryReserve refusal for impossible size (e.g. 500 TB)
            var refusal = await mgr.TryReserveUploadCapacityAsync(sessionId, 500_000_000_000_000L, tempStaging, tempDest);
            Assert.False(refusal.Success);
            Assert.Contains("Insufficient storage", refusal.Error);

            // 2. TryReserve success for realistic test size
            var result = await mgr.TryReserveUploadCapacityAsync(sessionId, 10_000_000, tempStaging, tempDest);
            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.ReservationId);

            // Verify active reservation exists
            var res = await db.StorageCapacityReservations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OwnerType == "UploadSession" && r.OwnerId == sessionId.ToString());
            Assert.NotNull(res);
            Assert.Equal(StorageCapacityReservationState.Active, res.State);

            // 2. Transition to batch
            var batchId = Random.Shared.Next(100_000, 999_999);
            var batchIdStr = batchId.ToString();
            var transitioned = await mgr.TransitionReservationToBatchAsync(sessionId, batchId);
            Assert.True(transitioned);

            var batchRes = await db.StorageCapacityReservations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OwnerType == "FilingBatch" && r.OwnerId == batchIdStr);
            Assert.NotNull(batchRes);
            Assert.Equal(StorageCapacityReservationState.Active, batchRes.State);

            // 3. Release
            await mgr.ReleaseReservationsAsync("FilingBatch", batchIdStr);

            var releasedRes = await db.StorageCapacityReservations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OwnerType == "FilingBatch" && r.OwnerId == batchIdStr);
            Assert.NotNull(releasedRes);
            Assert.Equal(StorageCapacityReservationState.Released, releasedRes.State);
        }
        finally
        {
            try { Directory.Delete(tempStaging, recursive: true); } catch { }
            try { Directory.Delete(tempDest, recursive: true); } catch { }
            await db.StorageCapacityReservations
                .Where(r => r.OwnerId == sessionId.ToString() || r.OwnerType == "FilingBatch")
                .ExecuteDeleteAsync();
        }
    }
}
