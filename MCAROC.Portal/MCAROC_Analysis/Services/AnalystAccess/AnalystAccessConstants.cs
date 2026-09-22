using System.Security.Claims;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>
/// Stable names shared by the database-backed analyst sign-in flow and every analyst-only authorization
/// check. This is intentionally distinct from <c>InternalReviewer</c>: an analyst cookie must never
/// satisfy an internal-reviewer scheme or policy.
/// </summary>
public static class AnalystAccessConstants
{
    public const string AuthenticationScheme = "Analyst";
    public const string Policy = "Analyst";
    public const string Role = "Analyst";

    /// <summary>Opaque, stable database analyst ID. Do not put a username, email address, or raw claims in it.</summary>
    public const string AnalystIdClaimType = ClaimTypes.NameIdentifier;
}
