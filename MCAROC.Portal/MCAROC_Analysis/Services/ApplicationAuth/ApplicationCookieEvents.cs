using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MCAROC_Analysis.Services.ApplicationAuth;

public sealed class ApplicationCookieEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var credentials = context.HttpContext.RequestServices.GetRequiredService<ApplicationCredentialChecker>();
        if (context.Principal?.FindFirst("credential-version")?.Value != credentials.Version)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync("ApplicationUser");
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        // Fetch/XHR and mutation clients should not receive a login document as their response.
        if (!HttpMethods.IsGet(context.Request.Method)
            || context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.ApiControllerAttribute>() is not null
            || context.Request.Path.StartsWithSegments("/api")
            || context.Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || context.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
