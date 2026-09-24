using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Controllers;

/// <summary>Read-only views of the pipeline coordinator (docs/pipeline-automation-plan.md §3.5). No action
/// here changes any state.</summary>
public class PipelineController(AppDbContext db, IOptionsMonitor<PipelineOptions> options) : Controller
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

        return View(new PipelineBoardViewModel
        {
            CoordinatorEnabled = options.CurrentValue.Enabled,
            Mode = CurrentMode(),
            Outcome = outcome,
            StuckMinutes = stuckMinutes,
            ReasonCode = reasonCode,
            Counts = counts,
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
