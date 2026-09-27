using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Controllers;

/// <summary>Pipeline coordinator board and ambiguity queue (docs/pipeline-automation-plan.md §3.5, §5A.3).</summary>
public class PipelineController(
    AppDbContext db,
    IOptionsMonitor<PipelineOptions> options,
    IdentitySelectionService? identitySelection = null,
    AutoFetchJobService? autoFetchJobs = null,
    AutoFetchQueue? autoFetchQueue = null,
    IOptions<ReferenceToolOptions>? referenceToolOptions = null) : Controller
{
    private const int TimelineLimit = 200;
    private const int MaxUnlockAlertCompanies = 200;

    private static readonly PipelineOutcome[] LiveOutcomes = [PipelineOutcome.InProgress, PipelineOutcome.CoreReady, PipelineOutcome.NeedsAttention];

    /// <summary>The request's most recent run and its stage states; 404 when the request has none. Same
    /// access as the Details page and <c>/autofetch/status</c> it sits beside.</summary>
    [HttpGet("/Requests/{id:long}/pipeline/status")]
    public async Task<IActionResult> Status(long id, CancellationToken ct)
    {
        var run = await db.PipelineRuns.AsNoTracking().Where(r => r.RequestId == id)
            .OrderByDescending(r => r.PipelineRunId).FirstOrDefaultAsync(ct);
        if (run is null) return NotFound();
        var stages = await StagesAsync([run.PipelineRunId], ct);
        // Newest TimelineLimit steps, returned oldest first so the page reads top to bottom.
        var events = await db.PipelineEvents.AsNoTracking().Where(e => e.PipelineRunId == run.PipelineRunId)
            .OrderByDescending(e => e.PipelineEventId).Take(TimelineLimit).ToListAsync(ct);
        events.Reverse();
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            run.PipelineRunId,
            trigger = run.Trigger.ToString(),
            outcome = run.Outcome.ToString(),
            isLive = LiveOutcomes.Contains(run.Outcome),
            run.CreatedUtc,
            run.CoreReadyUtc,
            run.CompletedUtc,
            stages = stages.GetValueOrDefault(run.PipelineRunId, []),
            mode = CurrentMode(),
            events = events.Select(e => new PipelineEventDto(e.AtUtc, e.Stage.ToString(), e.Action, e.Actor, e.ReasonCode,
                PipelineEventText.Describe(e)))
        });
    }

    /// <summary>Companies waiting for someone to approve a paid unlock, for the alert shown on every page. One
    /// entry per company (an approval covers every request waiting on it); a company that already has an open
    /// approval is being unlocked and isn't listed.</summary>
    [HttpGet("/pipeline/unlock-alerts")]
    [Authorize(AuthenticationSchemes = "InternalReviewer")]
    public async Task<IActionResult> UnlockAlerts(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Grouped in SQL, so a backlog of waiting jobs for one company can't crowd other companies out; the page
        // shows the first few it hasn't snoozed and counts the rest.
        var companies = await db.AutoFetchJobs.AsNoTracking()
            .Where(j => j.Status == AutoFetchJobStatus.WaitingForUnlock
                && !db.UnlockApprovals.Any(a => a.Identifier == j.Cin.Trim().ToUpper() && a.ConsumedAdmissionId == null && a.ExpiresUtc > now))
            .GroupBy(j => j.Cin.Trim().ToUpper())
            .Select(g => new { Identifier = g.Key, FirstJobId = g.Min(j => j.AutoFetchJobId), Count = g.Count(), Since = g.Min(j => j.CreatedUtc) })
            .OrderBy(g => g.Since).ThenBy(g => g.FirstJobId)
            .Take(MaxUnlockAlertCompanies)
            .ToListAsync(ct);
        var firstJobIds = companies.Select(c => c.FirstJobId).ToList();
        var firstJobs = await db.AutoFetchJobs.AsNoTracking().Where(j => firstJobIds.Contains(j.AutoFetchJobId))
            .Select(j => new { j.AutoFetchJobId, j.RequestId, j.StatusMessage, j.Request!.RequestNumber, j.Request.CompanyName })
            .ToDictionaryAsync(j => j.AutoFetchJobId, ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(companies.Where(c => firstJobs.ContainsKey(c.FirstJobId)).Select(c =>
        {
            var j = firstJobs[c.FirstJobId];
            return new UnlockAlertDto(c.Identifier, j.CompanyName, j.RequestId, j.RequestNumber, c.Count, j.StatusMessage, c.Since);
        }));
    }

    private string CurrentMode()
    {
        var o = options.CurrentValue;
        return !o.Enabled ? "Off" : o.Mode.ToString();
    }

    [HttpGet("/Pipeline")]
    [Authorize(AuthenticationSchemes = "InternalReviewer")]
    public async Task<IActionResult> Index(PipelineOutcome? outcome, int? stuckMinutes, string? reasonCode, CancellationToken ct)
    {
        var query = db.PipelineRuns.AsNoTracking().AsQueryable();
        if (outcome is { } o) query = query.Where(r => r.Outcome == o);
        if (!string.IsNullOrWhiteSpace(reasonCode))
        {
            var code = reasonCode.Trim();
            query = query.Where(r => db.PipelineStageStates.Any(s => s.PipelineRunId == r.PipelineRunId && s.ReasonCode == code));
        }
        if (stuckMinutes is > 0)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-stuckMinutes.Value);
            query = query.Where(r => LiveOutcomes.Contains(r.Outcome)
                && !db.PipelineStageStates.Any(s => s.PipelineRunId == r.PipelineRunId && s.UpdatedUtc >= cutoff));
        }

        var runs = await query.OrderByDescending(r => r.PipelineRunId).Take(200)
            .Select(r => new
            {
                r.PipelineRunId, r.RequestId, r.Trigger, r.Outcome, r.CreatedUtc, r.CoreReadyUtc,
                r.Request!.RequestNumber, r.Request.CompanyName
            })
            .ToListAsync(ct);
        var stages = await StagesAsync(runs.Select(r => r.PipelineRunId).ToList(), ct);
        var counts = await db.PipelineRuns.AsNoTracking().GroupBy(r => r.Outcome)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var ambiguityCount = await db.PipelineStageStates.AsNoTracking()
            .Where(s => s.Stage == PipelineStage.Resolve && s.State == PipelineStageStateKind.NeedsAttention)
            .Join(db.PipelineRuns.AsNoTracking().Where(r => LiveOutcomes.Contains(r.Outcome)),
                  s => s.PipelineRunId,
                  r => r.PipelineRunId,
                  (s, r) => s.PipelineRunId)
            .Distinct()
            .CountAsync(ct);

        return View(new PipelineBoardViewModel
        {
            CoordinatorEnabled = options.CurrentValue.Enabled,
            Mode = CurrentMode(),
            Outcome = outcome,
            StuckMinutes = stuckMinutes,
            ReasonCode = reasonCode,
            Counts = counts,
            AmbiguityCount = ambiguityCount,
            Rows = runs.Select(r =>
            {
                var s = stages.GetValueOrDefault(r.PipelineRunId, []);
                return new PipelineBoardRow
                {
                    PipelineRunId = r.PipelineRunId, RequestId = r.RequestId, RequestNumber = r.RequestNumber,
                    CompanyName = r.CompanyName, Trigger = r.Trigger, Outcome = r.Outcome, CreatedUtc = r.CreatedUtc,
                    CoreReadyUtc = r.CoreReadyUtc, Stages = s,
                    LastChangeUtc = s.Count == 0 ? r.CreatedUtc : s.Max(x => x.UpdatedUtc)
                };
            }).ToList()
        });
    }

    /// <summary>Ambiguity queue board (issue #295, plan §5A.3): requests whose Resolve stage needs
    /// attention, showing ranked candidates with disambiguators and "Select this CIN" actions.</summary>
    [HttpGet("/Pipeline/AmbiguityQueue")]
    [Authorize(AuthenticationSchemes = "InternalReviewer")]
    public async Task<IActionResult> AmbiguityQueue([FromQuery] long? requestId, CancellationToken ct)
    {
        var query = db.PipelineStageStates.AsNoTracking()
            .Where(s => s.Stage == PipelineStage.Resolve && s.State == PipelineStageStateKind.NeedsAttention)
            .Join(db.PipelineRuns.AsNoTracking().Where(r => LiveOutcomes.Contains(r.Outcome)),
                  s => s.PipelineRunId,
                  r => r.PipelineRunId,
                  (s, r) => new { StageState = s, Run = r });

        if (requestId is long targetId)
            query = query.Where(x => x.Run.RequestId == targetId);

        var stageRows = await query
            .OrderByDescending(x => x.StageState.UpdatedUtc)
            .Take(100)
            .Select(x => new
            {
                x.Run.RequestId,
                x.Run.Request!.RequestNumber,
                ClientName = x.Run.Request.Client != null ? x.Run.Request.Client.ClientName : null,
                x.Run.Request.CompanyName,
                CurrentCin = x.Run.Request.Cin ?? x.Run.Request.Llpin,
                x.StageState.ReasonCode,
                x.StageState.ReasonDetail,
                x.StageState.SourceRef,
                x.StageState.UpdatedUtc
            })
            .ToListAsync(ct);

        var distinctRequests = stageRows
            .GroupBy(x => x.RequestId)
            .Select(g => g.First())
            .ToList();

        var requestIds = distinctRequests.Select(x => x.RequestId).ToList();

        var sourceResolutionIds = distinctRequests
            .Select(x => x.SourceRef ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var resolutions = await db.IdentityResolutions.AsNoTracking()
            .Where(r => (r.RequestId != null && requestIds.Contains(r.RequestId.Value)) || sourceResolutionIds.Contains(r.IdentityResolutionId))
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct);

        var resolutionsBySource = resolutions.ToDictionary(r => r.IdentityResolutionId);
        var resolutionsByRequest = resolutions
            .Where(r => r.RequestId != null)
            .GroupBy(r => r.RequestId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var items = new List<AmbiguityQueueItemViewModel>();

        foreach (var row in distinctRequests)
        {
            IdentityResolution? res = null;
            if (row.SourceRef is { } srcId && resolutionsBySource.TryGetValue(srcId, out var bySource))
                res = bySource;
            else if (resolutionsByRequest.TryGetValue(row.RequestId, out var byReq))
                res = byReq;

            var candidates = new List<AmbiguityCandidateViewModel>();
            ResolutionHints hints = ResolutionHints.None;

            if (res is not null)
            {
                if (!string.IsNullOrWhiteSpace(res.HintsJson))
                {
                    try { hints = JsonSerializer.Deserialize<ResolutionHints>(res.HintsJson, CandidateJsonOptions) ?? ResolutionHints.None; }
                    catch (JsonException) { }
                }

                if (!string.IsNullOrWhiteSpace(res.CandidatesJson))
                {
                    try
                    {
                        var rawCandidates = JsonSerializer.Deserialize<List<SerializedCandidateDto>>(res.CandidatesJson, CandidateJsonOptions);
                        if (rawCandidates is not null)
                        {
                            candidates = rawCandidates.Select(c => new AmbiguityCandidateViewModel
                            {
                                Identifier = c.Identifier ?? "",
                                Name = c.Name ?? "",
                                RecordType = c.RecordType,
                                Status = c.Status,
                                State = c.State,
                                District = c.District,
                                RegistrationDate = c.RegistrationDate,
                                ToolOnly = c.ToolOnly,
                                Score = c.Score,
                                Reasons = c.Reasons ?? [],
                                IsRecommended = !string.IsNullOrWhiteSpace(res.RecommendedIdentifier)
                                    && string.Equals(res.RecommendedIdentifier, c.Identifier, StringComparison.OrdinalIgnoreCase)
                            }).ToList();
                        }
                    }
                    catch (JsonException) { }
                }
            }

            items.Add(new AmbiguityQueueItemViewModel
            {
                RequestId = row.RequestId,
                RequestNumber = row.RequestNumber,
                ClientName = row.ClientName,
                InputName = res?.InputName ?? row.CompanyName,
                CurrentCin = row.CurrentCin,
                Hints = hints,
                ReasonCode = row.ReasonCode ?? res?.ReasonCode ?? "IDENTITY_AMBIGUOUS",
                ReasonDetail = row.ReasonDetail,
                RecommendedIdentifier = res?.RecommendedIdentifier,
                UpdatedUtc = row.UpdatedUtc,
                Candidates = candidates
            });
        }

        return View(new AmbiguityQueueViewModel
        {
            Items = items,
            FilterRequestId = requestId
        });
    }

    /// <summary>Select this CIN action (issue #295, plan §5A.3). Human selection of a company identifier
    /// for a request whose Resolve stage needs attention.</summary>
    [HttpPost("/Pipeline/Identity/Select")]
    [Authorize(AuthenticationSchemes = "InternalReviewer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectCin(
        [FromForm] long requestId,
        [FromForm] string identifier,
        [FromForm] string? returnUrl = null,
        CancellationToken ct = default)
    {
        var id = (identifier ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(id))
        {
            TempData["PipelineError"] = "Select or enter a valid company identifier.";
            return SafeRedirect(returnUrl);
        }

        if (identitySelection is null)
        {
            TempData["PipelineError"] = "Identity selection service is unavailable.";
            return SafeRedirect(returnUrl);
        }

        var actor = User.Identity?.Name ?? "reviewer";
        try
        {
            var result = await identitySelection.SelectAsync(requestId, id, actor, ct);
            if (result.Resolution.AppliedToRequest)
            {
                // If the request had no auto-fetch job (e.g. name-only intake), create and enqueue one now.
                var hasJob = await db.AutoFetchJobs.AnyAsync(j => j.RequestId == requestId, ct);
                if (!hasJob && autoFetchJobs is not null && autoFetchQueue is not null)
                {
                    var request = await db.Requests.FirstAsync(r => r.RequestId == requestId, ct);
                    var opts = referenceToolOptions?.Value;
                    var correlationId = CorrelationContext.GetOrCreate(HttpContext);
                    var newJob = await autoFetchJobs.CreateOrResetJobAsync(
                        request,
                        opts?.IncludeFilingsByDefault ?? true,
                        opts?.DefaultMaxDocumentsPerSection > 0 ? opts.DefaultMaxDocumentsPerSection : 0,
                        ct,
                        correlationId);
                    autoFetchQueue.Enqueue(newJob.AutoFetchJobId);
                }

                TempData["PipelineOk"] = $"Successfully selected {id} for request.";
            }
            else
            {
                var reason = result.Resolution.ExistingRequestId is not null
                    ? $"This client already has request #{result.Resolution.ExistingRequestId} for {id}."
                    : $"Could not apply {id}: {result.Resolution.Decision.ReasonCode}.";
                TempData["PipelineError"] = reason;
            }
        }
        catch (Exception ex)
        {
            TempData["PipelineError"] = $"Selection failed: {ex.Message}";
        }

        return SafeRedirect(returnUrl);
    }

    private IActionResult SafeRedirect(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            if (returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\"))
                return Redirect(returnUrl);
            if (Url is not null && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
        }
        return RedirectToAction(nameof(AmbiguityQueue));
    }

    private static readonly JsonSerializerOptions CandidateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed class SerializedCandidateDto
    {
        public string? Identifier { get; set; }
        public string? Name { get; set; }
        public string? RecordType { get; set; }
        public string? Status { get; set; }
        public string? State { get; set; }
        public string? District { get; set; }
        public DateOnly? RegistrationDate { get; set; }
        public bool ToolOnly { get; set; }
        public double Score { get; set; }
        public List<string>? Reasons { get; set; }
    }

    private async Task<Dictionary<long, List<PipelineStageStatusDto>>> StagesAsync(List<long> runIds, CancellationToken ct)
    {
        var rows = await db.PipelineStageStates.AsNoTracking().Where(s => runIds.Contains(s.PipelineRunId)).ToListAsync(ct);
        return rows.GroupBy(s => s.PipelineRunId).ToDictionary(
            g => g.Key,
            g => g.OrderBy(s => s.Stage)
                .Select(s => new PipelineStageStatusDto(s.Stage.ToString(), s.State.ToString(), s.SkipKind?.ToString(),
                    s.ReasonCode, s.ReasonDetail, s.SourceRef, s.UpdatedUtc))
                .ToList());
    }
}

public sealed record PipelineEventDto(DateTime AtUtc, string Stage, string Action, string Actor, string? ReasonCode, string Text);

public sealed record UnlockAlertDto(string Identifier, string CompanyName, long RequestId, string RequestNumber,
    int WaitingRequests, string? Message, DateTime WaitingSinceUtc);
