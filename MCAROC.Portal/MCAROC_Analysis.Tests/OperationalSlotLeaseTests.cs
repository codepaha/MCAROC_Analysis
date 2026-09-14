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
}
