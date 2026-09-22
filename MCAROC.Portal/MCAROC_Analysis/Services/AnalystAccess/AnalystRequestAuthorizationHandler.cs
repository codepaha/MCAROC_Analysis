using MCAROC_Analysis.Data.Entities;
using Microsoft.AspNetCore.Authorization;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>Resource authorization for an already-resolved McaRequest. It deliberately performs a fresh
/// database assignment check on every request, so reassignment/revocation takes effect immediately.</summary>
public sealed class AnalystRequestAccessRequirement : IAuthorizationRequirement;

public sealed class AnalystRequestAuthorizationHandler(IAnalystRequestAccessService access)
    : AuthorizationHandler<AnalystRequestAccessRequirement, McaRequest>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AnalystRequestAccessRequirement requirement,
        McaRequest resource)
    {
        if (await access.CanAccessAsync(context.User, resource.RequestId))
            context.Succeed(requirement);
    }
}
