using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Controllers;

/// <summary>The "just give me a CIN" way to create a post-login request: the reference tool supplies the
/// workbooks and filing PDFs, and the job hands them to the very same ingestion / analysis / filings
/// pipelines the manual upload form feeds. Lives under /Requests/... so it reads as a sibling of the
/// manual New form, but in its own controller so RequestsController doesn't grow another concern.
///
/// Gated behind the same "InternalReviewer" cookie scheme as /internal/calc-audit (see
/// CalculationAuditController): every action here spends the app's own reference-tool session credential
/// on the caller's behalf and can trigger unbounded external downloads, so it must not be reachable by an
/// anonymous caller the way the rest of this app's pages are (see InternalAuthController's remarks on why
/// new one).</summary>
public partial class AutoFetchController(
    AppDbContext db,
    AutoFetchJobService jobs,
    AutoFetchQueue queue,
    ReferenceToolClient client,
    IOptions<ReferenceToolOptions> options,
    ILogger<AutoFetchController>? logger = null,
    PipelineAdopter? pipelineAdopter = null,
    CompanyUnlockService? unlock = null,
    CompanyGateCoordinator? gates = null,
    IdentityResolutionService? identity = null) : Controller
{
    // Company CIN (21 chars) or LLPIN (AAA-1234) — same rule the pre-login flow applies.
    [GeneratedRegex("^(?:[LU][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}|[A-Z]{3}-[0-9]{4})$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Z]{5}[0-9]{4}[A-Z]$")]
    private static partial Regex PanPattern();

    [HttpGet("/Requests/AutoFetch")]
    public async Task<IActionResult> New([FromQuery] string? cin = null)
    {
        var opts = options.Value;
        var vm = new AutoFetchRequestViewModel
        {
            Clients = await ActiveClientsAsync(),
            IncludeFilings = opts.IncludeFilingsByDefault,
            MaxDocumentsPerSection = opts.DefaultMaxDocumentsPerSection > 0 ? opts.DefaultMaxDocumentsPerSection : null,
            IsConfigured = opts.IsConfigured
        };

        if (!string.IsNullOrWhiteSpace(cin))
        {
            var trimmed = cin.Trim().ToUpperInvariant();
            if (IdentifierPattern().IsMatch(trimmed))
            {
                vm.Cin = trimmed;
                vm.EntityType = trimmed.Contains('-') ? EntityType.LLP : EntityType.Company;
            }
        }

        return View("~/Views/Requests/AutoFetch.cshtml", vm);
    }

    [HttpPost("/Requests/AutoFetch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(AutoFetchRequestViewModel model, CancellationToken ct)
    {
        model.Clients = await ActiveClientsAsync();
        model.IsConfigured = options.Value.IsConfigured;

        if (!model.IsConfigured)
            return Fail(model, "Auto-fetch is not configured on this server — set ReferenceTool:BaseUrl and ReferenceTool:SessionCookie (see README, \"Configuring secrets\").");

        if (model.ClientId <= 0 || model.Clients.All(c => c.ClientId != model.ClientId))
            return Fail(model, "Select a client.");

        var pan = string.IsNullOrWhiteSpace(model.Pan) ? null : model.Pan.Trim().ToUpperInvariant();
        if (pan is not null && !PanPattern().IsMatch(pan))
            return Fail(model, "PAN must be 10 characters, e.g. AABCV6369H.");

        var hasCin = !string.IsNullOrWhiteSpace(model.Cin);
        var hasName = !string.IsNullOrWhiteSpace(model.CompanyName);

        if (!hasCin && !hasName)
            return Fail(model, "A company name or a CIN / LLPIN is required.");

        var hints = new ResolutionHints(
            State: string.IsNullOrWhiteSpace(model.State) ? null : model.State.Trim(),
            District: string.IsNullOrWhiteSpace(model.District) ? null : model.District.Trim(),
            PinCode: string.IsNullOrWhiteSpace(model.PinCode) ? null : model.PinCode.Trim(),
            IncorporationYear: model.IncorporationYear,
            EntityType: model.EntityType,
            Pan: pan);

        var actor = User.Identity?.Name ?? "auto-fetch";

        if (hasCin)
        {
            var identifier = model.Cin!.Trim().ToUpperInvariant();
            if (!IdentifierPattern().IsMatch(identifier))
                return Fail(model, "Enter a valid company CIN (e.g. U24246DL2003PTC118255) or LLPIN (e.g. AAA-1234).");

            var isLlpin = identifier.Contains('-');
            if (model.EntityType == EntityType.LLP != isLlpin)
                return Fail(model, isLlpin ? "That identifier is an LLPIN — select LLP as the entity type." : "That identifier is a company CIN — select Company as the entity type.");

            // This lookup is scoped to ClientId on purpose. A matching company for another client is not a
            // duplicate and must never be surfaced here: its request, documents and results belong only to
            // that other client. The unique index below is the concurrency-safe backstop for this friendly
            // pre-check.
            var existing = await FindExistingRequestAsync(model.ClientId, identifier, ct);
            if (existing is not null)
                return Existing(model, existing);

            var request = new McaRequest
            {
                ClientId = model.ClientId,
                EntityType = model.EntityType,
                // The tool's search / the workbook fills this in during the job; the identifier stands in until then.
                CompanyName = string.IsNullOrWhiteSpace(model.CompanyName) ? identifier : model.CompanyName.Trim(),
                Cin = identifier,
                Llpin = model.EntityType == EntityType.LLP ? identifier : null,
                Pan = pan,
                AutoFetchCompanyIdentifier = identifier,
                // RequestNumber has a unique index. Give the first insert its own value so concurrent
                // submissions can race only on the client-scoped AutoFetch identifier, not on an empty
                // request number shared by every newly-created request.
                RequestNumber = $"PENDING-{Guid.NewGuid():N}",
                RequestStatus = RequestStatus.Created,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = actor
            };
            try
            {
                // The identity claim, request, and job are one logical unit. In particular, do not enqueue
                // until after the database transaction commits: a crash after commit is recovered from the
                // durable Queued job, while any job-creation failure rolls the request and its unique claim back.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.Requests.Add(request);
                await db.SaveChangesAsync(ct);
                request.RequestNumber = $"MCA-{request.CreatedDate:yyyyMMdd}-{request.RequestId:D6}";
                await db.SaveChangesAsync(ct);

                var correlationId = CorrelationContext.GetOrCreate(HttpContext);
                var job = await jobs.CreateOrResetJobAsync(request, model.IncludeFilings, model.MaxDocumentsPerSection ?? 0, ct, correlationId);
                await transaction.CommitAsync(ct);
                queue.Enqueue(job.AutoFetchJobId);

                if (identity is not null)
                {
                    try
                    {
                        var isHumanPick = !string.IsNullOrWhiteSpace(model.SelectedIdentifier)
                            && string.Equals(model.SelectedIdentifier.Trim(), identifier, StringComparison.OrdinalIgnoreCase);

                        if (isHumanPick)
                            await identity.ApplyHumanSelectionAsync(request.RequestId, identifier, model.CompanyName, hints, actor, ct);
                        else
                            await identity.ResolveForRequestAsync(request.RequestId, model.CompanyName, identifier, hints, actor, ct);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to record identity resolution for request {RequestId}", request.RequestId);
                    }
                }

                if (pipelineAdopter is not null)
                    await pipelineAdopter.TryAdoptAsync(request.RequestId, PipelineRunTrigger.AutoFetch, correlationId, ct);

                return RedirectToAction("Details", "Requests", new { id = request.RequestId });
            }
            catch (DbUpdateException)
            {
                // Two submissions can both pass the read above. The database's filtered unique index is the
                // actual idempotency guarantee; after its expected collision, show the request created by the
                // other submission instead of creating/enqueueing another fetch job.
                db.ChangeTracker.Clear();
                existing = await FindExistingRequestAsync(model.ClientId, identifier, ct);
                if (existing is not null)
                    return Existing(model, existing);
                throw;
            }
        }
        else
        {
            // Name-only intake (Issue #294, plan §5A.3)
            var inputName = model.CompanyName!.Trim();
            var request = new McaRequest
            {
                ClientId = model.ClientId,
                EntityType = model.EntityType,
                CompanyName = inputName,
                Cin = null,
                Llpin = null,
                Pan = pan,
                AutoFetchCompanyIdentifier = null,
                RequestNumber = $"PENDING-{Guid.NewGuid():N}",
                RequestStatus = RequestStatus.Created,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = actor
            };

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                db.Requests.Add(request);
                await db.SaveChangesAsync(ct);
                request.RequestNumber = $"MCA-{request.CreatedDate:yyyyMMdd}-{request.RequestId:D6}";
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            var correlationId = CorrelationContext.GetOrCreate(HttpContext);
            if (identity is not null)
            {
                var resolution = await identity.ResolveForRequestAsync(request.RequestId, inputName, null, hints, actor, ct);
                if (resolution.AppliedToRequest && resolution.Decision.Status == ResolutionStatus.Resolved && !string.IsNullOrWhiteSpace(request.Cin))
                {
                    // AutoSelected and applied to request; create and enqueue job
                    var job = await jobs.CreateOrResetJobAsync(request, model.IncludeFilings, model.MaxDocumentsPerSection ?? 0, ct, correlationId);
                    queue.Enqueue(job.AutoFetchJobId);
                }
            }

            if (pipelineAdopter is not null)
                await pipelineAdopter.TryAdoptAsync(request.RequestId, PipelineRunTrigger.AutoFetch, correlationId, ct);

            return RedirectToAction("Details", "Requests", new { id = request.RequestId });
        }
    }

    /// <summary>Interactive company search (issue #294, plan §5A.3). Live typing uses a bounded indexed
    /// prefix lookup; broader requests can resolve and rank candidates with disambiguators or consult the
    /// reference tool.</summary>
    [HttpGet("/Requests/AutoFetch/search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        CancellationToken ct = default,
        [FromQuery] string? state = null,
        [FromQuery] string? district = null,
        [FromQuery] string? pin = null,
        [FromQuery] int? year = null,
        [FromQuery] string? entityType = null,
        [FromQuery] bool fastOnly = false)
    {
        var query = (q ?? "").Trim();
        if (query.Length < 3)
        {
            if (identity is null) return Ok(Array.Empty<ReferenceCompanyHint>());
            return Ok(Array.Empty<CompanySearchCandidateDto>());
        }

        // Live typing uses one bounded indexed prefix query. The richer resolver and external tool remain
        // available to callers that explicitly request the broader search path.
        if (fastOnly && !IdentifierPattern().IsMatch(query.ToUpperInvariant()))
            return LocalSearchResult(await SearchLocalMasterDataAsync(query, 10, entityType, state, district, pin, year, ct));

        if (identity is not null)
        {
            EntityType? parsedEntityType = null;
            if (Enum.TryParse<EntityType>(entityType, true, out var et))
                parsedEntityType = et;

            var hints = new ResolutionHints(
                State: string.IsNullOrWhiteSpace(state) ? null : state.Trim(),
                District: string.IsNullOrWhiteSpace(district) ? null : district.Trim(),
                PinCode: string.IsNullOrWhiteSpace(pin) ? null : pin.Trim(),
                IncorporationYear: year,
                EntityType: parsedEntityType);

            var isIdentifier = IdentifierPattern().IsMatch(query.ToUpperInvariant());
            try
            {
                var decision = await identity.SuggestAsync(
                    isIdentifier ? null : query,
                    isIdentifier ? query.ToUpperInvariant() : null,
                    hints,
                    ct);

                if (decision.Candidates.Count > 0)
                {
                    var dtos = decision.Candidates.Select(c => new CompanySearchCandidateDto(
                        Identifier: c.Candidate.Identifier,
                        Name: c.Candidate.Name,
                        RecordType: c.Candidate.RecordType.ToString(),
                        Status: c.Candidate.Status,
                        State: c.Candidate.State,
                        District: c.Candidate.District,
                        PinCode: c.Candidate.PinCode,
                        RegistrationDate: c.Candidate.RegistrationDate?.ToString("yyyy-MM-dd"),
                        Category: c.Candidate.Category,
                        Class: c.Candidate.Class,
                        ListingStatus: c.Candidate.ListingStatus,
                        Score: c.Score,
                        MatchPercent: (int)Math.Round(c.Score * 100),
                        Reasons: c.Reasons,
                        IsToolOnly: c.Candidate.IsToolOnly
                    )).ToList();
                    return Ok(dtos);
                }
            }
            catch (SqlException ex) when (ex.Number == -2 && !ct.IsCancellationRequested)
            {
                logger?.LogWarning(ex, "Name resolution timed out for Auto-fetch suggestion query");
            }
        }

        var localHits = await SearchLocalMasterDataAsync(query, 10, entityType, state, district, pin, year, ct);
        if (localHits.Count > 0)
            return LocalSearchResult(localHits);

        if (!client.IsConfigured)
        {
            if (identity is null) return Ok(Array.Empty<ReferenceCompanyHint>());
            return Ok(Array.Empty<CompanySearchCandidateDto>());
        }

        try
        {
            var hits = await client.SearchCompaniesAsync(query, 10, ct);
            if (identity is null) return Ok(hits);

            var dtos = hits.Select(h => new CompanySearchCandidateDto(
                Identifier: h.Cin,
                Name: h.LegalName,
                RecordType: h.CompanyType,
                Status: h.Status,
                State: null,
                District: null,
                PinCode: null,
                RegistrationDate: null,
                Category: null,
                Class: null,
                ListingStatus: null,
                Score: 0.8,
                MatchPercent: 80,
                Reasons: ["Reference tool match"],
                IsToolOnly: true
            )).ToList();
            return Ok(dtos);
        }
        catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Reference-tool search failed for '{Query}'", query);
            if (identity is null) return Ok(Array.Empty<ReferenceCompanyHint>());
            return Ok(Array.Empty<CompanySearchCandidateDto>());
        }
    }

    private IActionResult LocalSearchResult(IReadOnlyList<CompanyMasterRecord> localHits)
    {
        if (identity is null) return Ok(localHits.Select(h => new ReferenceCompanyHint(
            h.Name, h.Identifier, null, h.Status, h.RecordType.ToString())).ToList());

        return Ok(localHits.Select(h => new CompanySearchCandidateDto(
            Identifier: h.Identifier,
            Name: h.Name,
            RecordType: h.RecordType.ToString(),
            Status: h.Status,
            State: h.State,
            District: h.District,
            PinCode: h.PinCode,
            RegistrationDate: h.RegistrationDate?.ToString("yyyy-MM-dd"),
            Category: h.Category,
            Class: h.Class,
            ListingStatus: h.ListingStatus,
            Score: 0.8,
            MatchPercent: 80,
            Reasons: ["Prefix match"],
            IsToolOnly: false
        )).ToList());
    }

    /// <summary>Indexed prefix match over Company/LLP master rows. Foreign-company (FCRN) records are
    /// excluded because the Auto-fetch form does not accept their identifiers.</summary>
    private async Task<List<CompanyMasterRecord>> SearchLocalMasterDataAsync(
        string query, int limit, string? entityType, string? state, string? district, string? pin, int? year, CancellationToken ct)
    {
        var rows = db.CompanyMasterRecords.AsNoTracking()
            .Where(r => (r.RecordType == CompanyMasterRecordType.Company || r.RecordType == CompanyMasterRecordType.Llp)
                && r.Name.StartsWith(query));

        if (Enum.TryParse<EntityType>(entityType, true, out var requestedType))
        {
            if (requestedType == EntityType.Company)
                rows = rows.Where(r => r.RecordType == CompanyMasterRecordType.Company);
            else if (requestedType == EntityType.LLP)
                rows = rows.Where(r => r.RecordType == CompanyMasterRecordType.Llp);
        }
        if (!string.IsNullOrWhiteSpace(state))
            rows = rows.Where(r => r.State == state.Trim());
        if (!string.IsNullOrWhiteSpace(district))
            rows = rows.Where(r => r.District == district.Trim());
        if (!string.IsNullOrWhiteSpace(pin))
            rows = rows.Where(r => r.PinCode == pin.Trim());
        if (year is >= 1 and <= 9998)
        {
            var start = new DateOnly(year.Value, 1, 1);
            var end = start.AddYears(1);
            rows = rows.Where(r => r.RegistrationDate >= start && r.RegistrationDate < end);
        }

        return await rows.OrderBy(r => r.Name).ThenBy(r => r.Identifier).Take(limit)
            .Select(r => new CompanyMasterRecord
            {
                Identifier = r.Identifier,
                Name = r.Name,
                RecordType = r.RecordType,
                Status = r.Status,
                State = r.State,
                District = r.District,
                PinCode = r.PinCode,
                RegistrationDate = r.RegistrationDate,
                Category = r.Category,
                Class = r.Class,
                ListingStatus = r.ListingStatus
            })
            .ToListAsync(ct);
    }

    [HttpGet("/Requests/{id:long}/autofetch/status")]
    public async Task<IActionResult> Status(long id, CancellationToken ct)
    {
        var job = await db.AutoFetchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestId == id, ct);
        if (job is null) return NotFound();
        var requestStatus = await db.Requests.AsNoTracking().Where(r => r.RequestId == id).Select(r => r.RequestStatus).FirstOrDefaultAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(ToDto(job, requestStatus));
    }

    [HttpPost("/Requests/{id:long}/autofetch/retry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(long id, CancellationToken ct)
    {
        var correlationId = CorrelationContext.GetOrCreate(HttpContext);
        var job = await jobs.RequeueAsync(id, ct, correlationId);
        if (job is null) return NotFound();
        if (job.Status == AutoFetchJobStatus.Queued)
        {
            queue.Enqueue(job.AutoFetchJobId);
            TempData["AutoFetchOk"] = "Auto-fetch queued again — completed steps are kept, the rest is retried.";
        }
        else
        {
            TempData["AutoFetchError"] = "Auto-fetch is still running for this request.";
        }
        return RedirectToAction("Details", "Requests", new { id });
    }

    [Authorize(AuthenticationSchemes = "InternalReviewer")]
    [HttpPost("/Requests/{id:long}/recheck")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Recheck(long id, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == id, ct);
        if (request is null) return NotFound();
        var identifier = (request.Cin ?? request.Llpin ?? "").Trim().ToUpperInvariant();
        if (!IdentifierPattern().IsMatch(identifier) || request.LatestCompletedIngestionRunId is null)
            return BadRequest("A completed request with a valid CIN or LLPIN is required.");
        if (!options.Value.IsConfigured || !options.Value.RefreshBeforeFetch)
        {
            TempData["AutoFetchError"] = "Company re-check requires the configured reference tool and its freshness gate.";
            return RedirectToAction("Details", "Requests", new { id });
        }

        var job = await jobs.TryQueueRecheckAsync(id, CorrelationContext.GetOrCreate(HttpContext), ct);
        if (job is null)
            TempData["AutoFetchError"] = "This request is still being processed or a company re-check is already active. Refresh the page for its status.";
        else
        {
            queue.Enqueue(job.AutoFetchJobId);
            TempData["AutoFetchOk"] = "Company re-check queued. Previous completed results remain available while fresh workbooks are retrieved.";
        }
        return RedirectToAction("Details", "Requests", new { id });
    }

    /// <summary>Approves spending 1 credit to unlock the request's company (#266). Any signed-in internal user
    /// may approve (owner decision, plan §9 #12); a reason is mandatory and the approver is recorded. The
    /// approval never expires and covers every request waiting on the company. The unlock is then attempted
    /// straight away rather than on the next poll.</summary>
    [HttpPost("/Requests/{id:long}/autofetch/unlock/approve")]
    [Authorize(AuthenticationSchemes = "InternalReviewer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveUnlock(long id, [FromForm] string? reason, CancellationToken ct)
    {
        if (unlock is null || gates is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        var job = await db.AutoFetchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestId == id, ct);
        if (job is null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["AutoFetchError"] = "Give a reason for approving the unlock — it spends 1 credit.";
            return RedirectToAction("Details", "Requests", new { id });
        }
        if (job.Status != AutoFetchJobStatus.WaitingForUnlock)
        {
            TempData["AutoFetchError"] = "This request isn't waiting for an unlock approval.";
            return RedirectToAction("Details", "Requests", new { id });
        }

        try
        {
            // Null means the company turned out to be unlocked already — nothing to approve; just resume.
            await unlock.ApproveAsync(id, User.Identity?.Name ?? "unknown", reason, ct);
        }
        catch (InvalidOperationException ex)
        {
            TempData["AutoFetchError"] = ex.Message;
            return RedirectToAction("Details", "Requests", new { id });
        }
        try
        {
            await gates.ResumeAsync(job.Cin, job.Bid, ct);
        }
        catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The approval is stored; the poll worker will act on it — don't turn a transient tool error into an error page.
            logger?.LogWarning(ex, "Unlock approved for request {RequestId}, but resuming immediately failed", id);
            TempData["AutoFetchOk"] = "Unlock approved. The reference tool couldn't be reached just now; the unlock will be attempted automatically within a few minutes.";
            return RedirectToAction("Details", "Requests", new { id });
        }
        var after = await db.AutoFetchJobs.AsNoTracking().Where(j => j.RequestId == id).Select(j => new { j.Status, j.StatusMessage, j.FailureReason }).FirstAsync(ct);
        if (after.Status == AutoFetchJobStatus.WaitingForUnlock)
            TempData["AutoFetchOk"] = $"Unlock approved. {after.StatusMessage}";
        else if (after.Status == AutoFetchJobStatus.Failed)
            TempData["AutoFetchError"] = after.FailureReason;
        else
            TempData["AutoFetchOk"] = "Unlock approved — the company is unlocked and auto-fetch has resumed.";
        return RedirectToAction("Details", "Requests", new { id });
    }

    public static AutoFetchStatusDto ToDto(AutoFetchJob job, RequestStatus requestStatus)
    {
        IReadOnlyList<string> warnings;
        try { warnings = JsonSerializer.Deserialize<List<string>>(job.WarningsJson) ?? []; }
        catch (JsonException) { warnings = []; }
        return new AutoFetchStatusDto(job.RequestId, job.Status.ToString(), job.IsTerminal, job.ProgressPercent, job.StatusMessage,
            job.FailureReason, warnings, job.FilesTotal, job.FilesDownloaded, job.FilesFailed, job.BytesDownloaded,
            job.RegistryTotalCount, job.RegistryListedCount, requestStatus.ToString(), job.StartedUtc, job.CompletedUtc);
    }

    private ViewResult Fail(AutoFetchRequestViewModel model, string message)
    {
        model.ErrorMessage = message;
        return View("~/Views/Requests/AutoFetch.cshtml", model);
    }

    private ViewResult Existing(AutoFetchRequestViewModel model, McaRequest request)
    {
        model.ExistingRequestId = request.RequestId;
        model.ExistingRequestNumber = request.RequestNumber;
        model.ExistingRequestStatus = request.RequestStatus.ToString();
        return View("~/Views/Requests/AutoFetch.cshtml", model);
    }

    private Task<McaRequest?> FindExistingRequestAsync(long clientId, string identifier, CancellationToken ct) =>
        db.Requests.AsNoTracking().FirstOrDefaultAsync(
            request => request.ClientId == clientId && request.AutoFetchCompanyIdentifier == identifier,
            ct);

    private Task<List<Client>> ActiveClientsAsync() =>
        db.Clients.Where(c => c.IsActive).OrderBy(c => c.ClientName).ToListAsync();
}
