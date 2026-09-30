using System.Security.Claims;
using MCAROC_Analysis.Services.ApplicationAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MCAROC_Analysis.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ApplicationAuthController(ApplicationCredentialChecker credentials) : Controller
{
    [AllowAnonymous]
    [HttpGet("/login")]
    public IActionResult Login(string? returnUrl)
    {
        ViewData["ReturnUrl"] = Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        return View();
    }

    [AllowAnonymous]
    [HttpPost("/login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ApplicationLogin")]
    public async Task<IActionResult> Login(string? username, string? password, string? returnUrl)
    {
        ViewData["ReturnUrl"] = Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        ViewData["Username"] = username;
        if (!credentials.Verify(username, password))
        {
            ModelState.AddModelError(string.Empty, "Invalid login ID or password.");
            return View();
        }
        // Switching to the full application account must not retain a restricted analyst identity.
        await HttpContext.SignOutAsync("Analyst");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, credentials.Username),
            new Claim("credential-version", credentials.Version)
        ], "ApplicationUser");
        await HttpContext.SignInAsync("ApplicationUser", new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [Authorize(AuthenticationSchemes = "ApplicationUser")]
    [HttpPost("/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("ApplicationUser");
        return Redirect("/login");
    }
}
