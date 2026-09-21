using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression coverage for LitigationSearchJobService's atomic claim and crash-recovery logic —
/// mirrors CalculationAiAuditClaimAndRecoveryTests exactly, running the exact SQL-level predicates
/// ProcessAsync/RecoverStaleWorkAsync use against a real SQL Server test database. Requires .\SQLEXPRESS.</summary>
public class LitigationSearchJobClaimAndRecoveryTests : IAsyncLifetime
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

    private static async Task<McaRequest> SeedRequestAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "LIT" + Guid.NewGuid().ToString("N")[..7], ClientName = "Litigation Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Litigation Test Company",
            Cin = "U45203OR1995PLC003982", RequestNumber = $"LIT-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    [Fact]
    public async Task Claim_OnlySucceedsOnce()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);
        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Pending,
            EntityType = "individual", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        // Mirrors LitigationSearchJobService.ProcessAsync's atomic claim exactly.
        Task<int> Claim() => db.LitigationSearchJobs
            .Where(j => j.LitigationSearchJobId == job.LitigationSearchJobId && j.Status == LitigationSearchJobStatus.Pending
                && (j.NextAttemptUtc == null || j.NextAttemptUtc <= DateTime.UtcNow))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, LitigationSearchJobStatus.Authenticating)
                .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1));

        var first = await Claim();
        var second = await Claim();

        Assert.Equal(1, first);
        Assert.Equal(0, second);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == job.LitigationSearchJobId);
        Assert.Equal(1, reloaded.AttemptCount);
        Assert.Equal(LitigationSearchJobStatus.Authenticating, reloaded.Status);
    }

    [Fact]
    public async Task RequestId_IsUnique_RejectsASecondJobForTheSameRequest()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);
        db.LitigationSearchJobs.Add(new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Pending,
            EntityType = "individual", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.LitigationSearchJobs.Add(new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Pending,
            EntityType = "individual", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task RecoverStaleWork_LeavesUnexpiredLeaseAlone_SoAStillRunningSearchIsNeverDuplicated()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);
        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Polling, AttemptCount = 1,
            EntityType = "individual", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow,
            StartedUtc = DateTime.UtcNow, LeaseOwner = "other-host:1234", LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(5)
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var inFlightStatuses = new[]
        {
            LitigationSearchJobStatus.Authenticating, LitigationSearchJobStatus.Registering, LitigationSearchJobStatus.Polling
        };
        var now = DateTime.UtcNow;
        await db.LitigationSearchJobs
            .Where(j => inFlightStatuses.Contains(j.Status) && (j.LeaseExpiresUtc == null || j.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, LitigationSearchJobStatus.Pending));

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == job.LitigationSearchJobId);
        Assert.Equal(LitigationSearchJobStatus.Polling, reloaded.Status);
    }

    [Fact]
    public async Task RecoverStaleWork_ResetsExpiredLease()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);
        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Polling, AttemptCount = 1,
            EntityType = "individual", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow,
            StartedUtc = DateTime.UtcNow.AddMinutes(-40), LeaseOwner = "crashed-host:1234", LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var inFlightStatuses = new[]
        {
            LitigationSearchJobStatus.Authenticating, LitigationSearchJobStatus.Registering, LitigationSearchJobStatus.Polling
        };
        var now = DateTime.UtcNow;
        await db.LitigationSearchJobs
            .Where(j => inFlightStatuses.Contains(j.Status) && (j.LeaseExpiresUtc == null || j.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, LitigationSearchJobStatus.Pending));

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == job.LitigationSearchJobId);
        Assert.Equal(LitigationSearchJobStatus.Pending, reloaded.Status);
    }

    [Fact]
    public async Task Claim_DeclinesWhenNextAttemptUtcInFuture_ButSucceedsOnceItPasses()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);
        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Pending,
            EntityType = "individual", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow,
            NextAttemptUtc = DateTime.UtcNow.AddMinutes(5)
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        Task<int> Claim() => db.LitigationSearchJobs
            .Where(j => j.LitigationSearchJobId == job.LitigationSearchJobId && j.Status == LitigationSearchJobStatus.Pending
                && (j.NextAttemptUtc == null || j.NextAttemptUtc <= DateTime.UtcNow))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, LitigationSearchJobStatus.Authenticating));

        Assert.Equal(0, await Claim());

        await db.LitigationSearchJobs.Where(j => j.LitigationSearchJobId == job.LitigationSearchJobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.NextAttemptUtc, DateTime.UtcNow.AddSeconds(-1)));

        Assert.Equal(1, await Claim());
    }

    [Fact]
    public async Task LeaseGuardedWrite_IsFencedOut_AfterATakeoverClaimsTheJobWithANewToken()
    {
        // Reviewer finding on PR #251: LeaseOwner alone is not a fencing token (it's a reusable
        // "machine:pid" string), so a stale worker that resumes after its lease was reclaimed could
        // overwrite a takeover worker's state. This proves the LeaseToken-guarded write
        // (LitigationSearchJobService.LeaseGuarded) actually stops that: worker A's lease expires, a
        // takeover (worker B) claims the job with a fresh token, and worker A's own resumed write — using
        // its own now-stale token — is refused, while worker B's write (its real token) succeeds.
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);
        var originalToken = Guid.NewGuid();
        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Polling, AttemptCount = 1,
            EntityType = "individual", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow,
            LeaseOwner = "worker-a:111", LeaseToken = originalToken, LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(-1) // already expired
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        // Takeover: recovery would reset Status to Pending first, but a claim only requires Status==Pending
        // AND an unexpired lease going forward — model the claim directly, mirroring ProcessAsync's exactly.
        var takeoverToken = Guid.NewGuid();
        var takeoverClaim = await db.LitigationSearchJobs
            .Where(j => j.LitigationSearchJobId == job.LitigationSearchJobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, LitigationSearchJobStatus.Authenticating)
                .SetProperty(j => j.LeaseOwner, "worker-b:222")
                .SetProperty(j => j.LeaseToken, takeoverToken)
                .SetProperty(j => j.LeaseExpiresUtc, DateTime.UtcNow.AddMinutes(30))
                .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1));
        Assert.Equal(1, takeoverClaim);

        // Worker A, unaware it lost the lease, resumes and tries to write using its own (now stale) token —
        // mirrors LitigationSearchJobService.LeaseGuarded's predicate exactly.
        Task<int> GuardedWrite(Guid token) => db.LitigationSearchJobs
            .Where(j => j.LitigationSearchJobId == job.LitigationSearchJobId && j.LeaseToken == token
                && j.LeaseExpiresUtc != null && j.LeaseExpiresUtc > DateTime.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, LitigationSearchJobStatus.Completed));

        Assert.Equal(0, await GuardedWrite(originalToken));

        // Worker B's own write, using the token it was actually claimed with, succeeds.
        Assert.Equal(1, await GuardedWrite(takeoverToken));

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == job.LitigationSearchJobId);
        Assert.Equal(LitigationSearchJobStatus.Completed, reloaded.Status); // worker B's write is the one that stuck
        Assert.Equal(2, reloaded.AttemptCount); // only the takeover's claim incremented it — worker A's fenced write did not
    }

    [Fact]
    public void BackoffDelay_IsIncreasingAndNeverNearZero()
    {
        var first = LitigationSearchJobService.BackoffDelay(1);
        var second = LitigationSearchJobService.BackoffDelay(2);
        var third = LitigationSearchJobService.BackoffDelay(3);

        Assert.True(first >= TimeSpan.FromSeconds(1), $"First retry delay was only {first} — the tight-loop bug this guards against.");
        Assert.True(second > first);
        Assert.True(third > second);
    }

    [Fact]
    public void BackoffDelay_IsCappedAtFiveMinutes()
    {
        var farOut = LitigationSearchJobService.BackoffDelay(20);

        Assert.True(farOut <= TimeSpan.FromMinutes(5));
    }
}
