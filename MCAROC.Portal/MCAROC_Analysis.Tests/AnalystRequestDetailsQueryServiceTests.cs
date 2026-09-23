using System.Security.Claims;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public sealed class AnalystRequestDetailsQueryServiceTests : IAsyncLifetime
{
    private IsolatedDatabase _database = null!;
    private long _analystId;
    private long _inactiveAnalystId;
    private long _assignedRequestId;
    private long _unassignedRequestId;
    private long _availableDocumentId;
    private long _quarantinedDocumentId;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateIsolatedDatabaseAsync("AnalystDetails");
        await using var db = CreateContext();
        var analyst = Analyst("ACTIVE");
        var inactiveAnalyst = Analyst("INACTIVE", active: false);
        var client = new Client
        {
            ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Analyst detail test", CreatedDate = DateTime.UtcNow
        };
        var assigned = Request(client, "Assigned synthetic company");
        var unassigned = Request(client, "Unassigned synthetic company");
        db.AddRange(analyst, inactiveAnalyst, client, assigned, unassigned);
        await db.SaveChangesAsync();
        _analystId = analyst.AnalystId;
        _inactiveAnalystId = inactiveAnalyst.AnalystId;
        _assignedRequestId = assigned.RequestId;
        _unassignedRequestId = unassigned.RequestId;

        db.AnalystAssignments.Add(new AnalystAssignment
        {
            AnalystId = _analystId, RequestId = _assignedRequestId, AssignedUtc = DateTime.UtcNow,
            AssignedByActorId = "test-operator"
        });
        db.AnalystAssignments.Add(new AnalystAssignment
        {
            AnalystId = _inactiveAnalystId, RequestId = _unassignedRequestId, AssignedUtc = DateTime.UtcNow,
            AssignedByActorId = "test-operator"
        });
        var available = Document(_assignedRequestId, "..\\source\\roc-report.pdf", DocumentUploadStatus.Processed);
        var quarantined = Document(_assignedRequestId, "private-quarantined.pdf", DocumentUploadStatus.Quarantined);
        db.RequestDocuments.AddRange(available, quarantined);
        await db.SaveChangesAsync();
        _availableDocumentId = available.DocumentId;
        _quarantinedDocumentId = quarantined.DocumentId;
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task BuildAsync_ReturnsOnlyAssignedSummaryAndNonQuarantinedDocumentMetadata()
    {
        await using var db = CreateContext();
        var service = new AnalystRequestDetailsQueryService(db, new AnalystRequestAccessService(db));

        var result = await service.BuildAsync(Principal(_analystId), _assignedRequestId);

        Assert.NotNull(result);
        Assert.Equal(_assignedRequestId, result.RequestId);
        Assert.Equal("Assigned synthetic company", result.CompanyName);
        var document = Assert.Single(result.Documents);
        Assert.Equal(_availableDocumentId, document.DocumentId);
        Assert.Equal("roc-report.pdf", document.FileName);
        Assert.DoesNotContain(result.Documents, item => item.DocumentId == _quarantinedDocumentId);
    }

    [Fact]
    public async Task BuildAsync_ReturnsNoDataForUnassignedInactiveOrMalformedAnalystPrincipal()
    {
        await using var db = CreateContext();
        var service = new AnalystRequestDetailsQueryService(db, new AnalystRequestAccessService(db));

        Assert.Null(await service.BuildAsync(Principal(_analystId), _unassignedRequestId));
        Assert.Null(await service.BuildAsync(Principal(_inactiveAnalystId), _assignedRequestId));
        Assert.Null(await service.BuildAsync(new ClaimsPrincipal(new ClaimsIdentity()), _assignedRequestId));
    }

    private AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(_database.ConnectionString).Options);

    private static ClaimsPrincipal Principal(long analystId) => new(new ClaimsIdentity(
        [new Claim(AnalystAccessConstants.AnalystIdClaimType, analystId.ToString()),
         new Claim(ClaimTypes.Role, AnalystAccessConstants.Role)],
        AnalystAccessConstants.AuthenticationScheme));

    private static Analyst Analyst(string prefix, bool active = true) => new()
    {
        LoginName = $"{prefix}-{Guid.NewGuid():N}".ToUpperInvariant(), PasswordHash = "test",
        DisplayName = "Synthetic analyst", IsActive = active, CreatedUtc = DateTime.UtcNow,
        DisabledUtc = active ? null : DateTime.UtcNow
    };

    private static McaRequest Request(Client client, string companyName) => new()
    {
        Client = client, EntityType = EntityType.Company, CompanyName = companyName,
        RequestNumber = $"TEST-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow
    };

    private static RequestDocument Document(long requestId, string fileName, DocumentUploadStatus status) => new()
    {
        RequestId = requestId, DocumentType = DocumentType.McaRocReport, OriginalFileName = fileName,
        StoredFileName = $"{Guid.NewGuid():N}.pdf", StoragePath = "synthetic-only", FileSize = 12,
        FileHash = Guid.NewGuid().ToString("N"), UploadStatus = status, UploadedDate = DateTime.UtcNow
    };
}
