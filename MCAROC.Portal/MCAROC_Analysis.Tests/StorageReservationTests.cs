using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class StorageReservationTests
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    [Fact]
    public async Task CanReserve_TransitionsOwnershipToBatch_AndReleasesCleanly()
    {
        await using var db = CreateContext();
        var options = Options.Create(new LargeArchiveUploadOptions());
        var mgr = new StorageReservationManager(db, options, NullLogger<StorageReservationManager>.Instance);

        var sessionId = Guid.NewGuid();
        var tempStaging = Path.Combine(Path.GetTempPath(), "staging-" + Guid.NewGuid().ToString("N"));
        var tempDest = Path.Combine(Path.GetTempPath(), "dest-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempStaging);
            Directory.CreateDirectory(tempDest);

            // 1. TryReserve
            var result = await mgr.TryReserveUploadCapacityAsync(sessionId, 100_000_000, tempStaging, tempDest);
            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.ReservationId);

            // Verify active reservation exists
            var res = await db.StorageCapacityReservations
                .FirstOrDefaultAsync(r => r.OwnerType == "UploadSession" && r.OwnerId == sessionId.ToString());
            Assert.NotNull(res);
            Assert.Equal(StorageCapacityReservationState.Active, res.State);

            // 2. Transition to batch
            var transitioned = await mgr.TransitionReservationToBatchAsync(sessionId, 99999);
            Assert.True(transitioned);

            var batchRes = await db.StorageCapacityReservations
                .FirstOrDefaultAsync(r => r.OwnerType == "FilingBatch" && r.OwnerId == "99999");
            Assert.NotNull(batchRes);
            Assert.Equal(StorageCapacityReservationState.Active, batchRes.State);

            // 3. Release
            await mgr.ReleaseReservationsAsync("FilingBatch", "99999");

            var releasedRes = await db.StorageCapacityReservations
                .FirstOrDefaultAsync(r => r.OwnerType == "FilingBatch" && r.OwnerId == "99999");
            Assert.NotNull(releasedRes);
            Assert.Equal(StorageCapacityReservationState.Released, releasedRes.State);
        }
        finally
        {
            try { Directory.Delete(tempStaging, recursive: true); } catch { }
            try { Directory.Delete(tempDest, recursive: true); } catch { }
            await mgr.ReleaseReservationsAsync("UploadSession", sessionId.ToString());
            await mgr.ReleaseReservationsAsync("FilingBatch", "99999");
        }
    }
}
