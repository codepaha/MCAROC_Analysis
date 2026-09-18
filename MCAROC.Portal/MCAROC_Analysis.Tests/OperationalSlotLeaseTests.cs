using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class OperationalSlotLeaseTests : IAsyncLifetime
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
    public async Task EnforcesSingleSlot_WithRenewalAndRelease()
    {
        await using var db = CreateContext();
        var service = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);

        var slotType = "TestSlot_" + Guid.NewGuid().ToString("N")[..8];
        var holder1 = "Holder1_" + Guid.NewGuid().ToString("N")[..8];
        var holder2 = "Holder2_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            // 1. Holder 1 acquires slot
            var r1 = await service.TryAcquireSlotAsync(slotType, holder1, TimeSpan.FromMinutes(5));
            Assert.True(r1.Success, r1.Error);
            Assert.Equal(holder1, r1.HolderId);

            // 2. Holder 2 attempts to acquire same slot -> must fail
            var r2 = await service.TryAcquireSlotAsync(slotType, holder2, TimeSpan.FromMinutes(5));
            Assert.False(r2.Success);
            Assert.Equal(holder1, r2.HolderId);

            // 3. Holder 1 renews slot
            var renewed = await service.TryRenewSlotAsync(slotType, holder1, TimeSpan.FromMinutes(10));
            Assert.True(renewed);

            // 4. Holder 1 releases slot
            await service.ReleaseSlotAsync(slotType, holder1);

            // 5. Holder 2 acquires now-free slot -> succeeds
            var r3 = await service.TryAcquireSlotAsync(slotType, holder2, TimeSpan.FromMinutes(5));
            Assert.True(r3.Success, r3.Error);
            Assert.Equal(holder2, r3.HolderId);
        }
        finally
        {
            await service.ReleaseSlotAsync(slotType, holder1);
            await service.ReleaseSlotAsync(slotType, holder2);
        }
    }

    /// <summary>The throughput fix this closes, directly: capacity &gt; 1 admits that many concurrent
    /// holders — several batches/uploads genuinely run at once — and only refuses once the slot is
    /// actually full, not after the first holder the way the old single-row design did.</summary>
    [Fact]
    public async Task Capacity_greater_than_one_admits_that_many_concurrent_holders_then_refuses()
    {
        await using var db = CreateContext();
        var service = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);

        var slotType = "TestSlot_" + Guid.NewGuid().ToString("N")[..8];
        var holderA = "HolderA_" + Guid.NewGuid().ToString("N")[..8];
        var holderB = "HolderB_" + Guid.NewGuid().ToString("N")[..8];
        var holderC = "HolderC_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            var a = await service.TryAcquireSlotAsync(slotType, holderA, TimeSpan.FromMinutes(5), capacity: 2);
            Assert.True(a.Success, a.Error);
            var b = await service.TryAcquireSlotAsync(slotType, holderB, TimeSpan.FromMinutes(5), capacity: 2);
            Assert.True(b.Success, b.Error); // both A and B held at once — the point of the fix

            var c = await service.TryAcquireSlotAsync(slotType, holderC, TimeSpan.FromMinutes(5), capacity: 2);
            Assert.False(c.Success); // slot is genuinely full at capacity 2

            // Releasing one frees exactly one seat for the next holder.
            await service.ReleaseSlotAsync(slotType, holderA);
            var c2 = await service.TryAcquireSlotAsync(slotType, holderC, TimeSpan.FromMinutes(5), capacity: 2);
            Assert.True(c2.Success, c2.Error);

            // B and C both still genuinely hold a lease at once.
            var activeHolders = await db.OperationalSlotLeases
                .Where(s => s.SlotType == slotType)
                .Select(s => s.ActiveHolderId)
                .ToListAsync();
            Assert.Equal(new[] { holderB, holderC }, activeHolders.OrderBy(x => x));
        }
        finally
        {
            await service.ReleaseSlotAsync(slotType, holderA);
            await service.ReleaseSlotAsync(slotType, holderB);
            await service.ReleaseSlotAsync(slotType, holderC);
        }
    }

    /// <summary>A holder re-acquiring a slot type it already holds (a retry, or a resumed job) renews its
    /// own lease in place rather than being counted as a second holder against capacity — re-acquiring at
    /// capacity 1 must not spuriously fail just because a row for that same holder already exists.</summary>
    [Fact]
    public async Task Reacquiring_ones_own_slot_renews_it_without_counting_twice_against_capacity()
    {
        await using var db = CreateContext();
        var service = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);

        var slotType = "TestSlot_" + Guid.NewGuid().ToString("N")[..8];
        var holder = "Holder_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            var first = await service.TryAcquireSlotAsync(slotType, holder, TimeSpan.FromMinutes(1), capacity: 1);
            Assert.True(first.Success, first.Error);

            var again = await service.TryAcquireSlotAsync(slotType, holder, TimeSpan.FromMinutes(10), capacity: 1);
            Assert.True(again.Success, again.Error);

            var rowCount = await db.OperationalSlotLeases.CountAsync(s => s.SlotType == slotType);
            Assert.Equal(1, rowCount); // renewed the same row, not a second one

            var lease = await db.OperationalSlotLeases.AsNoTracking().SingleAsync(s => s.SlotType == slotType);
            Assert.True(lease.ExpiresUtc > DateTime.UtcNow.AddMinutes(5)); // reflects the longer, second duration
        }
        finally
        {
            await service.ReleaseSlotAsync(slotType, holder);
        }
    }
}
