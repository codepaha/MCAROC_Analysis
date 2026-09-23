using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>
/// Keeps the Analyst cookie separate from the portal's default (anonymous) identity while enforcing
/// an explicit request-route allowlist whenever that cookie is present. New routes are denied by default.
/// </summary>
public sealed class AnalystRequestBoundaryFilter(IAnalystRequestAccessService access) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var auth = await context.HttpContext.AuthenticateAsync(AnalystAccessConstants.AuthenticationScheme);
        if (!auth.Succeeded || auth.Principal?.Identity?.IsAuthenticated != true)
            return;

        var principal = auth.Principal;
        if (!principal.IsInRole(AnalystAccessConstants.Role))
        {
            context.Result = new ForbidResult(AnalystAccessConstants.AuthenticationScheme);
            return;
        }

        context.HttpContext.User = principal;
        var controller = RouteValue(context, "controller");
        var action = RouteValue(context, "action");

        // These existing endpoints independently enforce the Analyst policy. Keep the allowlist action-specific
        // so a later action added to one of these controllers does not become an Analyst route by accident.
        if ((controller == "AnalystAuth" && (action == "Login" || action == "Logout"))
            || (controller == "AnalystDashboard" && action == "Index"))
            return;

        var requestId = ReadPositiveLong(RouteValue(context, "requestId"))
            ?? ReadPositiveLong(RouteValue(context, "id"));

        if (controller == "AnalystRequest" && action == "Details")
        {
            if (requestId is null || !await access.CanAccessAsync(principal, requestId.Value))
                context.Result = new NotFoundResult();
            return;
        }

        if (controller == "Requests" && action == "Details")
        {
            if (requestId is null || !await access.CanAccessAsync(principal, requestId.Value))
            {
                context.Result = new NotFoundResult();
                return;
            }

            // Never render the legacy reviewer-oriented request view to an Analyst.
            context.Result = new RedirectResult($"/analyst/requests/{requestId.Value}");
            return;
        }

        if (requestId is null)
        {
            context.Result = new ForbidResult(AnalystAccessConstants.AuthenticationScheme);
            return;
        }

        // Hide whether a guessed request exists before deciding whether this particular route is
        // permitted for an assigned Analyst.
        if (!await access.CanAccessAsync(principal, requestId.Value))
        {
            context.Result = new NotFoundResult();
            return;
        }

        // The existing uploaded-source download has its own document ownership, quarantine, path,
        // and extension checks. Every other legacy route (including mutations, paid/external actions,
        // and reviewer views) remains denied even for assigned requests.
        if (controller == "Requests" && action == "DownloadUploadedDocument")
            return;

        context.Result = new ForbidResult(AnalystAccessConstants.AuthenticationScheme);
    }

    private static string? RouteValue(AuthorizationFilterContext context, string key)
    {
        if (context.RouteData.Values.TryGetValue(key, out var value) && value is not null)
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        return context.ActionDescriptor.RouteValues.TryGetValue(key, out var descriptorValue)
            ? descriptorValue
            : null;
    }

    private static long? ReadPositiveLong(string? value) =>
        long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var result)
        && result > 0 ? result : null;
}
