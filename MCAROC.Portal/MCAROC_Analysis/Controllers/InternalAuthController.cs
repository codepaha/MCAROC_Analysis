using System.Security.Claims;
using MCAROC_Analysis.Services.InternalAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MCAROC_Analysis.Controllers;

/// <summary>Sign-in for the #164 internal calculation-audit reviewer role. Deliberately minimal and
/// feature-scoped — this is not a retrofit of app-wide authentication; every other page stays exactly as
/// unauthenticated as it is today. A single generic failure message never distinguishes an unknown
/// username from a wrong password (no username enumeration).</summary>
[AllowAnonymous]
public class InternalAuthController(IConfiguration config) : Controller
{
    [HttpGet("/internal/login")]
    public IActionResult Login(string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost("/internal/login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("InternalLogin")]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;

        var configuredUsername = config["InternalAuth:ReviewerUsername"];
        var configuredHash = config["InternalAuth:ReviewerPasswordHash"];
        var displayName = config["InternalAuth:ReviewerDisplayName"];

        var valid = !string.IsNullOrEmpty(configuredUsername)
            && string.Equals(username, configuredUsername, StringComparison.Ordinal)
            && InternalReviewerCredentialChecker.Verify(password ?? "", configuredHash);

        if (!valid)
        {
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            return View();
        }

        // The second constructor argument (authenticationType) must be non-null for IsAuthenticated to be
        // true once this identity is wrapped in a ClaimsPrincipal and signed in.
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(displayName) ? configuredUsername! : displayName)],
            "InternalReviewer");
        await HttpContext.SignInAsync("InternalReviewer", new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        });

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : Redirect("/");
    }

    [HttpPost("/internal/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("InternalReviewer");
        return Redirect("/internal/login");
    }
}
