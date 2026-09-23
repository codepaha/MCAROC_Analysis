using System.Security.Claims;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>Builds the analyst's queue from the one assignment-scoped query root.</summary>
public sealed class AnalystDashboardQueryService(AppDbContext db, IAnalystRequestAccessService access)
{
    private const int PageSize = 25;
    private const int MaximumPage = 10_000;

    public async Task<AnalystDashboardViewModel?> BuildAsync(
        ClaimsPrincipal principal,
        string? search,
        RequestStatus? status,
        int page,
        CancellationToken ct = default)
    {
        if (!access.TryGetAnalystId(principal, out var analystId))
            return null;

        var analystName = principal.Identity?.Name ?? "Analyst";
        var assigned = access.AccessibleRequests(analystId).AsNoTracking();
        var processingStatuses = RequestRiskDefinitions.ProcessingStatuses
            .Where(value => value != RequestStatus.Created)
            .ToArray();

        var assignedCount = await assigned.CountAsync(ct);
        var awaitingUploadCount = await assigned.CountAsync(request => request.RequestStatus == RequestStatus.Created, ct);
        var processingCount = await assigned.CountAsync(request => processingStatuses.Contains(request.RequestStatus), ct);
        var reviewNeededCount = await assigned.CountAsync(request => request.IsManualReviewRequired, ct);
        var readyCount = await assigned.CountAsync(request => request.RequestStatus == RequestStatus.AnalysisCompleted, ct);
        var failedOrAttentionCount = await assigned.CountAsync(request =>
            RequestRiskDefinitions.FailedStatuses.Contains(request.RequestStatus)
            || request.IsManualReviewRequired
            || request.HasIngestionWarnings, ct);

        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim()[..Math.Min(search.Trim().Length, 100)];
        var filtered = assigned.AsQueryable();
        if (status is { } selectedStatus)
            filtered = filtered.Where(request => request.RequestStatus == selectedStatus);
        if (normalizedSearch is not null)
        {
            filtered = filtered.Where(request => request.CompanyName.Contains(normalizedSearch)
                || request.RequestNumber.Contains(normalizedSearch)
                || (request.Cin != null && request.Cin.Contains(normalizedSearch))
                || (request.Llpin != null && request.Llpin.Contains(normalizedSearch)));
        }

        var totalFilteredCount = await filtered.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalFilteredCount / (double)PageSize));
        var safePage = Math.Clamp(page, 1, Math.Min(pageCount, MaximumPage));
        var requests = await filtered
            .OrderByDescending(request => request.CreatedDate)
            .ThenByDescending(request => request.RequestId)
            .Skip((safePage - 1) * PageSize)
            .Take(PageSize)
            .Select(request => new AnalystQueueItemViewModel
            {
                RequestId = request.RequestId,
                RequestNumber = request.RequestNumber,
                CompanyName = request.CompanyName,
                Cin = request.Cin,
                Llpin = request.Llpin,
                EntityType = request.EntityType,
                Status = request.RequestStatus,
                CreatedUtc = request.CreatedDate,
                NeedsReview = request.IsManualReviewRequired,
                AttentionReason = request.ManualReviewReason
                    ?? request.FailureReason
                    ?? (RequestRiskDefinitions.FailedStatuses.Contains(request.RequestStatus)
                        ? "Processing failed"
                        : request.HasIngestionWarnings ? "Ingestion warnings" : null)
            })
            .ToListAsync(ct);

        return new AnalystDashboardViewModel
        {
            AnalystName = analystName,
            AssignedCount = assignedCount,
            AwaitingUploadCount = awaitingUploadCount,
            ProcessingCount = processingCount,
            ReviewNeededCount = reviewNeededCount,
            ReadyCount = readyCount,
            FailedOrAttentionCount = failedOrAttentionCount,
            Search = normalizedSearch,
            Status = status,
            Page = safePage,
            PageSize = PageSize,
            TotalFilteredCount = totalFilteredCount,
            Requests = requests
        };
    }
}
