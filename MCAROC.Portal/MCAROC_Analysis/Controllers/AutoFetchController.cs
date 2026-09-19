using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.AutoFetch;
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
    ILogger<AutoFetchController>? logger = null) : Controller
{
    // Company CIN (21 chars) or LLPIN (AAA-1234) — same rule the pre-login flow applies.
    [GeneratedRegex("^(?:[LU][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}|[A-Z]{3}-[0-9]{4})$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Z]{5}[0-9]{4}[A-Z]$")]
    private static partial Regex PanPattern();

    [HttpGet("/Requests/AutoFetch")]
    public async Task<IActionResult> New()
    {
        var opts = options.Value;
        return View("~/Views/Requests/AutoFetch.cshtml", new AutoFetchRequestViewModel
        {
            Clients = await ActiveClientsAsync(),
            IncludeFilings = opts.IncludeFilingsByDefault,
            MaxDocumentsPerSection = opts.DefaultMaxDocumentsPerSection > 0 ? opts.DefaultMaxDocumentsPerSection : null,
            IsConfigured = opts.IsConfigured
        });
    }

    [HttpPost("/Requests/AutoFetch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(AutoFetchRequestViewModel model, CancellationToken ct)
    {
        model.Clients = await ActiveClientsAsync();
        model.IsConfigured = options.Value.IsConfigured;

        if (!model.IsConfigured)
            return Fail(model, "Auto-fetch is not configured on this server — set ReferenceTool:BaseUrl and ReferenceTool:SessionCookie (see README, \"Configuring secrets\").");

        var identifier = (model.Cin ?? "").Trim().ToUpperInvariant();
        if (identifier.Length == 0)
            return Fail(model, "A CIN or LLPIN is required.");
        if (!IdentifierPattern().IsMatch(identifier))
            return Fail(model, "Enter a valid company CIN (e.g. U24246DL2003PTC118255) or LLPIN (e.g. AAA-1234).");

        var isLlpin = identifier.Contains('-');
        if (model.EntityType == EntityType.LLP != isLlpin)
            return Fail(model, isLlpin ? "That identifier is an LLPIN — select LLP as the entity type." : "That identifier is a company CIN — select Company as the entity type.");

        var pan = string.IsNullOrWhiteSpace(model.Pan) ? null : model.Pan.Trim().ToUpperInvariant();
        if (pan is not null && !PanPattern().IsMatch(pan))
            return Fail(model, "PAN must be 10 characters, e.g. AABCV6369H.");

        if (model.ClientId <= 0 || model.Clients.All(c => c.ClientId != model.ClientId))
            return Fail(model, "Select a client.");

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
            CreatedBy = "auto-fetch"
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

            var job = await jobs.CreateOrResetJobAsync(request, model.IncludeFilings, model.MaxDocumentsPerSection ?? 0, ct);
            await transaction.CommitAsync(ct);
            queue.Enqueue(job.AutoFetchJobId);

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

    /// <summary>Auto-complete for the form: proxies the reference tool's own company search. Returns an
    /// empty list (not an error) whenever the tool can't be asked, so the form still works by CIN alone.</summary>
    [HttpGet("/Requests/AutoFetch/search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        var query = (q ?? "").Trim();
        if (query.Length < 3) return Ok(Array.Empty<ReferenceCompanyHint>());
        if (!client.IsConfigured) return Ok(Array.Empty<ReferenceCompanyHint>());
        try
        {
            var hits = await client.SearchCompaniesAsync(query, 10, ct);
            return Ok(hits);
        }
        catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Reference-tool search failed for '{Query}'", query);
            return Ok(Array.Empty<ReferenceCompanyHint>());
        }
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
        var job = await jobs.RequeueAsync(id, ct);
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
