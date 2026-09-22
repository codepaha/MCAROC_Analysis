using System.Security.Claims;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>Single source of truth for analyst request visibility. Controllers must start from
/// <see cref="AccessibleRequests"/> or call <see cref="CanAccessAsync"/>; browser-supplied request,
/// client, and analyst identifiers are never authority.</summary>
public interface IAnalystRequestAccessService
{
    IQueryable<McaRequest> AccessibleRequests(long analystId);
    Task<bool> CanAccessAsync(ClaimsPrincipal principal, long requestId, CancellationToken ct = default);
    bool TryGetAnalystId(ClaimsPrincipal principal, out long analystId);
}

public sealed class AnalystRequestAccessService(AppDbContext db) : IAnalystRequestAccessService
{
    public IQueryable<McaRequest> AccessibleRequests(long analystId) =>
        db.Requests.Where(request => db.AnalystAssignments.Any(assignment =>
            assignment.RequestId == request.RequestId && assignment.AnalystId == analystId && assignment.Analyst!.IsActive));

    public bool TryGetAnalystId(ClaimsPrincipal principal, out long analystId) =>
        long.TryParse(principal.FindFirstValue(AnalystAccessConstants.AnalystIdClaimType), out analystId) && analystId > 0;

    public Task<bool> CanAccessAsync(ClaimsPrincipal principal, long requestId, CancellationToken ct = default)
    {
        if (!TryGetAnalystId(principal, out var analystId))
            return Task.FromResult(false);

        return db.AnalystAssignments.AnyAsync(assignment =>
            assignment.RequestId == requestId && assignment.AnalystId == analystId && assignment.Analyst!.IsActive, ct);
    }
}
