using System.Collections.Generic;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Audit;

/// <summary>
/// Authoritative registry mapping MVC controller and action names to audit action types and logging policies.
/// Provides safe non-nullable fallbacks for unmapped mutating routes.
/// </summary>
public static class AuditRouteRegistry
{
    public static readonly IReadOnlyDictionary<(string Controller, string Action), (AuditActionType ActionType, AuditRulePolicy Policy)> RouteMap =
        new Dictionary<(string, string), (AuditActionType, AuditRulePolicy)>
        {
            // Requests
            { ("Requests", "New"), (AuditActionType.RequestCreated, AuditRulePolicy.Always) },
            { ("Requests", "Reingest"), (AuditActionType.ReingestionStarted, AuditRulePolicy.Always) },
            { ("Requests", "AddSourceWorkbooks"), (AuditActionType.WorkbooksAdded, AuditRulePolicy.Always) },
            { ("Requests", "AskChat"), (AuditActionType.ChatMessageSent, AuditRulePolicy.Always) },
            { ("Requests", "RetryDocumentChunking"), (AuditActionType.ChunkingRetryRequested, AuditRulePolicy.Always) },

            // AutoFetch
            { ("AutoFetch", "New"), (AuditActionType.AutoFetchRequested, AuditRulePolicy.Always) },
            { ("AutoFetch", "Retry"), (AuditActionType.AutoFetchRetried, AuditRulePolicy.Always) },

            // RequestsUpload
            { ("RequestsUpload", "Initiate"), (AuditActionType.ArchiveUploadInitiated, AuditRulePolicy.Always) },
            { ("RequestsUpload", "UploadChunk"), (AuditActionType.ArchiveUploadChunkFailed, AuditRulePolicy.FailuresOnly) },
            { ("RequestsUpload", "Complete"), (AuditActionType.ArchiveUploadCompleted, AuditRulePolicy.Always) },
            { ("RequestsUpload", "Abort"), (AuditActionType.ArchiveUploadAborted, AuditRulePolicy.Always) },
            { ("RequestsUpload", "Heartbeat"), (AuditActionType.OtherMutation, AuditRulePolicy.Never) },

            // Clients
            { ("Clients", "Edit"), (AuditActionType.ClientEdited, AuditRulePolicy.Always) },

            // InternalAuth
            { ("InternalAuth", "Login"), (AuditActionType.InternalLoginAttempted, AuditRulePolicy.Always) },
            { ("InternalAuth", "Logout"), (AuditActionType.InternalLoggedOut, AuditRulePolicy.Always) },

            // PreLoginReports
            { ("PreLoginReports", "Fetch"), (AuditActionType.PreLoginReportFetched, AuditRulePolicy.Always) },
            { ("PreLoginReports", "Batch"), (AuditActionType.PreLoginReportBatched, AuditRulePolicy.Always) },
            { ("PreLoginReports", "Edit"), (AuditActionType.PreLoginReportEdited, AuditRulePolicy.Always) },
            { ("PreLoginReports", "Rerun"), (AuditActionType.PreLoginReportRerun, AuditRulePolicy.Always) },

            // Chat
            { ("Chat", "Ask"), (AuditActionType.ChatMessageSent, AuditRulePolicy.Always) },

            // CalculationAudit
            { ("CalculationAudit", "Triage"), (AuditActionType.DiscrepancyTriaged, AuditRulePolicy.Always) },
            { ("CalculationAudit", "Confirm"), (AuditActionType.DiscrepancyConfirmed, AuditRulePolicy.Always) },
            { ("CalculationAudit", "Reject"), (AuditActionType.DiscrepancyRejected, AuditRulePolicy.Always) },
            { ("CalculationAudit", "AcceptException"), (AuditActionType.DiscrepancyExceptionAccepted, AuditRulePolicy.Always) },
            { ("CalculationAudit", "MarkFixedPendingReaudit"), (AuditActionType.DiscrepancyMarkedFixedPending, AuditRulePolicy.Always) },
            { ("CalculationAudit", "Resolve"), (AuditActionType.DiscrepancyResolved, AuditRulePolicy.Always) },
        };

    public static (AuditActionType ActionType, AuditRulePolicy Policy) Resolve(string controller, string action)
    {
        if (string.IsNullOrWhiteSpace(controller) || string.IsNullOrWhiteSpace(action))
            return (AuditActionType.OtherMutation, AuditRulePolicy.Always);

        return RouteMap.TryGetValue((controller, action), out var route)
            ? route
            : (AuditActionType.OtherMutation, AuditRulePolicy.Always);
    }
}
