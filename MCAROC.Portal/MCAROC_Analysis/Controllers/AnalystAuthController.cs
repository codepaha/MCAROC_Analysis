using System.Security.Claims;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using MCAROC_Analysis.Services.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>
/// Database-managed sign-in for the staged analyst role. This remains separate from InternalReviewer:
/// the cookie grants no access except to endpoints that opt into the Analyst policy and resource check.
/// </summary>
[AllowAnonymous]
public sealed class AnalystAuthController(
    AppDbContext db,
    IAnalystPasswordHasher passwordHasher,
    IAuditLogService auditLog) : Controller
{
    [HttpGet("/analyst/login")]
    public IActionResult Login(string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost("/analyst/login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("AnalystLogin")]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl, CancellationToken ct)
    {
        ViewData["ReturnUrl"] = returnUrl;
        var loginName = NormalizeLoginName(username);
        var analyst = string.IsNullOrEmpty(loginName)
            ? null
            : await db.Analysts.SingleOrDefaultAsync(item => item.LoginName == loginName && item.IsActive, ct);
        var verification = analyst is null
            ? PasswordVerificationResult.Failed
            : passwordHasher.Verify(password ?? string.Empty, analyst.PasswordHash);

        if (verification == PasswordVerificationResult.Failed)
        {
            await LogAsync(ActorType.UnverifiedOperator, "Anonymous", AuditStatus.Failure, ct);
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            return View();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            analyst!.PasswordHash = passwordHasher.Hash(password);
            await db.SaveChangesAsync(ct);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(AnalystAccessConstants.AnalystIdClaimType, analyst!.AnalystId.ToString()),
                new Claim(ClaimTypes.Name, analyst.DisplayName),
                new Claim(ClaimTypes.Role, AnalystAccessConstants.Role)
            ],
            AnalystAccessConstants.AuthenticationScheme);
        await HttpContext.SignInAsync(AnalystAccessConstants.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
        await LogAsync(ActorType.AuthenticatedAnalyst, analyst.AnalystId.ToString(), AuditStatus.Success, ct);

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : Redirect("/");
    }

    [Authorize(Policy = AnalystAccessConstants.Policy)]
    [HttpPost("/analyst/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var actorId = User.FindFirstValue(AnalystAccessConstants.AnalystIdClaimType) ?? "Unknown";
        await HttpContext.SignOutAsync(AnalystAccessConstants.AuthenticationScheme);
        await LogAsync(ActorType.AuthenticatedAnalyst, actorId, AuditStatus.Success, ct, AuditActionType.AnalystLoggedOut);
        return Redirect("/analyst/login");
    }

    internal static string NormalizeLoginName(string? loginName) => loginName?.Trim().ToUpperInvariant() ?? string.Empty;

    private Task LogAsync(ActorType actorType, string actorId, AuditStatus status, CancellationToken ct,
        AuditActionType action = AuditActionType.AnalystLoginAttempted) =>
        auditLog.TryLogAsync(new AuditEvent(
            action,
            AuditEventKind.HttpMutation,
            status,
            actorType,
            actorId,
            CorrelationContext.GetOrCreate(HttpContext)), ct);
}
