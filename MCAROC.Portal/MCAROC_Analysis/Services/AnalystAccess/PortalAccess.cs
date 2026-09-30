using System.Security.Claims;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>
/// Single switch for "may this user use every feature". Today there is one portal user with full access,
/// so any signed-in non-Analyst identity qualifies. When user access management arrives with client
/// onboarding, this is the one place (plus the fallback policy) to replace with real role checks.
/// </summary>
public static class PortalAccess
{
    public static bool HasFullAccess(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && !user.IsInRole(AnalystAccessConstants.Role);
}
