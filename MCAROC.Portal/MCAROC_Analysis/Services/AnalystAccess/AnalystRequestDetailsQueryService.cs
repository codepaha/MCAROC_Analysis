using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>Loads the deliberately limited, read-only analyst detail page after a fresh assignment check.</summary>
public sealed class AnalystRequestDetailsQueryService(AppDbContext db, IAnalystRequestAccessService access)
{
    public async Task<AnalystRequestDetailViewModel?> BuildAsync(
        System.Security.Claims.ClaimsPrincipal principal, long requestId, CancellationToken ct = default)
    {
        if (requestId <= 0
            || !principal.IsInRole(AnalystAccessConstants.Role)
            || !access.TryGetAnalystId(principal, out var analystId))
            return null;

        var request = await access.AccessibleRequests(analystId).AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .Select(item => new AnalystRequestDetailViewModel
            {
                RequestId = item.RequestId,
                RequestNumber = item.RequestNumber,
                CompanyName = item.CompanyName,
                EntityType = item.EntityType,
                Cin = item.Cin,
                Llpin = item.Llpin,
                Pan = item.Pan,
                Status = item.RequestStatus,
                CreatedUtc = item.CreatedDate,
                UpdatedUtc = item.UpdatedDate,
                NeedsReview = item.IsManualReviewRequired || RequestRiskDefinitions.FailedStatuses.Contains(item.RequestStatus),
                AttentionReason = item.ManualReviewReason ?? item.FailureReason
            })
            .SingleOrDefaultAsync(ct);

        if (request is null)
            return null;

        var documents = await db.RequestDocuments.AsNoTracking()
            .Where(document => document.RequestId == requestId
                && document.UploadStatus != DocumentUploadStatus.Quarantined
                && db.AnalystAssignments.Any(assignment => assignment.RequestId == document.RequestId
                    && assignment.AnalystId == analystId && assignment.Analyst!.IsActive))
            .OrderByDescending(document => document.UploadedDate)
            .Select(document => new
            {
                DocumentId = document.DocumentId,
                document.OriginalFileName,
                Type = document.DocumentType,
                Status = document.UploadStatus,
                UploadedUtc = document.UploadedDate
            })
            .ToListAsync(ct);

        return new AnalystRequestDetailViewModel
        {
            RequestId = request.RequestId,
            RequestNumber = request.RequestNumber,
            CompanyName = request.CompanyName,
            EntityType = request.EntityType,
            Cin = request.Cin,
            Llpin = request.Llpin,
            Pan = request.Pan,
            Status = request.Status,
            CreatedUtc = request.CreatedUtc,
            UpdatedUtc = request.UpdatedUtc,
            NeedsReview = request.NeedsReview,
            AttentionReason = request.AttentionReason,
            Documents = documents.Select(document => new AnalystRequestDocumentViewModel
            {
                DocumentId = document.DocumentId,
                FileName = SafeDisplayName(document.OriginalFileName),
                Type = document.Type,
                Status = document.Status,
                UploadedUtc = document.UploadedUtc
            }).ToList()
        };
    }

    private static string SafeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Uploaded document";
        var leaf = Path.GetFileName(value.Replace('\\', '/'));
        var clean = new string(leaf.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Uploaded document" : clean;
    }
}
