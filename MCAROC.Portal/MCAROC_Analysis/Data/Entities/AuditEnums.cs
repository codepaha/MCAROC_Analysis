namespace MCAROC_Analysis.Data.Entities;

public enum ActorType
{
    AuthenticatedReviewer,
    AuthenticatedAnalyst,
    SystemWorker,
    UnverifiedOperator
}

public enum AuditStatus
{
    Success,
    Warning,
    Failure
}

public enum AuditEventKind
{
    HttpMutation,
    DomainLifecycle
}

public enum AuditRulePolicy
{
    Always,
    FailuresOnly,
    Never
}

public enum AuditActionType
{
    // Requests
    RequestCreated,
    ReingestionStarted,
    WorkbooksAdded,
    ChatMessageSent,
    ChunkingRetryRequested,

    // AutoFetch
    AutoFetchRequested,
    AutoFetchRetried,
    AutoFetchRecheckRequested,
    UnlockApproved,

    // RequestsUpload
    ArchiveUploadInitiated,
    ArchiveUploadChunkFailed,
    ArchiveUploadCompleted,
    ArchiveUploadAborted,

    // Clients
    ClientEdited,

    // InternalAuth: the reviewer login was removed; these values stay because stored audit rows use them.
    InternalLoginAttempted,
    InternalLoggedOut,

    // Analyst access
    AnalystProvisioned,
    AnalystLoginAttempted,
    AnalystLoggedOut,
    AnalystAssignmentChanged,

    // PreLoginReports
    PreLoginReportFetched,
    PreLoginReportBatched,
    PreLoginReportEdited,
    PreLoginReportRerun,

    // CalculationAudit
    DiscrepancyTriaged,
    DiscrepancyConfirmed,
    DiscrepancyRejected,
    DiscrepancyExceptionAccepted,
    DiscrepancyMarkedFixedPending,
    DiscrepancyResolved,

    // CompanyMaster
    CompanyMasterDateProbed,
    CompanyMasterProxyTested,
    CompanyMasterSyncTriggered,
    CompanyMasterManualUploaded,

    // Litigation
    LitigationAnalysisRequested,
    LitigationSearchRequested,

    // Pipeline / Identity
    IdentitySelected,
    PipelineStageRetried,
    PipelineStageSkipped,
    PipelineRunCancelled,

    // Fallback
    OtherMutation,

    // Domain lifecycle (Worker / Services)
    IngestionStarted,
    IngestionCompleted,
    IngestionFailed,
    BatchUnpackStarted,
    BatchUnpackCompleted,
    BatchUnpackFailed,
    DocumentChunkingStarted,
    DocumentChunkingCompleted,
    DocumentChunkingFailed,
    WorkerOrphanRecovered,

    // Application login (append to preserve existing enum values)
    ApplicationLoginAttempted,
    ApplicationLoggedOut,

    // Append new values so existing enum ordinals remain stable.
    PreLoginAssignmentCreated,
    PreLoginAssignmentCompleted
}
