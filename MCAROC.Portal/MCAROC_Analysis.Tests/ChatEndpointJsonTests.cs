using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Chat;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.Dashboard;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

public class ChatEndpointJsonTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static RequestsController NewController(AppDbContext db)
    {
        var controller = new RequestsController(db, null!, null!, null!, null!, null!, Dossier.DossierGoldenMasterTests.CreateCache(), null!, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static async Task<long> SeedRequestAsync(string? companyName = "Test Company")
    {
        await using var db = CreateContext();
        var req = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = companyName ?? "Test Company",
            RequestNumber = $"REQ-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        return req.RequestId;
    }

    private sealed class FakeSuccessfulChatService : ChatService
    {
        private readonly AppDbContext _db;

        public FakeSuccessfulChatService(AppDbContext db)
            : base(db, null!, null!, NullLogger<ChatService>.Instance)
        {
            _db = db;
        }

        public override async Task<ChatTurnResult> AskTurnAsync(long requestId, string question, Guid? clientTurnId, CancellationToken ct)
        {
            var session = await GetOrCreateSessionAsync(requestId, ct);
            var assistantMsg = new ChatMessage
            {
                ChatSessionId = session.ChatSessionId,
                ClientTurnId = clientTurnId,
                Role = ChatRole.Assistant,
                MessageText = "Here is the verified answer.",
                Status = ChatMessageStatus.Success,
                CreatedDate = DateTime.UtcNow
            };
            _db.ChatMessages.Add(assistantMsg);
            await _db.SaveChangesAsync(ct);
            return new ChatTurnResult(ChatTurnOutcome.Success, assistantMsg);
        }

        public override Task<ChatTurnResult> AskTurnAsync(long requestId, string question, CancellationToken ct) =>
            AskTurnAsync(requestId, question, null, ct);
    }

    private static async Task<IHost> CreateTestHostAsync(Action<IServiceCollection>? configureServices = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddDbContext<AppDbContext>(options => options.UseSqlServer(ConnectionString));
                        services.AddRouting();
                        services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
                        services.AddTransient<RequestsController>(sp => NewController(sp.GetRequiredService<AppDbContext>()));
                        services.AddScoped<ChatService, FakeSuccessfulChatService>();
                        configureServices?.Invoke(services);
                        services.AddControllersWithViews()
                            .AddApplicationPart(typeof(RequestsController).Assembly)
                            .AddControllersAsServices();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAntiforgery();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/test-token", (IAntiforgery af, HttpContext ctx) =>
                            {
                                var tokens = af.GetAndStoreTokens(ctx);
                                return Results.Ok(new { requestToken = tokens.RequestToken });
                            });
                            endpoints.MapControllers();
                        });
                    });
            });

        return await host.StartAsync();
    }

    // ── 1. Antiforgery Header Tests ──

    [Fact]
    public async Task PostChat_MissingAntiforgeryToken_ReturnsBadRequest()
    {
        using var host = await CreateTestHostAsync();
        var client = host.GetTestClient();
        var requestId = await SeedRequestAsync();

        var content = new StringContent(JsonSerializer.Serialize(new { question = "Who are the directors?" }), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"/Requests/{requestId}/chat", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostChat_ValidAntiforgeryHeader_SucceedsWith200AndExpectedJson()
    {
        using var host = await CreateTestHostAsync();
        var client = host.GetTestClient();
        var requestId = await SeedRequestAsync();

        // 1. Fetch token and cookie
        var tokenRes = await client.GetAsync("/test-token");
        tokenRes.EnsureSuccessStatusCode();
        var cookieHeader = tokenRes.Headers.GetValues("Set-Cookie").FirstOrDefault();
        Assert.NotNull(cookieHeader);
        var cookie = cookieHeader.Split(';')[0];

        var tokenJson = await tokenRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(tokenJson);
        var requestToken = doc.RootElement.GetProperty("requestToken").GetString();
        Assert.NotNull(requestToken);

        // 2. Post with RequestVerificationToken header and cookie
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/Requests/{requestId}/chat");
        requestMessage.Headers.Add("RequestVerificationToken", requestToken);
        requestMessage.Headers.Add("Cookie", cookie);
        requestMessage.Content = new StringContent(JsonSerializer.Serialize(new { question = "Who are the directors?" }), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(requestMessage);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        using var resDoc = JsonDocument.Parse(responseJson);
        var root = resDoc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("Here is the verified answer.", root.GetProperty("message").GetProperty("text").GetString());
        Assert.Equal("Assistant", root.GetProperty("message").GetProperty("role").GetString());
    }

    // ── 2. Endpoint Question Validation Tests ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostChat_EmptyQuestion_ReturnsBadRequest_QuestionEmpty(string? question)
    {
        await using var db = CreateContext();
        var controller = NewController(db);
        var requestId = await SeedRequestAsync();
        var chatService = new FakeSuccessfulChatService(db);

        var result = await controller.AskChat(requestId, new AskChatJsonRequest { Question = question }, chatService, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var apiRes = Assert.IsType<ChatApiResponse>(badRequest.Value);
        Assert.False(apiRes.Success);
        Assert.Equal("QUESTION_EMPTY", apiRes.Error?.Code);
    }

    [Fact]
    public async Task PostChat_QuestionOver1000Chars_ReturnsBadRequest_QuestionTooLong()
    {
        await using var db = CreateContext();
        var controller = NewController(db);
        var requestId = await SeedRequestAsync();
        var chatService = new FakeSuccessfulChatService(db);
        var longQuestion = new string('x', 1001);

        var result = await controller.AskChat(requestId, new AskChatJsonRequest { Question = longQuestion }, chatService, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var apiRes = Assert.IsType<ChatApiResponse>(badRequest.Value);
        Assert.False(apiRes.Success);
        Assert.Equal("QUESTION_TOO_LONG", apiRes.Error?.Code);
    }

    [Fact]
    public async Task PostChat_UnknownRequest_ReturnsNotFound_RequestNotFound()
    {
        await using var db = CreateContext();
        var controller = NewController(db);
        var missingRequestId = -99999L;
        var chatService = new FakeSuccessfulChatService(db);

        var result = await controller.AskChat(missingRequestId, new AskChatJsonRequest { Question = "Valid question?" }, chatService, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var apiRes = Assert.IsType<ChatApiResponse>(notFound.Value);
        Assert.False(apiRes.Success);
        Assert.Equal("REQUEST_NOT_FOUND", apiRes.Error?.Code);
    }

    // ── 3. Upstream AI Failure and Persistence Tests ──

    private sealed class FailingChatCompletionService : ChatCompletionService
    {
        public override Task<ChatCompletionResult> CompleteAsync(
            string companyName, RetrievalContext context, IReadOnlyList<ChatMessage> history, string question, CancellationToken ct)
        {
            throw new HttpRequestException("Vertex AI upstream 503 service unavailable with internal credentials /secret/path");
        }
    }

    private sealed class StubRetrievalContextBuilder : RetrievalContextBuilder
    {
        public override Task<RetrievalContext> BuildAsync(long requestId, string question, CancellationToken ct)
        {
            return Task.FromResult(new RetrievalContext([], "Complete"));
        }
    }

    [Fact]
    public async Task PostChat_UpstreamAiFailure_Returns502_AndPersistsBothUserAndFailedAssistantTurns()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Failure Corp");
        var contextBuilder = new StubRetrievalContextBuilder();
        var completionService = new FailingChatCompletionService();
        var realChatService = new ChatService(db, contextBuilder, completionService, NullLogger<ChatService>.Instance);

        var controller = NewController(db);
        var result = await controller.AskChat(requestId, new AskChatJsonRequest { Question = "What is the turnover?" }, realChatService, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, statusResult.StatusCode);

        var apiRes = Assert.IsType<ChatApiResponse>(statusResult.Value);
        Assert.False(apiRes.Success);
        Assert.Equal("AI_FAILURE", apiRes.Error?.Code);
        // Assert ex.Message is NOT leaked to client
        Assert.DoesNotContain("/secret/path", apiRes.Error?.Message);
        Assert.DoesNotContain("Vertex", apiRes.Error?.Message);
        Assert.Equal("Sorry, something went wrong answering that question. Please try again.", apiRes.Error?.Message);

        // Assert message DTO is mapped for client alignment
        Assert.NotNull(apiRes.Message);
        Assert.Equal("Assistant", apiRes.Message.Role);
        Assert.Equal("Failed", apiRes.Message.Status);
        Assert.Equal("Sorry, something went wrong answering that question. Please try again.", apiRes.Message.Text);

        // Assert persistence: both user message and failed assistant message must exist in DB
        await using var verifyDb = CreateContext();
        var messages = await verifyDb.ChatMessages
            .Where(m => verifyDb.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId))
            .OrderBy(m => m.ChatMessageId)
            .ToListAsync();

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("What is the turnover?", messages[0].MessageText);

        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal(ChatMessageStatus.Failed, messages[1].Status);
        Assert.Equal("Sorry, something went wrong answering that question. Please try again.", messages[1].MessageText);
    }

    // ── 4. Cancellation Handling Test ──

    private sealed class HangingChatCompletionService : ChatCompletionService
    {
        public override async Task<ChatCompletionResult> CompleteAsync(
            string companyName, RetrievalContext context, IReadOnlyList<ChatMessage> history, string question, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ChatCompletionResult("Never reached", false, []);
        }
    }

    [Fact]
    public async Task PostChat_Cancellation_RollsBackUserTurn_AndRethrowsOperationCanceledException()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Cancel Corp");
        var contextBuilder = new StubRetrievalContextBuilder();
        var completionService = new HangingChatCompletionService();
        var realChatService = new ChatService(db, contextBuilder, completionService, NullLogger<ChatService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel or cancel mid-flight

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await realChatService.AskTurnAsync(requestId, "Will this be rolled back?", cts.Token);
        });

        // Assert rollback: user message was removed and no assistant turn exists
        await using var verifyDb = CreateContext();
        var messages = await verifyDb.ChatMessages
            .Where(m => verifyDb.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId))
            .ToListAsync();

        Assert.Empty(messages);
    }

    private sealed class SuccessfulMockCompletionService : ChatCompletionService
    {
        public override Task<ChatCompletionResult> CompleteAsync(
            string companyName, RetrievalContext context, IReadOnlyList<ChatMessage> history, string question, CancellationToken ct)
        {
            return Task.FromResult(new ChatCompletionResult("Answer ready to save", false, []));
        }
    }

    private sealed class SaveCancelingChatService : ChatService
    {
        private readonly CancellationTokenSource _cts;

        public SaveCancelingChatService(
            AppDbContext db,
            RetrievalContextBuilder contextBuilder,
            ChatCompletionService completionService,
            CancellationTokenSource cts)
            : base(db, contextBuilder, completionService, NullLogger<ChatService>.Instance)
        {
            _cts = cts;
        }

        internal override Task BeforeAssistantMessageSaveAsync(CancellationToken ct)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PostChat_CancellationDuringAssistantSave_DetachesAssistant_RollsBackUserTurn_AndNeverPersistsOrphanAssistant()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Save Cancel Corp");
        var contextBuilder = new StubRetrievalContextBuilder();
        var completionService = new SuccessfulMockCompletionService();

        using var cts = new CancellationTokenSource();
        var realChatService = new SaveCancelingChatService(db, contextBuilder, completionService, cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await realChatService.AskTurnAsync(requestId, "Will assistant be saved if cancelled during save?", cts.Token);
        });

        // Assert that NO orphan assistant was saved and user message was rolled back
        await using var verifyDb = CreateContext();
        var messages = await verifyDb.ChatMessages
            .Where(m => verifyDb.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId))
            .ToListAsync();

        Assert.Empty(messages);
    }

    private sealed class AfterSaveCancelingChatService : ChatService
    {
        private readonly CancellationTokenSource _cts;

        public AfterSaveCancelingChatService(
            AppDbContext db,
            RetrievalContextBuilder contextBuilder,
            ChatCompletionService completionService,
            CancellationTokenSource cts)
            : base(db, contextBuilder, completionService, NullLogger<ChatService>.Instance)
        {
            _cts = cts;
        }

        internal override Task AfterAssistantMessageSaveAsync(CancellationToken ct)
        {
            _cts.Cancel();
            _cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PostChat_CancellationAfterAssistantSave_RemovesCommittedAssistant_AndRollsBackUserTurn()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("After Save Cancel Corp");
        var contextBuilder = new StubRetrievalContextBuilder();
        var completionService = new SuccessfulMockCompletionService();

        using var cts = new CancellationTokenSource();
        var realChatService = new AfterSaveCancelingChatService(db, contextBuilder, completionService, cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await realChatService.AskTurnAsync(requestId, "Will committed assistant be removed if cancelled right after save?", cts.Token);
        });

        // Assert that committed assistant was deleted from DB and user message was rolled back
        await using var verifyDb = CreateContext();
        var messages = await verifyDb.ChatMessages
            .Where(m => verifyDb.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId))
            .ToListAsync();

        Assert.Empty(messages);
    }

    // ── 5. Malformed Citations Resilience Tests ──

    private sealed class RawCitationsChatService : ChatService
    {
        private readonly AppDbContext _db;
        private readonly string _rawCitationsJson;

        public RawCitationsChatService(AppDbContext db, string rawCitationsJson)
            : base(db, null!, null!, NullLogger<ChatService>.Instance)
        {
            _db = db;
            _rawCitationsJson = rawCitationsJson;
        }

        public override async Task<ChatTurnResult> AskTurnAsync(long requestId, string question, Guid? clientTurnId, CancellationToken ct)
        {
            var session = await GetOrCreateSessionAsync(requestId, ct);
            var assistantMsg = new ChatMessage
            {
                ChatSessionId = session.ChatSessionId,
                ClientTurnId = clientTurnId,
                Role = ChatRole.Assistant,
                MessageText = "Answer with strange citations",
                CitedSourcesJson = _rawCitationsJson,
                Status = ChatMessageStatus.Success,
                CreatedDate = DateTime.UtcNow
            };
            _db.ChatMessages.Add(assistantMsg);
            await _db.SaveChangesAsync(ct);
            return new ChatTurnResult(ChatTurnOutcome.Success, assistantMsg);
        }

        public override Task<ChatTurnResult> AskTurnAsync(long requestId, string question, CancellationToken ct) =>
            AskTurnAsync(requestId, question, null, ct);
    }

    [Theory]
    [InlineData("""[123, "not_an_object", null, true]""")]
    [InlineData("""[{"SourceType":"DocumentChunk","PageNumber":99999999999999999999999999999999999999999999999999,"DocumentId":1e50}]""")]
    [InlineData("""[{"SourceType":123,"Label":null,"DocumentName":false,"PageNumber":-5,"DocumentId":-10}]""")]
    [InlineData("""{"not":"an_array"}""")]
    [InlineData("""{corrupt-json""")]
    public async Task PostChat_MalformedPersistedCitations_NeverThrows500_AndSafelySanitizes(string malformedCitationsJson)
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Malformed Citations Corp");
        var controller = NewController(db);
        var chatService = new RawCitationsChatService(db, malformedCitationsJson);

        var result = await controller.AskChat(requestId, new AskChatJsonRequest { Question = "Safe question" }, chatService, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiRes = Assert.IsType<ChatApiResponse>(okResult.Value);
        Assert.True(apiRes.Success);
        Assert.NotNull(apiRes.Message);
        Assert.All(apiRes.Message.Citations, c =>
        {
            Assert.NotNull(c.SourceType);
            Assert.NotNull(c.Label);
        });
    }

    // ── 5. DocumentRetriever Authoritative Batch Isolation ──

    private static float[] Axis(int dim, float sign = 1f)
    {
        var v = new float[768];
        v[dim] = sign;
        return v;
    }

    private static DocumentChunk NewChunk(long requestId, long batchId, long docId, string srn, FilingCategory category, float[] embedding) => new()
    {
        RequestId = requestId, FilingDocumentId = docId, FilingId = batchId, BatchId = batchId,
        Srn = srn, Category = category, FormType = null, DocumentName = "doc.pdf",
        ChunkIndex = 0, PageNumber = 1, ChunkText = $"chunk for batch {batchId}",
        Embedding = new SqlVector<float>(embedding),
        EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
    };

    [Fact]
    public async Task DocumentRetriever_BatchIsolation_ExcludesStaleBatchChunks()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Batch Isolation Corp");
        var staleBatchId = DateTime.UtcNow.Ticks + 1;
        var activeBatchId = DateTime.UtcNow.Ticks + 2;

        var staleChunk = NewChunk(requestId, staleBatchId, 101, "SRN-OLD", FilingCategory.Charge, Axis(0));
        var activeChunk = NewChunk(requestId, activeBatchId, 201, "SRN-NEW", FilingCategory.Charge, Axis(0));

        db.DocumentChunks.AddRange(staleChunk, activeChunk);
        await db.SaveChangesAsync();

        var retriever = new DocumentRetriever(db, ChatRetrievalOptions.Default with { MaxCosineDistance = 2.0 });
        var matches = await retriever.SearchRequestDocumentsAsync(
            requestId,
            activeBatchId,
            Axis(0),
            new QuestionHints(null, null, null, null, null),
            CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal(activeBatchId, matches[0].Chunk.BatchId);
        Assert.Equal(201, matches[0].Chunk.FilingDocumentId);
    }

    // ── 6. Citation Verification Against Authoritative Batch ──

    private sealed class CitationInjectingChatService : ChatService
    {
        private readonly AppDbContext _db;
        private readonly List<ResolvedCitation> _citations;

        public CitationInjectingChatService(AppDbContext db, List<ResolvedCitation> citations)
            : base(db, null!, null!, NullLogger<ChatService>.Instance)
        {
            _db = db;
            _citations = citations;
        }

        public override async Task<ChatTurnResult> AskTurnAsync(long requestId, string question, Guid? clientTurnId, CancellationToken ct)
        {
            var session = await GetOrCreateSessionAsync(requestId, ct);
            var assistantMsg = new ChatMessage
            {
                ChatSessionId = session.ChatSessionId,
                ClientTurnId = clientTurnId,
                Role = ChatRole.Assistant,
                MessageText = "Answer with citations.",
                CitedSourcesJson = JsonSerializer.Serialize(_citations),
                Status = ChatMessageStatus.Success,
                CreatedDate = DateTime.UtcNow
            };
            _db.ChatMessages.Add(assistantMsg);
            await _db.SaveChangesAsync(ct);
            return new ChatTurnResult(ChatTurnOutcome.Success, assistantMsg);
        }

        public override Task<ChatTurnResult> AskTurnAsync(long requestId, string question, CancellationToken ct) =>
            AskTurnAsync(requestId, question, null, ct);
    }

    [Fact]
    public async Task PostChat_CitationVerification_EmitsViewerUrlForAuthoritativeBatch_AndNullForStaleOrMissing()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Citation Corp");

        // Seed an authoritative completed batch
        var authBatch = new McaFilingBatch
        {
            RequestId = requestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow.AddHours(-2),
            CompletedDate = DateTime.UtcNow.AddHours(-1)
        };
        db.McaFilingBatches.Add(authBatch);
        await db.SaveChangesAsync();

        // Seed a document in the authoritative batch
        var authFiling = new McaFiling
        {
            RequestId = requestId,
            BatchId = authBatch.BatchId,
            Srn = "SRN-AUTH"
        };
        db.McaFilings.Add(authFiling);
        await db.SaveChangesAsync();

        var authDoc = new McaFilingDocument
        {
            RequestId = requestId,
            BatchId = authBatch.BatchId,
            FilingId = authFiling.FilingId,
            OriginalFileName = "auth_doc.pdf",
            FileHash = Guid.NewGuid().ToString("N")
        };
        db.McaFilingDocuments.Add(authDoc);
        await db.SaveChangesAsync();

        // Seed an older completed batch and document
        var oldBatch = new McaFilingBatch
        {
            RequestId = requestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow.AddHours(-10),
            CompletedDate = DateTime.UtcNow.AddHours(-9)
        };
        db.McaFilingBatches.Add(oldBatch);
        await db.SaveChangesAsync();

        var oldFiling = new McaFiling
        {
            RequestId = requestId,
            BatchId = oldBatch.BatchId,
            Srn = "SRN-OLD"
        };
        db.McaFilings.Add(oldFiling);
        await db.SaveChangesAsync();

        var oldDoc = new McaFilingDocument
        {
            RequestId = requestId,
            BatchId = oldBatch.BatchId,
            FilingId = oldFiling.FilingId,
            OriginalFileName = "old_doc.pdf",
            FileHash = Guid.NewGuid().ToString("N")
        };
        db.McaFilingDocuments.Add(oldDoc);
        await db.SaveChangesAsync();

        var citations = new List<ResolvedCitation>
        {
            // 1. Authoritative batch document: should get viewer URL
            new("DocumentChunk", 1, "auth_doc.pdf", 3, null, null, "auth_doc.pdf · Page 3", authDoc.FilingDocumentId),
            // 2. Old batch document: should get viewerUrl = null
            new("DocumentChunk", 2, "old_doc.pdf", 5, null, null, "old_doc.pdf · Page 5", oldDoc.FilingDocumentId),
            // 3. DocumentChunk with no DocumentId (legacy shape): should get viewerUrl = null
            new("DocumentChunk", 3, "unlinked.pdf", 1, null, null, "unlinked.pdf · Page 1", null),
            // 4. Structured fact: should get viewerUrl = null
            new("StructuredFact", null, null, null, "RocCharge", 42, "Charge: State Bank of India", null)
        };

        var controller = NewController(db);
        var chatService = new CitationInjectingChatService(db, citations);

        var result = await controller.AskChat(requestId, new AskChatJsonRequest { Question = "Show sources" }, chatService, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiRes = Assert.IsType<ChatApiResponse>(okResult.Value);
        Assert.True(apiRes.Success);
        Assert.NotNull(apiRes.Message);
        Assert.Equal(4, apiRes.Message.Citations.Count);

        // 1. Verified citation
        var c1 = apiRes.Message.Citations[0];
        Assert.Equal(authDoc.FilingDocumentId, c1.DocumentId);
        Assert.Equal($"/Requests/{requestId}/documents/{authDoc.FilingDocumentId}/view#page=3", c1.ViewerUrl);

        // 2. Stale batch citation
        var c2 = apiRes.Message.Citations[1];
        Assert.Equal(oldDoc.FilingDocumentId, c2.DocumentId);
        Assert.Null(c2.ViewerUrl);

        // 3. Unlinked citation
        var c3 = apiRes.Message.Citations[2];
        Assert.Null(c3.DocumentId);
        Assert.Null(c3.ViewerUrl);

        // 4. Structured fact
        var c4 = apiRes.Message.Citations[3];
        Assert.Null(c4.ViewerUrl);
    }

    [Fact]
    public async Task GetChatHistory_ReturnsPersistedMessages_WithAuthoritativeCitations()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("History Corp");
        var authoritativeBatch = new McaFilingBatch
        {
            RequestId = requestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow.AddHours(-1),
            CompletedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(authoritativeBatch);
        await db.SaveChangesAsync();

        var filing = new McaFiling { RequestId = requestId, BatchId = authoritativeBatch.BatchId, Srn = "SRN-HIST" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        var doc = new McaFilingDocument
        {
            RequestId = requestId,
            BatchId = authoritativeBatch.BatchId,
            FilingId = filing.FilingId,
            OriginalFileName = "auth_doc.pdf",
            FileHash = Guid.NewGuid().ToString("N")
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var session = new ChatSession { RequestId = requestId, CreatedDate = DateTime.UtcNow, LastActivityDate = DateTime.UtcNow };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var citationsJson = JsonSerializer.Serialize(new[]
        {
            new { SourceType = "DocumentChunk", DocumentId = doc.FilingDocumentId, DocumentName = "auth_doc.pdf", PageNumber = 2 }
        });

        db.ChatMessages.AddRange(
            new ChatMessage { ChatSessionId = session.ChatSessionId, Role = ChatRole.User, MessageText = "Question 1?", CreatedDate = DateTime.UtcNow.AddMinutes(-1) },
            new ChatMessage { ChatSessionId = session.ChatSessionId, Role = ChatRole.Assistant, MessageText = "Answer 1.", CitedSourcesJson = citationsJson, Status = ChatMessageStatus.Success, CreatedDate = DateTime.UtcNow.AddSeconds(-30) }
        );
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var chatService = new FakeSuccessfulChatService(db);

        var result = await controller.GetChatHistory(requestId, chatService, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic resData = okResult.Value!;
        Assert.True((bool)resData.success);

        var messages = (List<ChatMessageDto>)resData.messages;
        Assert.Equal(2, messages.Count);
        Assert.Equal("Question 1?", messages[0].Text);
        Assert.Equal("Answer 1.", messages[1].Text);
        Assert.Single(messages[1].Citations);
        Assert.Equal($"/Requests/{requestId}/documents/{doc.FilingDocumentId}/view#page=2", messages[1].Citations[0].ViewerUrl);
    }

    [Fact]
    public async Task PostChat_DeduplicationWithClientTurnId_ReturnsExistingAnswer_WithoutCreatingDuplicateTurns()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Dedup Corp");
        var contextBuilder = new StubRetrievalContextBuilder();
        var completionService = new SuccessfulMockCompletionService();
        var realChatService = new ChatService(db, contextBuilder, completionService, NullLogger<ChatService>.Instance);

        var clientTurnId = Guid.NewGuid();

        // First ask
        var turn1 = await realChatService.AskTurnAsync(requestId, "Who is the managing director?", clientTurnId, CancellationToken.None);
        Assert.Equal(ChatTurnOutcome.Success, turn1.Outcome);

        // Immediate retry with identical ClientTurnId
        var turn2 = await realChatService.AskTurnAsync(requestId, "Who is the managing director?", clientTurnId, CancellationToken.None);
        Assert.Equal(ChatTurnOutcome.Success, turn2.Outcome);
        Assert.Equal(turn1.Message?.ChatMessageId, turn2.Message?.ChatMessageId);

        // Verify that database has only 2 messages (1 user, 1 assistant), not 4
        await using var verifyDb = CreateContext();
        var messages = await verifyDb.ChatMessages
            .Where(m => verifyDb.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId))
            .ToListAsync();

        Assert.Equal(2, messages.Count);
        Assert.Equal(clientTurnId, messages.First(m => m.Role == ChatRole.User).ClientTurnId);
    }

    [Fact]
    public async Task PostChat_LegitimateRepeatedQuestion_WithDifferentClientTurnId_CreatesDistinctTurns()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Repeat Corp");
        var contextBuilder = new StubRetrievalContextBuilder();
        var completionService = new SuccessfulMockCompletionService();
        var realChatService = new ChatService(db, contextBuilder, completionService, NullLogger<ChatService>.Instance);

        var clientTurnId1 = Guid.NewGuid();
        var clientTurnId2 = Guid.NewGuid();

        var turn1 = await realChatService.AskTurnAsync(requestId, "Who is the managing director?", clientTurnId1, CancellationToken.None);
        Assert.Equal(ChatTurnOutcome.Success, turn1.Outcome);

        var turn2 = await realChatService.AskTurnAsync(requestId, "Who is the managing director?", clientTurnId2, CancellationToken.None);
        Assert.Equal(ChatTurnOutcome.Success, turn2.Outcome);
        Assert.NotEqual(turn1.Message?.ChatMessageId, turn2.Message?.ChatMessageId);

        // Verify that database has 4 messages (2 user, 2 assistant)
        await using var verifyDb = CreateContext();
        var messages = await verifyDb.ChatMessages
            .Where(m => verifyDb.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId))
            .OrderBy(m => m.CreatedDate)
            .ToListAsync();

        Assert.Equal(4, messages.Count);
        Assert.Equal(2, messages.Count(m => m.Role == ChatRole.User));
        Assert.Equal(2, messages.Count(m => m.Role == ChatRole.Assistant));
    }

    [Fact]
    public async Task AskTurnAsync_QuestionOver1000Chars_SharedServiceRejectsWithQuestionTooLong()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("OverLimit Corp");
        var contextBuilder = new StubRetrievalContextBuilder();
        var completionService = new SuccessfulMockCompletionService();
        var realChatService = new ChatService(db, contextBuilder, completionService, NullLogger<ChatService>.Instance);

        var longQuestion = new string('a', ChatService.MaxQuestionLength + 1);
        var result = await realChatService.AskTurnAsync(requestId, longQuestion, CancellationToken.None);

        Assert.Equal(ChatTurnOutcome.QuestionTooLong, result.Outcome);
        Assert.Null(result.Message);

        await using var verifyDb = CreateContext();
        var messages = await verifyDb.ChatMessages
            .Where(m => verifyDb.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId))
            .ToListAsync();
        Assert.Empty(messages);
    }

    private sealed class DelayedMockCompletionService(TimeSpan delay) : ChatCompletionService
    {
        public override async Task<ChatCompletionResult> CompleteAsync(
            string companyName, RetrievalContext context, IReadOnlyList<ChatMessage> history, string question, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new ChatCompletionResult("Delayed answer ready", false, []);
        }
    }

    [Fact]
    public async Task PostChat_ConcurrentRaceWithSameClientTurnId_AwaitsAndReturnsCompletedAssistantTurn()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("Concurrent Corp");
        var contextBuilder = new StubRetrievalContextBuilder();
        var delayedCompletion = new DelayedMockCompletionService(TimeSpan.FromMilliseconds(600));

        var clientTurnId = Guid.NewGuid();

        var task1 = Task.Run(async () =>
        {
            await using var ctx1 = CreateContext();
            var service1 = new ChatService(ctx1, contextBuilder, delayedCompletion, NullLogger<ChatService>.Instance);
            return await service1.AskTurnAsync(requestId, "Who is the managing director?", clientTurnId, CancellationToken.None);
        });

        // Small delay to ensure task1 enters completeAsync before task2 tries to insert the same ClientTurnId
        await Task.Delay(150);

        var task2 = Task.Run(async () =>
        {
            await using var ctx2 = CreateContext();
            var service2 = new ChatService(ctx2, contextBuilder, delayedCompletion, NullLogger<ChatService>.Instance);
            return await service2.AskTurnAsync(requestId, "Who is the managing director?", clientTurnId, CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);
        Assert.Equal(ChatTurnOutcome.Success, results[0].Outcome);
        Assert.Equal(ChatTurnOutcome.Success, results[1].Outcome);
        Assert.NotNull(results[0].Message);
        Assert.NotNull(results[1].Message);
        Assert.Equal(results[0].Message!.ChatMessageId, results[1].Message!.ChatMessageId);

        await using var verifyDb = CreateContext();
        var messages = await verifyDb.ChatMessages
            .Where(m => verifyDb.ChatSessions.Any(s => s.ChatSessionId == m.ChatSessionId && s.RequestId == requestId))
            .ToListAsync();

        Assert.Equal(2, messages.Count);
        Assert.Equal(clientTurnId, messages.First(m => m.Role == ChatRole.User).ClientTurnId);
    }

    [Fact]
    public async Task GetChatHistory_WithInFlightUserTurn_ReturnsUserMessageWithClientTurnId_WithoutAssistant()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync("InFlight Corp");

        var session = new ChatSession { RequestId = requestId, CreatedDate = DateTime.UtcNow, LastActivityDate = DateTime.UtcNow };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var clientTurnId = Guid.NewGuid();
        var userMsg = new ChatMessage
        {
            ChatSessionId = session.ChatSessionId,
            ClientTurnId = clientTurnId,
            Role = ChatRole.User,
            MessageText = "What are the latest filings?",
            CreatedDate = DateTime.UtcNow
        };
        db.ChatMessages.Add(userMsg);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var chatService = new ChatService(db, new StubRetrievalContextBuilder(), new SuccessfulMockCompletionService(), NullLogger<ChatService>.Instance);

        var result = await controller.GetChatHistory(requestId, chatService, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic resData = okResult.Value!;
        Assert.True((bool)resData.success);

        var messages = (List<ChatMessageDto>)resData.messages;
        Assert.Single(messages);
        Assert.Equal("What are the latest filings?", messages[0].Text);
        Assert.Equal("User", messages[0].Role);
        Assert.Equal(clientTurnId, messages[0].ClientTurnId);
    }
}
