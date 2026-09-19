using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using MCAROC_Analysis.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MCAROC_Analysis.Services.Audit;

/// <summary>
/// Exception-aware dual-stage HTTP filter recording structured HttpMutation audit events.
/// Intercepts mutating verbs (POST, PUT, DELETE, PATCH). Captures unhandled action exceptions,
/// applies selective upload auditing policies, and records zero-trust actor metadata without retaining client IPs.
/// </summary>
public class AuditLogFilter(IAuditLogService auditService) : IAsyncActionFilter, IAsyncAlwaysRunResultFilter
{
    public const string AuditRecordedKey = "__MCAROC_AuditRecorded";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var verb = context.HttpContext.Request.Method;
        if (!IsMutatingVerb(verb))
        {
            await next();
            return;
        }

        var executed = await next();

        // Action method threw an unhandled exception before returning an IActionResult
        if (executed.Exception != null && !executed.ExceptionHandled)
        {
            context.HttpContext.Items[AuditRecordedKey] = true;

            var controller = context.RouteData.Values["controller"]?.ToString() ?? "";
            var action = context.RouteData.Values["action"]?.ToString() ?? "";
            var rule = AuditRouteRegistry.Resolve(controller, action);

            if (rule.Policy != AuditRulePolicy.Never)
            {
                var (actorType, actorId) = ResolveActor(context.HttpContext);
                var correlationId = CorrelationContext.GetOrCreate(context.HttpContext);
                var requestId = TryExtractRequestId(context.RouteData.Values, context.HttpContext.Request);
                var sanitized = AuditSanitizer.SanitizeAndCap(executed.Exception.Message, 500);

                await auditService.TryLogAsync(new AuditEvent<HttpMutationPayload>(
                    Action: rule.ActionType,
                    EventKind: AuditEventKind.HttpMutation,
                    Status: AuditStatus.Failure,
                    ActorType: actorType,
                    ActorId: actorId,
                    CorrelationId: correlationId,
                    RequestId: requestId,
                    ErrorMessage: sanitized,
                    Payload: new HttpMutationPayload(controller, action, 500)));
            }
        }
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.HttpContext.Items.ContainsKey(AuditRecordedKey))
        {
            await next();
            return;
        }

        var verb = context.HttpContext.Request.Method;
        if (!IsMutatingVerb(verb))
        {
            await next();
            return;
        }

        var controller = context.RouteData.Values["controller"]?.ToString() ?? "";
        var action = context.RouteData.Values["action"]?.ToString() ?? "";
        var rule = AuditRouteRegistry.Resolve(controller, action);

        if (rule.Policy == AuditRulePolicy.Never)
        {
            await next();
            return;
        }

        ResultExecutedContext executed;
        try
        {
            executed = await next();
        }
        catch (Exception ex)
        {
            context.HttpContext.Items[AuditRecordedKey] = true;
            var (actorType, actorId) = ResolveActor(context.HttpContext);
            var correlationId = CorrelationContext.GetOrCreate(context.HttpContext);
            var requestId = TryExtractRequestId(context.RouteData.Values, context.HttpContext.Request);
            var sanitized = AuditSanitizer.SanitizeAndCap(ex.Message, 500);

            await auditService.TryLogAsync(new AuditEvent<HttpMutationPayload>(
                Action: rule.ActionType,
                EventKind: AuditEventKind.HttpMutation,
                Status: AuditStatus.Failure,
                ActorType: actorType,
                ActorId: actorId,
                CorrelationId: correlationId,
                RequestId: requestId,
                ErrorMessage: sanitized,
                Payload: new HttpMutationPayload(controller, action, 500)));
            throw;
        }

        context.HttpContext.Items[AuditRecordedKey] = true;
        var statusCode = context.HttpContext.Response.StatusCode;
        var isFailure = statusCode >= 400;

        // Skip successful UploadChunk (FailuresOnly policy)
        if (rule.Policy == AuditRulePolicy.FailuresOnly && !isFailure)
        {
            return;
        }

        var (resolvedActorType, resolvedActorId) = ResolveActor(context.HttpContext);
        var resolvedCorrelationId = CorrelationContext.GetOrCreate(context.HttpContext);
        var resolvedRequestId = TryExtractRequestId(context.RouteData.Values, context.HttpContext.Request);
        var status = isFailure ? AuditStatus.Failure : AuditStatus.Success;

        await auditService.TryLogAsync(new AuditEvent<HttpMutationPayload>(
            Action: rule.ActionType,
            EventKind: AuditEventKind.HttpMutation,
            Status: status,
            ActorType: resolvedActorType,
            ActorId: resolvedActorId,
            CorrelationId: resolvedCorrelationId,
            RequestId: resolvedRequestId,
            ErrorMessage: isFailure ? $"HTTP {statusCode}" : null,
            Payload: new HttpMutationPayload(controller, action, statusCode)));
    }

    public static (ActorType ActorType, string ActorId) ResolveActor(HttpContext context)
    {
        var reviewerIdentity = context.User?.Identities
            ?.FirstOrDefault(i => i.AuthenticationType == "InternalReviewer" && i.IsAuthenticated);

        if (reviewerIdentity != null)
        {
            var actorId = reviewerIdentity.Name
                ?? context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? "InternalReviewer";
            return (ActorType.AuthenticatedReviewer, actorId);
        }

        return (ActorType.UnverifiedOperator, "Anonymous");
    }

    public static long? TryExtractRequestId(Microsoft.AspNetCore.Routing.RouteValueDictionary routeValues, HttpRequest request)
    {
        if (routeValues.TryGetValue("id", out var idVal) && long.TryParse(idVal?.ToString(), out var id))
            return id;

        if (routeValues.TryGetValue("requestId", out var reqIdVal) && long.TryParse(reqIdVal?.ToString(), out var reqId))
            return reqId;

        if (request.Query.TryGetValue("requestId", out var qVal) && long.TryParse(qVal.ToString(), out var qId))
            return qId;

        return null;
    }

    private static bool IsMutatingVerb(string method) =>
        method is "POST" or "PUT" or "DELETE" or "PATCH";
}
