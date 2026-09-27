using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #294 against a real SQL Server: retrieval reaches a reordered name through the word index; a
/// resolution and the request update happen together; nothing is written to the request unless an identifier
/// was chosen; a collision with the client's existing request never throws. Each test uses its own random
/// name token and CIN serial so the shared test database never mixes rows between tests.</summary>
public class IdentityResolutionServiceTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly string _word = "ZQ" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _serial = Random.Shared.Next(100_000, 999_999).ToString();
    private readonly string _llpin = new string(Enumerable.Range(0, 3).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray())
        + "-" + Random.Shared.Next(1000, 9999);
    private readonly List<long> _requests = [];

    private string Cin(int n) => $"U{n}0000MH2020PTC{_serial}";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static IdentityResolutionService Service(AppDbContext db, bool autoSelect) =>
        new(db, Options.Create(new ResolverOptions { AutoSelectEnabled = autoSelect }));

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        await db.IdentityResolutions.Where(r => r.RequestId != null && _requests.Contains(r.RequestId.Value)).ExecuteDeleteAsync();
        await db.Requests.Where(r => _requests.Contains(r.RequestId)).ExecuteDeleteAsync();
        await db.CompanyNameTokens.Where(t => t.Identifier.EndsWith(_serial) || t.Identifier == _llpin).ExecuteDeleteAsync();
        await db.CompanyMasterRecords.Where(r => r.Identifier.EndsWith(_serial) || r.Identifier == _llpin).ExecuteDeleteAsync();
    }

    /// <summary>Seeds master rows, then fills their derived columns and the word index the way a deployment does.</summary>
    private async Task SeedMasterAsync(params (string Identifier, string Name, CompanyMasterRecordType Type)[] rows)
    {
        await using (var db = CreateContext())
        {
            foreach (var (id, name, type) in rows)
                db.CompanyMasterRecords.Add(new CompanyMasterRecord { Identifier = id, RecordType = type, Name = name, Status = "Active" });
            await db.SaveChangesAsync();
        }
        await using var connection = new SqlConnection(ConnectionString);
        await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);
        await CompanyNameTokenIndex.RebuildAsync(connection);
    }

    private async Task<long> NewRequestAsync(string? identifier = null)
    {
        await using var db = CreateContext();
        var request = new McaRequest
        {
            ClientId = 1, CompanyName = "pending", RequestNumber = $"TEST-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow,
            EntityType = EntityType.Company, Cin = identifier, AutoFetchCompanyIdentifier = identifier
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        _requests.Add(request.RequestId);
        return request.RequestId;
    }

    [Fact]
    public async Task Retrieval_ReachesAReorderedName_ThroughTheWordIndex()
    {
        await SeedMasterAsync((Cin(1), $"{_word} SUNRISE ORCHARDS PRIVATE LIMITED", CompanyMasterRecordType.Company));
        await using var connection = new SqlConnection(ConnectionString);

        var candidates = await CompanyNameCandidateRetriever.RetrieveAsync(connection, $"Orchards Sunrise {_word} Pvt Ltd");
        var ranked = CompanyNameResolver.Rank($"Orchards Sunrise {_word} Pvt Ltd", ResolutionHints.None, candidates);

        Assert.Equal(Cin(1), ranked[0].Candidate.Identifier);
        Assert.Equal(0, ranked[0].Features["ExactNormalized"]);
    }

    [Fact]
    public async Task AutoSelectDisabled_RecordsSuggestion_AndLeavesTheRequestUntouched()
    {
        await SeedMasterAsync((Cin(1), $"{_word} TEXTILES PRIVATE LIMITED", CompanyMasterRecordType.Company));
        var requestId = await NewRequestAsync();
        await using var db = CreateContext();

        var result = await Service(db, autoSelect: false)
            .ResolveForRequestAsync(requestId, $"{_word} Textiles Pvt Ltd", null, ResolutionHints.None, "test");

        Assert.Equal(ResolutionStatus.NeedsConfirmation, result.Decision.Status);
        Assert.False(result.AppliedToRequest);
        await using var check = CreateContext();
        Assert.Null((await check.Requests.SingleAsync(r => r.RequestId == requestId)).AutoFetchCompanyIdentifier);
        var row = await check.IdentityResolutions.SingleAsync(r => r.IdentityResolutionId == result.IdentityResolutionId);
        Assert.Equal(Cin(1), row.RecommendedIdentifier);
        Assert.True(row.AutoSelectEligible);
        Assert.Equal(CompanyNameResolver.AlgorithmVersion, row.AlgorithmVersion);
        Assert.Contains(Cin(1), row.CandidatesJson);
    }

    [Fact]
    public async Task AutoSelectEnabled_WritesTheIdentifierAndTheAuditRowTogether()
    {
        await SeedMasterAsync((Cin(1), $"{_word} TEXTILES PRIVATE LIMITED", CompanyMasterRecordType.Company));
        var requestId = await NewRequestAsync();
        await using var db = CreateContext();

        var result = await Service(db, autoSelect: true)
            .ResolveForRequestAsync(requestId, $"{_word} Textiles Pvt Ltd", null, ResolutionHints.None, "test");

        Assert.True(result.AppliedToRequest);
        await using var check = CreateContext();
        var request = await check.Requests.SingleAsync(r => r.RequestId == requestId);
        Assert.Equal(Cin(1), request.AutoFetchCompanyIdentifier);
        Assert.Equal(Cin(1), request.Cin);
        Assert.Equal(EntityType.Company, request.EntityType);
        var row = await check.IdentityResolutions.SingleAsync(r => r.IdentityResolutionId == result.IdentityResolutionId);
        Assert.Equal(ResolutionMethod.AutoSelected, row.Method);
        Assert.True(row.AppliedToRequest);
    }

    [Fact]
    public async Task CollisionWithTheClientsExistingRequest_IsRecorded_NotThrown()
    {
        await SeedMasterAsync((Cin(1), $"{_word} TEXTILES PRIVATE LIMITED", CompanyMasterRecordType.Company));
        var existing = await NewRequestAsync(Cin(1));
        var requestId = await NewRequestAsync();
        await using var db = CreateContext();

        var result = await Service(db, autoSelect: false)
            .ResolveForRequestAsync(requestId, null, Cin(1), ResolutionHints.None, "test");

        Assert.False(result.AppliedToRequest);
        Assert.Equal(existing, result.ExistingRequestId);
        await using var check = CreateContext();
        var row = await check.IdentityResolutions.SingleAsync(r => r.IdentityResolutionId == result.IdentityResolutionId);
        Assert.Equal(ResolutionReasonCodes.DuplicateRequest, row.ReasonCode);
        Assert.Null((await check.Requests.SingleAsync(r => r.RequestId == requestId)).AutoFetchCompanyIdentifier);
    }

    [Fact]
    public async Task HumanSelection_OfAnLlp_IsAppliedAsHumanSelected()
    {
        var llpin = _llpin;
        await SeedMasterAsync((llpin, $"{_word} ADVISORS LLP", CompanyMasterRecordType.Llp));
        var requestId = await NewRequestAsync();
        await using var db = CreateContext();

        var result = await Service(db, autoSelect: false)
            .ApplyHumanSelectionAsync(requestId, llpin, $"{_word} Advisors", ResolutionHints.None, "reviewer@test");

        Assert.True(result.AppliedToRequest);
        Assert.Equal(ResolutionMethod.HumanSelected, result.Decision.Method);
        await using var check = CreateContext();
        var request = await check.Requests.SingleAsync(r => r.RequestId == requestId);
        Assert.Equal(EntityType.LLP, request.EntityType);
        Assert.Equal(llpin, request.Llpin);
    }

    [Fact]
    public async Task UserProvidedIdentifier_NotInMaster_IsNotApplied()
    {
        var requestId = await NewRequestAsync();
        await using var db = CreateContext();

        var result = await Service(db, autoSelect: true)
            .ResolveForRequestAsync(requestId, null, Cin(9), ResolutionHints.None, "test");

        Assert.Equal(ResolutionStatus.NotFound, result.Decision.Status);
        Assert.False(result.AppliedToRequest);
    }
}
