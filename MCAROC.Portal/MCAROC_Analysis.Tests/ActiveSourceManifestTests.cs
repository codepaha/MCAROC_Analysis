using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class ActiveSourceManifestTests
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    [Fact]
    public async Task CanQueryAndEnforceSingleActiveSource_PerRequestAndDocumentType()
    {
        await using var db = CreateContext();

        var client = new Client { ClientCode = "MANI_" + Guid.NewGuid().ToString("N")[..6], ClientName = "Manifest Client", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);

        var request = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "Manifest Corp",
            RequestNumber = "MANI-" + Guid.NewGuid().ToString("N")[..8],
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var doc1 = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "roc1.xlsx",
            StoredFileName = "roc1.xlsx",
            StoragePath = @"C:\test\roc1.xlsx",
            FileHash = "hash1",
            UploadedDate = DateTime.UtcNow,
            IsActiveSource = true
        };
        db.RequestDocuments.Add(doc1);
        await db.SaveChangesAsync();

        // Verify active source can be queried deterministically
        var active = await db.RequestDocuments
            .FirstOrDefaultAsync(d => d.RequestId == request.RequestId && d.DocumentType == DocumentType.McaRocReport && d.IsActiveSource);
        Assert.NotNull(active);
        Assert.Equal(doc1.DocumentId, active.DocumentId);

        // Transition: supersede doc1 with doc2
        var doc2 = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "roc2.xlsx",
            StoredFileName = "roc2.xlsx",
            StoragePath = @"C:\test\roc2.xlsx",
            FileHash = "hash2",
            UploadedDate = DateTime.UtcNow,
            IsActiveSource = false
        };
        db.RequestDocuments.Add(doc2);
        await db.SaveChangesAsync();

        // Swap: doc1 -> inactive superseded by doc2; doc2 -> active
        doc1.IsActiveSource = false;
        doc1.SupersededByDocumentId = doc2.DocumentId;
        doc2.IsActiveSource = true;
        await db.SaveChangesAsync();

        var newActive = await db.RequestDocuments
            .FirstOrDefaultAsync(d => d.RequestId == request.RequestId && d.DocumentType == DocumentType.McaRocReport && d.IsActiveSource);
        Assert.NotNull(newActive);
        Assert.Equal(doc2.DocumentId, newActive.DocumentId);

        var oldDoc = await db.RequestDocuments.FindAsync(doc1.DocumentId);
        Assert.False(oldDoc!.IsActiveSource);
        Assert.Equal(doc2.DocumentId, oldDoc.SupersededByDocumentId);

        // Cleanup
        db.RequestDocuments.Remove(doc1);
        db.RequestDocuments.Remove(doc2);
        db.Requests.Remove(request);
        db.Clients.Remove(client);
        await db.SaveChangesAsync();
    }
}
