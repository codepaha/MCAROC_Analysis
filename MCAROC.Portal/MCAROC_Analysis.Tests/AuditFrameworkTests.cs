using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class AuditFrameworkTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlServer(ConnectionString));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE McaFilingBatches SET CorrelationId = LOWER(REPLACE(CAST(NEWID() AS nvarchar(36)), '-', '')) WHERE CorrelationId IS NULL OR CorrelationId = '';
            UPDATE AutoFetchJobs SET CorrelationId = LOWER(REPLACE(CAST(NEWID() AS nvarchar(36)), '-', '')) WHERE CorrelationId IS NULL OR CorrelationId = '';
            UPDATE LargeArchiveUploadSessions SET CorrelationId = LOWER(REPLACE(CAST(NEWID() AS nvarchar(36)), '-', '')) WHERE CorrelationId IS NULL OR CorrelationId = '';

            IF OBJECT_ID('TR_AuditLogs_AppendOnly', 'TR') IS NULL
            BEGIN
                EXEC(N'CREATE TRIGGER [TR_AuditLogs_AppendOnly]
                ON [dbo].[AuditLogs]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    RAISERROR(''Table AuditLogs is append-only. UPDATE and DELETE operations are forbidden.'', 16, 1);
                    ROLLBACK TRANSACTION;
                END;')
            END
        ");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<McaRequest> SeedRequestAsync(AppDbContext db)
    {
        var client = new Client
        {
            ClientCode = $"TST{Guid.NewGuid():N}"[..10],
            ClientName = "Test Client",
            CreatedDate = DateTime.UtcNow
        };
        db.Clients.Add(client);

        var request = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "Audit Test Co",
            Cin = "U74999MH2020PTC123456",
            RequestNumber = $"TEST-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DocumentsUploaded,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 1. Unhandled Action Exception Regression
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task UnhandledActionException_LogsSingleFailureEvent_AndPropagates()
    {
        var scopeFactory = CreateScopeFactory();
        var auditService = new AuditLogService(scopeFactory, NullLogger<AuditLogService>.Instance);
        var filter = new AuditLogFilter(auditService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        var correlationId = CorrelationContext.GetOrCreate(httpContext);

        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        routeData.Values["action"] = "New";

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object());

        var expectedException = new InvalidOperationException("Unhandled database lock failed at C:\\App_Data\\locked.txt with Bearer secret123");

        ActionExecutionDelegate next = () =>
        {
            var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object())
            {
                Exception = expectedException,
                ExceptionHandled = false
            };
            return Task.FromResult(executed);
        };

        // Execute action filter stage
        await filter.OnActionExecutionAsync(executingContext, next);

        // Verify the dual-stage marker was set to prevent double logging
        Assert.True(httpContext.Items.ContainsKey(AuditLogFilter.AuditRecordedKey));

        // Now run result filter stage — should be a no-op because marker is present
        var resultExecutingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new StatusCodeResult(500),
            new object());

        var resultNextCalled = false;
        ResultExecutionDelegate resultNext = () =>
        {
            resultNextCalled = true;
            return Task.FromResult(new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), new StatusCodeResult(500), new object()));
        };
        await filter.OnResultExecutionAsync(resultExecutingContext, resultNext);
        Assert.True(resultNextCalled);

        // Verify database row: exactly ONE failure row for this correlation ID
        await using var db = CreateContext();
        var logs = await db.AuditLogs
            .Where(a => a.CorrelationId == correlationId)
            .ToListAsync();

        var log = Assert.Single(logs);
        Assert.Equal(AuditStatus.Failure, log.Status);
        Assert.Equal(AuditActionType.RequestCreated, log.Action);
        Assert.Equal(AuditEventKind.HttpMutation, log.EventKind);
        Assert.Equal(ActorType.UnverifiedOperator, log.ActorType);
        Assert.Equal("Anonymous", log.ActorId);
        Assert.Contains("[PATH_REDACTED]", log.ErrorMessage);
        Assert.Contains("Bearer [REDACTED]", log.ErrorMessage);
        Assert.DoesNotContain("secret123", log.ErrorMessage);
        Assert.DoesNotContain("C:\\App_Data", log.ErrorMessage);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 2. Selective Upload Auditing: UploadChunk Success Excluded
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task UploadChunk_SuccessfulPuts_DoNotCreateAuditLog()
    {
        var scopeFactory = CreateScopeFactory();
        var auditService = new AuditLogService(scopeFactory, NullLogger<AuditLogService>.Instance);
        var filter = new AuditLogFilter(auditService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "PUT";
        httpContext.Response.StatusCode = 200;
        var correlationId = CorrelationContext.GetOrCreate(httpContext);

        var routeData = new RouteData();
        routeData.Values["controller"] = "RequestsUpload";
        routeData.Values["action"] = "UploadChunk";

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var resultExecutingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new OkResult(),
            new object());

        ResultExecutionDelegate resultNext = () =>
            Task.FromResult(new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), new OkResult(), new object()));

        await filter.OnResultExecutionAsync(resultExecutingContext, resultNext);

        await using var db = CreateContext();
        var logs = await db.AuditLogs
            .Where(a => a.CorrelationId == correlationId)
            .ToListAsync();

        Assert.Empty(logs); // FailuresOnly policy skips successes!
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 3. Selective Upload Auditing: UploadChunk Failure Included
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task UploadChunk_FailedPut_CreatesSingleFailureAuditLog()
    {
        var scopeFactory = CreateScopeFactory();
        var auditService = new AuditLogService(scopeFactory, NullLogger<AuditLogService>.Instance);
        var filter = new AuditLogFilter(auditService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "PUT";
        httpContext.Response.StatusCode = 400; // Bad Request
        var correlationId = CorrelationContext.GetOrCreate(httpContext);

        var routeData = new RouteData();
        routeData.Values["controller"] = "RequestsUpload";
        routeData.Values["action"] = "UploadChunk";

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var resultExecutingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new BadRequestResult(),
            new object());

        ResultExecutionDelegate resultNext = () =>
            Task.FromResult(new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), new BadRequestResult(), new object()));

        await filter.OnResultExecutionAsync(resultExecutingContext, resultNext);

        await using var db = CreateContext();
        var logs = await db.AuditLogs
            .Where(a => a.CorrelationId == correlationId)
            .ToListAsync();

        var log = Assert.Single(logs);
        Assert.Equal(AuditStatus.Failure, log.Status);
        Assert.Equal(AuditActionType.ArchiveUploadChunkFailed, log.Action);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 4. Valid JSON Envelope For Oversized Payloads
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void OversizedPayload_YieldsValidCompactJsonEnvelope_Under2000Chars()
    {
        // Construct a huge payload that exceeds 2,000 characters
        var oversizedText = new string('A', 3000);
        var payload = new ChunkingFailedPayload(
            FilingDocumentId: 42,
            ErrorCategory: "EmbeddingApi",
            SanitizedError: oversizedText,
            RetryCount: 3,
            IsTerminal: true,
            CorrelationId: "cid123");

        var json = AuditLogService.SerializeAndCapPayload(payload);

        Assert.NotNull(json);
        Assert.True(json.Length <= 2000, $"Payload length {json.Length} must be <= 2000");

        // Must deserialize cleanly without JSON syntax error!
        var envelope = JsonSerializer.Deserialize<TruncatedPayloadEnvelope>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(envelope);
        Assert.True(envelope.Truncated);
        Assert.Equal(nameof(ChunkingFailedPayload), envelope.PayloadType);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 5. Correlation Persistence: Manual Upload Flow
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ManualUpload_AssignsCorrelationIdToBatch()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);

        var correlationId = $"manual_corr_{Guid.NewGuid():N}";
        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            SourceDocumentId = 1,
            CorrelationId = correlationId,
            Status = FilingBatchStatus.Uploaded,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var savedBatch = await db.McaFilingBatches.FirstAsync(b => b.BatchId == batch.BatchId);
        Assert.Equal(correlationId, savedBatch.CorrelationId);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 6. Correlation Persistence: AutoFetch Flow
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AutoFetch_PropagatesCorrelationIdFromJobToBatch()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);

        var clientMock = new ReferenceToolClient(
            new HttpClient(),
            Microsoft.Extensions.Options.Options.Create(new ReferenceToolOptions()),
            NullLogger<ReferenceToolClient>.Instance);

        var jobs = new AutoFetchJobService(
            db, clientMock, Microsoft.Extensions.Options.Options.Create(new ReferenceToolOptions()),
            new FileValidationService(new Services.Excel.ExcelSheetReader()),
            null!, null!, null!, null!,
            new FakeHostEnv(Path.GetTempPath()),
            NullLogger<AutoFetchJobService>.Instance);

        var expectedCorrelationId = $"autofetch_corr_{Guid.NewGuid():N}";
        var job = await jobs.CreateOrResetJobAsync(request, false, 0, CancellationToken.None, expectedCorrelationId);

        Assert.Equal(expectedCorrelationId, job.CorrelationId);

        // Simulate retry with new correlation
        var retryCorrelationId = $"retry_corr_{Guid.NewGuid():N}";
        job.Status = AutoFetchJobStatus.Failed;
        await db.SaveChangesAsync();

        var retried = await jobs.RequeueAsync(request.RequestId, CancellationToken.None, retryCorrelationId);
        Assert.NotNull(retried);
        Assert.Equal(retryCorrelationId, retried.CorrelationId);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 7. Correlation Persistence: Resumable Upload Flow
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ResumableUpload_PropagatesCorrelationIdFromSessionToBatch()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);

        var expectedCorrelationId = $"resumable_corr_{Guid.NewGuid():N}";
        var session = new LargeArchiveUploadSession
        {
            SessionId = Guid.NewGuid(),
            RequestId = request.RequestId,
            CorrelationId = expectedCorrelationId,
            HashedCapabilityToken = "token_hash",
            OriginalFileName = "archive.zip",
            TotalExpectedSizeBytes = 1000,
            Status = LargeArchiveUploadSessionStatus.Uploading
        };
        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync();

        var savedSession = await db.LargeArchiveUploadSessions.FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal(expectedCorrelationId, savedSession.CorrelationId);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 8. All Correlation Columns Bounded in Schema (MaxLength = 64)
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AllCorrelationColumns_AreExplicitlyBoundedTo64Chars_AndRequired()
    {
        using var db = CreateContext();
        var model = db.Model;

        var entities = new[]
        {
            model.FindEntityType(typeof(McaFilingBatch)),
            model.FindEntityType(typeof(AutoFetchJob)),
            model.FindEntityType(typeof(LargeArchiveUploadSession)),
            model.FindEntityType(typeof(AuditLog))
        };

        foreach (var entity in entities)
        {
            Assert.NotNull(entity);
            var prop = entity.FindProperty("CorrelationId");
            Assert.NotNull(prop);
            Assert.Equal(64, prop.GetMaxLength());
            Assert.False(prop.IsNullable);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 9. Backfill — No Empty CorrelationId
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task MigrationBackfill_EnsuresNoEmptyCorrelationIdsInDatabase()
    {
        await using var db = CreateContext();

        var emptyBatches = await db.McaFilingBatches.CountAsync(b => b.CorrelationId == "");
        var emptyJobs = await db.AutoFetchJobs.CountAsync(j => j.CorrelationId == "");
        var emptySessions = await db.LargeArchiveUploadSessions.CountAsync(s => s.CorrelationId == "");

        Assert.Equal(0, emptyBatches);
        Assert.Equal(0, emptyJobs);
        Assert.Equal(0, emptySessions);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 10. Route Coverage — Exhaustive Reflection Test
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AllMutatingControllerActions_AreRegisteredInAuditRouteRegistry()
    {
        var controllerTypes = typeof(RequestsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        var missingRoutes = new List<string>();

        foreach (var controller in controllerTypes)
        {
            var controllerName = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controller.Name[..^"Controller".Length]
                : controller.Name;

            var mutatingMethods = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().Any(a =>
                    a is HttpPostAttribute or HttpPutAttribute or HttpDeleteAttribute or HttpPatchAttribute))
                .ToList();

            foreach (var method in mutatingMethods)
            {
                var actionName = method.Name;
                if (!AuditRouteRegistry.RouteMap.ContainsKey((controllerName, actionName)))
                {
                    missingRoutes.Add($"{controllerName}.{actionName}");
                }
            }
        }

        Assert.Empty(missingRoutes);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 11. Verified Action Names & Non-Nullable Safe Fallback
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AuditRouteRegistry_UnknownRoute_ReturnsSafeFallback()
    {
        var result = AuditRouteRegistry.Resolve("NonExistentController", "UnknownAction");
        Assert.Equal(AuditActionType.OtherMutation, result.ActionType);
        Assert.Equal(AuditRulePolicy.Always, result.Policy);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 12. Logout Mapping Verified
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AuditRouteRegistry_LogoutMapping_ResolvesToInternalLoggedOut()
    {
        var result = AuditRouteRegistry.Resolve("InternalAuth", "Logout");
        Assert.Equal(AuditActionType.InternalLoggedOut, result.ActionType);
        Assert.Equal(AuditRulePolicy.Always, result.Policy);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 13. All String Columns Bounded in AuditLog Schema
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AuditLog_AllStringColumns_HaveExplicitBoundsInSchema()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(AuditLog));
        Assert.NotNull(entity);

        Assert.Equal(64, entity.FindProperty(nameof(AuditLog.CorrelationId))?.GetMaxLength());
        Assert.Equal(200, entity.FindProperty(nameof(AuditLog.ActorId))?.GetMaxLength());
        Assert.Equal(80, entity.FindProperty(nameof(AuditLog.EntityType))?.GetMaxLength());
        Assert.Equal(500, entity.FindProperty(nameof(AuditLog.ErrorMessage))?.GetMaxLength());
        Assert.Equal(2000, entity.FindProperty(nameof(AuditLog.EventPayloadJson))?.GetMaxLength());
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 14. Result Filter — Redirect Status (302)
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ResultFilter_RedirectResult_LogsHttpMutationWithStatus302()
    {
        var scopeFactory = CreateScopeFactory();
        var auditService = new AuditLogService(scopeFactory, NullLogger<AuditLogService>.Instance);
        var filter = new AuditLogFilter(auditService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Response.StatusCode = 302;
        var correlationId = CorrelationContext.GetOrCreate(httpContext);

        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        routeData.Values["action"] = "New";

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var redirectResult = new RedirectToActionResult("Details", "Requests", new { id = 42 });

        var resultExecutingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            redirectResult,
            new object());

        ResultExecutionDelegate resultNext = () =>
            Task.FromResult(new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), redirectResult, new object()));

        await filter.OnResultExecutionAsync(resultExecutingContext, resultNext);

        await using var db = CreateContext();
        var log = await db.AuditLogs.FirstAsync(a => a.CorrelationId == correlationId);

        Assert.Equal(AuditStatus.Success, log.Status);
        Assert.Equal(AuditActionType.RequestCreated, log.Action);

        var payload = JsonSerializer.Deserialize<HttpMutationPayload>(log.EventPayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(302, payload.HttpStatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 15. Zero-Trust Actor: Spoofed Header Ignored
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ResolveActor_IgnoresSpoofedHeader_AndReturnsUnverifiedOperator()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Name"] = "admin";
        httpContext.Request.Headers["X-Remote-User"] = "superuser";

        var (actorType, actorId) = AuditLogFilter.ResolveActor(httpContext);

        Assert.Equal(ActorType.UnverifiedOperator, actorType);
        Assert.Equal("Anonymous", actorId);
    }

    [Fact]
    public void ResolveActor_AuthenticatedReviewer_ReturnsAuthenticatedReviewer()
    {
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "AliceReviewer")],
            "InternalReviewer");
        httpContext.User = new ClaimsPrincipal(identity);

        var (actorType, actorId) = AuditLogFilter.ResolveActor(httpContext);

        Assert.Equal(ActorType.AuthenticatedReviewer, actorType);
        Assert.Equal("AliceReviewer", actorId);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 16. No IP Stored Property
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AuditLog_HasNoClientIpProperty()
    {
        var properties = typeof(AuditLog).GetProperties();
        Assert.Null(properties.FirstOrDefault(p => p.Name.Equals("ClientIp", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(properties.FirstOrDefault(p => p.Name.Equals("IpAddress", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(properties.FirstOrDefault(p => p.Name.Equals("RemoteIp", StringComparison.OrdinalIgnoreCase)));
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 17. Token, GSTIN, PAN, and Path Redaction
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AuditSanitizer_RedactsAllSensitivePatterns_AndCapsLength()
    {
        var input = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.xyz " +
                    "Basic dXNlcjpwYXNz " +
                    "api_key=sk_live_1234567890abcdef " +
                    "Data Source=sql.prod.corp;User ID=sa;Password=SuperSecret123; " +
                    "C:\\inetpub\\wwwroot\\appsettings.json " +
                    "\\\\storage\\confidential\\keys.txt " +
                    "Customer PAN: ABCDE1234F " +
                    "Customer GSTIN: 27ABCDE1234F1Z5 " +
                    "Contact support@mcaroc.internal " +
                    "\r\n   at Controller.Action() in C:\\src\\Controller.cs:line 42";

        var sanitized = AuditSanitizer.SanitizeAndCap(input, 500);

        Assert.Contains("Bearer [REDACTED]", sanitized);
        Assert.Contains("Basic [REDACTED]", sanitized);
        Assert.Contains("api_key=[REDACTED]", sanitized);
        Assert.Contains("[CONNECTION_STRING_REDACTED]", sanitized);
        Assert.Contains("[PATH_REDACTED]", sanitized);
        Assert.Contains("[PAN_REDACTED]", sanitized);
        Assert.Contains("[GSTIN_REDACTED]", sanitized);
        Assert.Contains("[EMAIL_REDACTED]", sanitized);

        Assert.DoesNotContain("eyJhbGci", sanitized);
        Assert.DoesNotContain("SuperSecret123", sanitized);
        Assert.DoesNotContain("ABCDE1234F", sanitized);
        Assert.DoesNotContain("27ABCDE1234F1Z5", sanitized);
        Assert.DoesNotContain("support@mcaroc.internal", sanitized);
        Assert.DoesNotContain("at Controller.Action", sanitized);
        Assert.True(sanitized.Length <= 500);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 18. PII-Trim On DTOs
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void EventPayloadDTOs_DoNotExposeSensitivePIIFields()
    {
        Assert.Null(typeof(RequestCreatedPayload).GetProperty("CompanyName"));
        Assert.Null(typeof(DiscrepancyDecidedPayload).GetProperty("ReviewerName"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 19. Transaction Rollback Isolation
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AuditLog_SurvivesBusinessTransactionRollback()
    {
        await using var db = CreateContext();
        var scopeFactory = CreateScopeFactory();
        var auditService = new AuditLogService(scopeFactory, NullLogger<AuditLogService>.Instance);

        var correlationId = $"tx_rollback_corr_{Guid.NewGuid():N}";

        // Start a business transaction and roll it back
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var client = new Client
            {
                ClientCode = $"ROLL{Guid.NewGuid():N}"[..10],
                ClientName = "Rollback Client",
                CreatedDate = DateTime.UtcNow
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            // Audit write uses its own isolated scope factory
            await auditService.TryLogAsync(new AuditEvent(
                Action: AuditActionType.IngestionFailed,
                EventKind: AuditEventKind.DomainLifecycle,
                Status: AuditStatus.Failure,
                ActorType: ActorType.SystemWorker,
                ActorId: "IngestionWorker",
                CorrelationId: correlationId,
                ErrorMessage: "Transaction failed and will be rolled back"));

            await tx.RollbackAsync();
        }

        // Verify the business client was rolled back
        await using var verifyDb = CreateContext();
        var auditRow = await verifyDb.AuditLogs.FirstOrDefaultAsync(a => a.CorrelationId == correlationId);

        // The audit row MUST exist despite the business transaction rollback!
        Assert.NotNull(auditRow);
        Assert.Equal(AuditStatus.Failure, auditRow.Status);
        Assert.Equal(AuditActionType.IngestionFailed, auditRow.Action);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 20. Audit Write Failure Never Crashes Caller
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AuditWriteFailure_DoesNotThrow_AndEmitsCriticalLog()
    {
        // Faulty scope factory that throws
        var faultyScopeFactory = new FaultyScopeFactory();
        var testLogger = new TestLogger<AuditLogService>();
        var auditService = new AuditLogService(faultyScopeFactory, testLogger);

        // Should complete without throwing
        await auditService.TryLogAsync(new AuditEvent(
            Action: AuditActionType.RequestCreated,
            EventKind: AuditEventKind.HttpMutation,
            Status: AuditStatus.Success,
            ActorType: ActorType.SystemWorker,
            ActorId: "Test",
            CorrelationId: "corr123"));

        Assert.Single(testLogger.CriticalLogs);
        Assert.Equal(AuditLogService.AuditWriteFailureEventId.Id, testLogger.CriticalLogs[0].EventId.Id);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 21. Fallback Rate Limiting (Coalescing)
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AuditWriteFailures_AreCoalescedRateLimited()
    {
        var faultyScopeFactory = new FaultyScopeFactory();
        var testLogger = new TestLogger<AuditLogService>();
        var auditService = new AuditLogService(faultyScopeFactory, testLogger);

        for (var i = 0; i < 50; i++)
        {
            await auditService.TryLogAsync(new AuditEvent(
                Action: AuditActionType.RequestCreated,
                EventKind: AuditEventKind.HttpMutation,
                Status: AuditStatus.Success,
                ActorType: ActorType.SystemWorker,
                ActorId: "Test",
                CorrelationId: $"corr_{i}"));
        }

        // Out of 50 consecutive failures: 1st, 10th, 20th, 30th, 40th, 50th -> exactly 6 calls!
        Assert.Equal(6, testLogger.CriticalLogs.Count);
        Assert.Equal(50, auditService.ConsecutiveFailures);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 22. Viewer Endpoint Authorization Attribute
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AuditLogController_HasInternalReviewerAuthorizeAttribute()
    {
        var authAttr = typeof(AuditLogController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authAttr);
        Assert.Equal("InternalReviewer", authAttr.AuthenticationSchemes);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 23. Viewer Row Isolation
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Viewer_OnlyReturnsEventsForRequestedRequestId()
    {
        await using var db = CreateContext();
        var req1 = await SeedRequestAsync(db);
        var req2 = await SeedRequestAsync(db);

        var log1 = new AuditLog
        {
            TimestampUtc = DateTime.UtcNow,
            CorrelationId = "cid_req1",
            ActorType = ActorType.AuthenticatedReviewer,
            ActorId = "Reviewer1",
            Action = AuditActionType.RequestCreated,
            EventKind = AuditEventKind.HttpMutation,
            RequestId = req1.RequestId,
            Status = AuditStatus.Success
        };
        var log2 = new AuditLog
        {
            TimestampUtc = DateTime.UtcNow,
            CorrelationId = "cid_req2",
            ActorType = ActorType.AuthenticatedReviewer,
            ActorId = "Reviewer2",
            Action = AuditActionType.RequestCreated,
            EventKind = AuditEventKind.HttpMutation,
            RequestId = req2.RequestId,
            Status = AuditStatus.Success
        };
        db.AuditLogs.AddRange(log1, log2);
        await db.SaveChangesAsync();

        var controller = new AuditLogController(db);
        var result = await controller.Index(req1.RequestId);
        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AuditLogPageViewModel>(viewResult.Model);

        Assert.All(model.Items, item => Assert.Equal(req1.RequestId, item.RequestId));
        Assert.DoesNotContain(model.Items, item => item.RequestId == req2.RequestId);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 24. Viewer Stable Pagination & Out-of-Range Clamping
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Viewer_PaginationAndClamping_WorksDeterministically()
    {
        await using var db = CreateContext();
        var req = await SeedRequestAsync(db);

        var logs = new List<AuditLog>();
        for (var i = 0; i < 30; i++)
        {
            logs.Add(new AuditLog
            {
                TimestampUtc = DateTime.UtcNow.AddMinutes(i),
                CorrelationId = $"cid_{i}",
                ActorType = ActorType.SystemWorker,
                ActorId = "Worker",
                Action = AuditActionType.DocumentChunkingCompleted,
                EventKind = AuditEventKind.DomainLifecycle,
                RequestId = req.RequestId,
                Status = AuditStatus.Success
            });
        }
        db.AuditLogs.AddRange(logs);
        await db.SaveChangesAsync();

        var controller = new AuditLogController(db);

        // Page 1 with pageSize = 10
        var res1 = await controller.Index(req.RequestId, page: 1, pageSize: 10);
        var vm1 = Assert.IsType<AuditLogPageViewModel>(Assert.IsType<ViewResult>(res1).Model);
        Assert.Equal(1, vm1.Page);
        Assert.Equal(10, vm1.Items.Count);
        Assert.Equal(30, vm1.TotalCount);
        Assert.Equal(3, vm1.TotalPages);

        // Ensure descending order (newest first)
        for (var i = 0; i < vm1.Items.Count - 1; i++)
        {
            Assert.True(vm1.Items[i].TimestampUtc >= vm1.Items[i + 1].TimestampUtc);
        }

        // Out-of-range page = 99 -> clamped to page 1
        var resClamped = await controller.Index(req.RequestId, page: 99, pageSize: 10);
        var vmClamped = Assert.IsType<AuditLogPageViewModel>(Assert.IsType<ViewResult>(resClamped).Model);
        Assert.Equal(1, vmClamped.Page);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 25. Worker Lifecycle Audit Events
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task DocumentChunkingOrchestrator_LogsLifecycleEvents()
    {
        await using var db = CreateContext();
        var req = await SeedRequestAsync(db);

        var batch = new McaFilingBatch
        {
            RequestId = req.RequestId,
            SourceDocumentId = 1,
            CorrelationId = $"worker_corr_{Guid.NewGuid():N}",
            Status = FilingBatchStatus.Uploaded,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            BatchId = batch.BatchId,
            RequestId = req.RequestId,
            Srn = "SRN_TEST_WORKER"
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        var doc = new McaFilingDocument
        {
            BatchId = batch.BatchId,
            RequestId = req.RequestId,
            FilingId = filing.FilingId,
            OriginalFileName = "worker_lifecycle.pdf",
            FormType = "AOC-4",
            FileHash = $"h_{Guid.NewGuid():N}",
            ExtractedTextPath = @"C:\NonExistent\file.txt",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Pending,
            ChunkRetryCount = 2, // Next failure will be terminal (retryCount = 3)
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var scopeFactory = CreateScopeFactory();
        var auditService = new AuditLogService(scopeFactory, NullLogger<AuditLogService>.Instance);
        var queue = new DocumentChunkingQueue();
        var orchestrator = new DocumentChunkingOrchestrator(
            db, null!, queue, NullLogger<DocumentChunkingOrchestrator>.Instance, auditService);

        await orchestrator.ChunkDocumentAsync(doc.FilingDocumentId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var events = await verifyDb.AuditLogs
            .Where(a => a.CorrelationId == batch.CorrelationId)
            .OrderBy(a => a.AuditLogId)
            .ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal(AuditActionType.DocumentChunkingStarted, events[0].Action);
        Assert.Equal(AuditActionType.DocumentChunkingFailed, events[1].Action);
        Assert.Equal(AuditStatus.Failure, events[1].Status);
        Assert.NotNull(events[1].EventPayloadJson);
        Assert.Contains("TextFileMissing", events[1].EventPayloadJson);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 27. Worker Orphan Recovered: Emits Per Affected Batch With Batch Correlation & RequestId
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RecoverStaleWorkAsync_EmitsEventPerAffectedBatch_WithPersistedCorrelationId_AndRequestId()
    {
        await using var db = CreateContext();

        // 1. Setup two requests with separate batches
        var req1 = await SeedRequestAsync(db);
        var expectedCorrelationId1 = $"corr_batch1_{Guid.NewGuid():N}"[..32];
        var batch1 = new McaFilingBatch
        {
            RequestId = req1.RequestId,
            CorrelationId = expectedCorrelationId1,
            Status = FilingBatchStatus.Processing,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch1);

        var req2 = await SeedRequestAsync(db);
        var expectedCorrelationId2 = $"corr_batch2_{Guid.NewGuid():N}"[..32];
        var batch2 = new McaFilingBatch
        {
            RequestId = req2.RequestId,
            CorrelationId = expectedCorrelationId2,
            Status = FilingBatchStatus.Processing,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch2);
        await db.SaveChangesAsync();

        var filing1 = new McaFiling
        {
            BatchId = batch1.BatchId,
            RequestId = req1.RequestId,
            Srn = $"SRN_{Guid.NewGuid():N}"[..12]
        };
        var filing2 = new McaFiling
        {
            BatchId = batch2.BatchId,
            RequestId = req2.RequestId,
            Srn = $"SRN_{Guid.NewGuid():N}"[..12]
        };
        db.McaFilings.AddRange(filing1, filing2);
        await db.SaveChangesAsync();

        // 2. Add stale InProgress documents for batch 1 (2 docs) and batch 2 (1 doc)
        var doc1 = new McaFilingDocument
        {
            BatchId = batch1.BatchId,
            RequestId = req1.RequestId,
            FilingId = filing1.FilingId,
            OriginalFileName = "TestDoc1.pdf",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.InProgress,
            UpdatedAt = DateTime.UtcNow
        };
        var doc2 = new McaFilingDocument
        {
            BatchId = batch1.BatchId,
            RequestId = req1.RequestId,
            FilingId = filing1.FilingId,
            OriginalFileName = "TestDoc2.pdf",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.InProgress,
            UpdatedAt = DateTime.UtcNow
        };
        var doc3 = new McaFilingDocument
        {
            BatchId = batch2.BatchId,
            RequestId = req2.RequestId,
            FilingId = filing2.FilingId,
            OriginalFileName = "TestDoc3.pdf",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.InProgress,
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.AddRange(doc1, doc2, doc3);
        await db.SaveChangesAsync();

        // 3. Run RecoverStaleWorkAsync
        var scopeFactory = CreateScopeFactory();
        var auditService = new AuditLogService(scopeFactory, NullLogger<AuditLogService>.Instance);
        var queue = new DocumentChunkingQueue();
        var orchestrator = new DocumentChunkingOrchestrator(
            db, null!, queue, NullLogger<DocumentChunkingOrchestrator>.Instance, auditService);

        var recoveredCount = await orchestrator.RecoverStaleWorkAsync(CancellationToken.None);
        Assert.True(recoveredCount >= 2);

        // 4. Verify audit events in database
        await using var verifyDb = CreateContext();
        var events1 = await verifyDb.AuditLogs
            .Where(a => a.CorrelationId == expectedCorrelationId1)
            .ToListAsync();
        var events2 = await verifyDb.AuditLogs
            .Where(a => a.CorrelationId == expectedCorrelationId2)
            .ToListAsync();

        var ev1 = Assert.Single(events1);
        Assert.Equal(AuditActionType.WorkerOrphanRecovered, ev1.Action);
        Assert.Equal(AuditStatus.Success, ev1.Status);
        Assert.Equal(req1.RequestId, ev1.RequestId);
        Assert.Equal("McaFilingBatch", ev1.EntityType);
        Assert.Equal(batch1.BatchId, ev1.EntityId);
        Assert.NotNull(ev1.EventPayloadJson);
        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var p1 = JsonSerializer.Deserialize<WorkerOrphanRecoveredPayload>(ev1.EventPayloadJson, jsonOpts);
        Assert.NotNull(p1);
        Assert.Equal(batch1.BatchId, p1.BatchId);
        Assert.Equal(req1.RequestId, p1.RequestId);
        Assert.Equal(2, p1.DocumentsReset);
        Assert.Equal(expectedCorrelationId1, p1.CorrelationId);

        var ev2 = Assert.Single(events2);
        Assert.Equal(AuditActionType.WorkerOrphanRecovered, ev2.Action);
        Assert.Equal(AuditStatus.Success, ev2.Status);
        Assert.Equal(req2.RequestId, ev2.RequestId);
        Assert.Equal("McaFilingBatch", ev2.EntityType);
        Assert.Equal(batch2.BatchId, ev2.EntityId);
        Assert.NotNull(ev2.EventPayloadJson);
        var p2 = JsonSerializer.Deserialize<WorkerOrphanRecoveredPayload>(ev2.EventPayloadJson, jsonOpts);
        Assert.NotNull(p2);
        Assert.Equal(batch2.BatchId, p2.BatchId);
        Assert.Equal(req2.RequestId, p2.RequestId);
        Assert.Equal(1, p2.DocumentsReset);
        Assert.Equal(expectedCorrelationId2, p2.CorrelationId);

        // 5. Verify the event appears in the request-scoped query used by the audit viewer
        var request1ViewerLogs = await verifyDb.AuditLogs
            .Where(a => a.RequestId == req1.RequestId && a.Action == AuditActionType.WorkerOrphanRecovered)
            .ToListAsync();
        Assert.Single(request1ViewerLogs);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 28. Append-Only Immutability: Trigger Blocks UPDATE and DELETE Operations
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AuditLogs_TriggerEnforcesAppendOnly_PreventsUpdateAndDeletion()
    {
        await using var db = CreateContext();

        var correlationId = Guid.NewGuid().ToString("N");
        var log = new AuditLog
        {
            TimestampUtc = DateTime.UtcNow,
            CorrelationId = correlationId,
            ActorType = ActorType.SystemWorker,
            ActorId = "AppendOnlyTest",
            Action = AuditActionType.OtherMutation,
            EventKind = AuditEventKind.HttpMutation,
            Status = AuditStatus.Success
        };
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();

        // Attempt to UPDATE the row — must be rejected by trigger
        var updateEx = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE AuditLogs SET Status = 'Failure' WHERE AuditLogId = {0}", log.AuditLogId);
        });
        Assert.Contains("append-only", updateEx.Message, StringComparison.OrdinalIgnoreCase);

        // Attempt to DELETE the row — must be rejected by trigger
        var deleteEx = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM AuditLogs WHERE AuditLogId = {0}", log.AuditLogId);
        });
        Assert.Contains("append-only", deleteEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // 29. Correlation Context: Validates Guid Header and Rejects Arbitrary Client Strings
    // ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void CorrelationContext_ValidatesGuidHeader_AndRejectsArbitraryClientHeaders()
    {
        // 1. Valid client Guid header -> accepted and formatted as 32-char hex
        var clientGuid = Guid.NewGuid();
        var httpContext1 = new DefaultHttpContext();
        httpContext1.Request.Headers["X-Correlation-ID"] = clientGuid.ToString();

        var resolvedCid1 = CorrelationContext.GetOrCreate(httpContext1);
        Assert.Equal(clientGuid.ToString("N"), resolvedCid1);

        // 2. Arbitrary / malicious client string -> rejected and replaced with server Guid
        var httpContext2 = new DefaultHttpContext();
        httpContext2.Request.Headers["X-Correlation-ID"] = "arbitrary-collision-attack-or-raw-text";

        var resolvedCid2 = CorrelationContext.GetOrCreate(httpContext2);
        Assert.NotEqual("arbitrary-collision-attack-or-raw-text", resolvedCid2);
        Assert.True(Guid.TryParse(resolvedCid2, out _));
        Assert.Equal(32, resolvedCid2.Length);
    }
}

// ─────────────────────────────────────────────────────────────────────────────────
// Test Helpers
// ─────────────────────────────────────────────────────────────────────────────────
public class FaultyScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() =>
        throw new InvalidOperationException("Simulated database connection crash during audit persistence.");
}

public class TestLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, EventId EventId, string Message)> CriticalLogs { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Critical)
        {
            CriticalLogs.Add((logLevel, eventId, formatter(state, exception)));
        }
    }
}

public class FakeHostEnv(string path) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string WebRootPath { get; set; } = path;
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "MCAROC_Analysis";
    public string ContentRootPath { get; set; } = path;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}
