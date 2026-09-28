using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Services.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace MCAROC_Analysis.Tests;

public sealed class RegistryAnalyticsTests
{
    private sealed class ReadOnlyBaselineState(RegistryAggregateData? snapshot, bool admissionOpen = true)
        : IRegistrySnapshotStore, IRegistryPromotionCoordinator
    {
        public int Reads { get; private set; }
        public Task<RegistryAggregateData?> GetSnapshotAsync(long id, CancellationToken ct = default)
        { Assert.Equal(-1, id); Reads++; return Task.FromResult(snapshot); }
        public Task SaveSnapshotAsync(long id, RegistryAggregateData data, CancellationToken ct = default)
            => throw new InvalidOperationException("A dashboard request must not rebuild or save aggregates.");
        public bool IsPromotionActive(out long? id) { id = null; return !admissionOpen; }
        public Task<IAsyncDisposable> AcquirePromotionAdmissionAsync(long id, TimeSpan timeout, CancellationToken ct = default)
            => throw new InvalidOperationException("Unexpected promotion admission.");
        public Task<IAsyncDisposable?> TryAcquireRebuildGateAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("A dashboard request must not acquire a rebuild gate.");
        public Task<bool> TryProbePromotionAdmissionAsync(CancellationToken ct = default) => Task.FromResult(admissionOpen);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DashboardCacheReadNeverWaitsForRebuildEvenWhenSnapshotIsStaleOrMissing(bool hasSnapshot, bool admissionOpen)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options);
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        using var heldRebuildLock = new SemaphoreSlim(0, 1);
        var data = hasSnapshot ? new RegistryAggregateData {
            Metadata = new() { IsImportedBaseline = true, CalculatedUtc = DateTime.UtcNow.AddDays(-1) },
            EntityAnalytics = [new() { RecordType = CompanyMasterRecordType.Company, Statuses = [new("Active", 10)] }]
        } : null;
        var state = new ReadOnlyBaselineState(data, admissionOpen);
        var service = new CompanyRegistryQueryService(db, cache, state, state,
            NullLogger<CompanyRegistryQueryService>.Instance, heldRebuildLock);
        var read = (Task<RegistryAggregateData?>)typeof(CompanyRegistryQueryService)
            .GetMethod("GetImportedBaselineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [CancellationToken.None, false])!;
        var returned = await read.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(admissionOpen ? data : null, returned);
        Assert.Equal(admissionOpen ? 1 : 0, state.Reads);
    }

    [Fact]
    public void ExplorerSqlSelectsOnlyDisplayFieldsFromOriginalMasterSchema()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options);
        var projection = (Expression<Func<CompanyMasterRecord, RegistryRecordRow>>)typeof(CompanyRegistryQueryService)
            .GetField("ExplorerProjection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var query = db.CompanyMasterRecords.Where(r => r.RecordType == CompanyMasterRecordType.Company && r.Name.StartsWith("reliance"))
            .OrderBy(r => r.Name).ThenBy(r => r.Identifier).Take(51).Select(projection).ToQueryString();
        Assert.DoesNotContain("NameNormalized", query);
        Assert.DoesNotContain("NameCore", query);
        Assert.DoesNotContain("EntityForm", query);
        Assert.Contains("[Identifier]", query);
        Assert.Contains("[RegistrationDate]", query);
        Assert.Contains("ORDER BY", query);
    }

    [Fact]
    public void SharedSnapshotRoundTripRetainsLlpAnalyticsAndBaselineProvenance()
    {
        var data = new RegistryAggregateData
        {
            Metadata = new() { IsImportedBaseline = true, CalculatedUtc = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc) },
            EntityAnalytics = [new() { RecordType = CompanyMasterRecordType.Llp,
                Statuses = [new("Active", 7), new("Under CIRP", 2), new("Other", 1)],
                States = [new("Maharashtra", 10)], Rocs = [new("ROC Mumbai", 10)],
                Registrations = [new(2026, 8, 3)] }]
        };
        var json = JsonSerializer.Serialize(RegistryAggregateSnapshotDto.FromModel(data));
        var restored = JsonSerializer.Deserialize<RegistryAggregateSnapshotDto>(json)!.ToModel();
        Assert.True(restored.Metadata.IsImportedBaseline);
        Assert.Null(restored.Metadata.PublishedDate);
        Assert.Equal(data.Metadata.CalculatedUtc, restored.Metadata.CalculatedUtc);
        var llp = Assert.Single(restored.EntityAnalytics);
        Assert.Equal(10, llp.Total);
        Assert.Equal(2, llp.Distressed);
        Assert.Equal("ROC Mumbai", Assert.Single(llp.Rocs).Label);
        Assert.Equal(3, Assert.Single(llp.Registrations).Count);
    }

    [Theory]
    [InlineData("Under CIRP", true)]
    [InlineData("Under Process of Strike-Off", true)]
    [InlineData("Active", false)]
    [InlineData("Strike Off", false)]
    [InlineData("Unknown", false)]
    public void DistressCountsUseRecordedStatusesWithoutInferringRisk(string status, bool expected)
        => Assert.Equal(expected, RegistryEntityAnalytics.IsDistressed(status));
}
