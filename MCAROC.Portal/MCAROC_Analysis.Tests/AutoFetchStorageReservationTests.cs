using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Coverage for <see cref="StorageReservationManager.TryReserveAsync"/> — the generic single-
/// volume reservation AutoFetchJobService uses to reserve disk headroom before downloading a job's filing
/// PDFs. Proves the finding this closes: two jobs (or an auto-fetch job and a large-archive upload)
/// genuinely contend for the same tracked disk headroom, not two independent, uncoordinated checks.
///
/// Every test uses a synthetic UNC-style "volume" unique to that test run (a random path's root is a
/// UNC share name, e.g. \\vol-&lt;guid&gt;\share\), with <see cref="FixedFreeSpaceManager"/> stubbing the
/// free-space figure for it — deterministic and independent of the real disk and of any other test's
/// reservations on the machine's actual drives, rather than racing real headroom on a shared test box.</summary>
public class AutoFetchStorageReservationTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    /// <summary>A directory under a unique fake UNC root — Path.GetPathRoot resolves this to
    /// \\vol-&lt;guid&gt;\share\, a "volume" no other test (or prior run against the persistent test DB)
    /// has ever reserved against.</summary>
    private static string NewSyntheticDirectory() => $@"\\vol-{Guid.NewGuid():N}\share\autofetch";

    private static FixedFreeSpaceManager NewManager(AppDbContext db, long freeSpaceBytes) =>
        new(db, Options.Create(new LargeArchiveUploadOptions()), NullLogger<StorageReservationManager>.Instance, freeSpaceBytes);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Refuses_a_reservation_that_exceeds_free_space()
    {
        await using var db = CreateContext();
        var mgr = NewManager(db, freeSpaceBytes: 100_000_000L); // 100 MB synthetic volume
        var dir = NewSyntheticDirectory();

        var result = await mgr.TryReserveAsync("AutoFetchJob", "job-" + Guid.NewGuid().ToString("N"), dir, 200_000_000L, TimeSpan.FromHours(1));

        Assert.False(result.Success);
        Assert.Contains("Insufficient storage", result.Error);
        Assert.Null(result.ReservationId);
    }

    [Fact]
    public async Task Reserves_within_free_space_and_releases_it()
    {
        await using var db = CreateContext();
        var mgr = NewManager(db, freeSpaceBytes: 100_000_000L);
        var dir = NewSyntheticDirectory();
        var jobId = "job-" + Guid.NewGuid().ToString("N");

        var result = await mgr.TryReserveAsync("AutoFetchJob", jobId, dir, 10_000_000, TimeSpan.FromHours(1));
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ReservationId);

        var stored = await db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.OwnerType == "AutoFetchJob" && r.OwnerId == jobId);
        Assert.Equal(StorageCapacityReservationState.Active, stored.State);
        Assert.Equal(10_000_000, stored.ReservedBytes);

        await mgr.ReleaseReservationsAsync("AutoFetchJob", jobId);

        var released = await db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.OwnerType == "AutoFetchJob" && r.OwnerId == jobId);
        Assert.Equal(StorageCapacityReservationState.Released, released.State);
    }

    /// <summary>The concurrent-jobs scenario directly: job A reserves nearly all of a volume's (synthetic)
    /// free space; job B's reservation for a modest amount on the SAME volume is then refused — not
    /// because the disk is actually full, but because A's still-active reservation is correctly counted
    /// against B. Once A releases, the identical request from B succeeds, proving release genuinely frees
    /// the capacity back up for the next job rather than leaking it.</summary>
    [Fact]
    public async Task A_second_jobs_reservation_is_blocked_by_the_firsts_active_reservation_and_freed_on_release()
    {
        await using var db = CreateContext();
        var mgr = NewManager(db, freeSpaceBytes: 100_000_000L); // 100 MB synthetic volume
        var dir = NewSyntheticDirectory();
        var jobA = "job-a-" + Guid.NewGuid().ToString("N");
        var jobB = "job-b-" + Guid.NewGuid().ToString("N");
        const long jobBRequest = 50_000_000L; // 50 MB

        var reservedByA = await mgr.TryReserveAsync("AutoFetchJob", jobA, dir, 90_000_000L, TimeSpan.FromHours(1)); // leaves 10 MB
        Assert.True(reservedByA.Success, reservedByA.Error);

        var blockedB = await mgr.TryReserveAsync("AutoFetchJob", jobB, dir, jobBRequest, TimeSpan.FromHours(1));
        Assert.False(blockedB.Success);
        Assert.Contains("Insufficient storage", blockedB.Error);

        await mgr.ReleaseReservationsAsync("AutoFetchJob", jobA);

        var admittedB = await mgr.TryReserveAsync("AutoFetchJob", jobB, dir, jobBRequest, TimeSpan.FromHours(1));
        Assert.True(admittedB.Success, admittedB.Error);
    }

    private sealed class FixedFreeSpaceManager(AppDbContext db, IOptions<LargeArchiveUploadOptions> options, ILogger<StorageReservationManager> logger, long freeSpaceBytes)
        : StorageReservationManager(db, options, logger)
    {
        public override long GetAvailableFreeSpace(string volumeRoot) => freeSpaceBytes;
    }
}
