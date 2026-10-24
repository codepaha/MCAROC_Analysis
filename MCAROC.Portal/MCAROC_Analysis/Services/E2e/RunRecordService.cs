using System.Globalization;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.E2e;

public sealed record RunRecordStage(string Stage, string State, string? SkipKind, string? ReasonCode, string? ReasonDetail, int Attempts, DateTime? UpdatedUtc);

public sealed record RunRecordFetch(string Status, string? Message, string? FailureReason, IReadOnlyList<string> Warnings, int RegistryListed, int RegistryTotal,
    int FilesTotal, int FilesDownloaded, int FilesFailed, long BytesDownloaded, int Attempts, DateTime CreatedUtc, DateTime? CompletedUtc);

public sealed record RunRecordLifecycle(string State, DateTime? UnlockedUtc, DateTime? LastRefreshRequestedUtc, DateTime? LastRefreshCompletedUtc);

public sealed record RunRecordLitigation(string Status, bool? ExactMatch, IReadOnlyList<LitigationKeyword> Keywords, int Cases, int CasesShown, DateTime CreatedUtc);

public sealed record RunRecordDownload(DateTime AtUtc, string By, string? Variant, long? Bytes, int? Pages);

/// <param name="PreflightBefore">The last pre-flight check run before the request was created; null when none was. Its checks are empty when the log kept
/// only the outcome (<paramref name="PreflightChecksKept"/> false).</param>
public sealed record RunRecord(
    long RequestId, string RequestNumber, string CompanyName, string? Identifier, string EntityType, string? ClientName, DateTime CreatedUtc,
    PreflightRunPayload? PreflightBefore, DateTime? PreflightAtUtc, bool PreflightChecksKept,
    string? PipelineOutcome, DateTime? PipelineCreatedUtc, DateTime? CoreReadyUtc, DateTime? PipelineCompletedUtc, IReadOnlyList<RunRecordStage> Stages,
    RunRecordFetch? Fetch, RunRecordLifecycle? Lifecycle, RunRecordLitigation? Litigation, IReadOnlyList<RunRecordDownload> DossierDownloads)
{
    public IEnumerable<RunRecordStage> StagesWithWarnings => Stages.Where(s => s.State is "SucceededWithWarnings" or "NeedsAttention" || s.SkipKind == "Warning");
}

/// <summary>One request's E2E run, everything #514 asks a matrix run to record, from what the portal already keeps: the pre-flight check made before it,
/// every pipeline stage with its outcome and reason, the fetch (registry listed against total, downloads and failures), the company's refresh state,
/// the planned litigation keyword list and case counts, and every dossier download. Read-only. Whether the dossier was opened and reviewed is left
/// to the person running the round (a box in the Markdown).</summary>
public sealed class RunRecordService(AppDbContext db)
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<RunRecord?> GetAsync(long requestId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().Include(r => r.Client).FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return null;
        var identifier = request.Cin ?? request.Llpin ?? request.AutoFetchCompanyIdentifier;

        var preflight = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActionType.PreflightRun && a.TimestampUtc <= request.CreatedDate)
            .OrderByDescending(a => a.TimestampUtc).Select(a => new { a.TimestampUtc, a.EventPayloadJson, a.Status }).FirstOrDefaultAsync(ct);
        // A run the log could not keep whole (a "truncated" marker, or unreadable) still happened: its overall result is the audit row's status.
        var preflightRun = preflight is null ? null : Read<PreflightRunPayload>(preflight.EventPayloadJson);
        var checksKept = preflightRun?.Checks is not null && preflightRun.Overall is not null;
        if (preflight is not null && !checksKept)
            preflightRun = new PreflightRunPayload(
                preflight.Status switch { AuditStatus.Failure => "Fail", AuditStatus.Warning => "Warn", _ => "Pass" }, preflight.TimestampUtc, []);

        var run = await db.PipelineRuns.AsNoTracking().Where(r => r.RequestId == requestId).OrderByDescending(r => r.PipelineRunId).FirstOrDefaultAsync(ct);
        // Ordered in memory: the stage is stored as text, so the database would sort it alphabetically, not in pipeline order.
        var stages = run is null ? [] : (await db.PipelineStageStates.AsNoTracking().Where(s => s.PipelineRunId == run.PipelineRunId).ToListAsync(ct))
            .OrderBy(s => s.Stage).ToList();

        var job = await db.AutoFetchJobs.AsNoTracking().Where(j => j.RequestId == requestId).OrderByDescending(j => j.AutoFetchJobId).FirstOrDefaultAsync(ct);
        var lifecycleKey = (job?.Cin ?? identifier)?.Trim().ToUpperInvariant();
        var lifecycle = lifecycleKey is null ? null
            : await db.CompanyReportLifecycles.AsNoTracking().FirstOrDefaultAsync(l => l.Identifier == lifecycleKey, ct);

        var search = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == requestId)
            .OrderByDescending(j => j.LitigationSearchJobId).FirstOrDefaultAsync(ct);
        var cases = db.LitigationCases.AsNoTracking().Where(c => c.RequestId == requestId && c.MergedIntoCaseId == null);
        var caseCount = search is null ? 0 : await cases.CountAsync(ct);
        var shownCount = search is null ? 0 : await cases.Where(LitigationAttribution.IsShown).CountAsync(ct);

        var downloads = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActionType.DossierDownloaded && a.RequestId == requestId)
            .OrderBy(a => a.TimestampUtc).Select(a => new { a.TimestampUtc, a.ActorId, a.EventPayloadJson }).ToListAsync(ct);

        return new RunRecord(
            request.RequestId, request.RequestNumber, request.CompanyName, identifier, request.EntityType.ToString(), request.Client?.ClientName,
            request.CreatedDate,
            preflightRun, preflight?.TimestampUtc, checksKept,
            run?.Outcome.ToString(), run?.CreatedUtc, run?.CoreReadyUtc, run?.CompletedUtc,
            stages.Select(s => new RunRecordStage(s.Stage.ToString(), s.State.ToString(), s.SkipKind?.ToString(), s.ReasonCode, s.ReasonDetail, s.Attempts, s.UpdatedUtc)).ToList(),
            job is null ? null : new RunRecordFetch(job.Status.ToString(), job.StatusMessage, job.FailureReason, Read<List<string>>(job.WarningsJson) ?? [],
                job.RegistryListedCount, job.RegistryTotalCount, job.FilesTotal, job.FilesDownloaded, job.FilesFailed, job.BytesDownloaded,
                job.AttemptCount, job.CreatedUtc, job.CompletedUtc),
            lifecycle is null ? null : new RunRecordLifecycle(lifecycle.State.ToString(), lifecycle.UnlockedUtc, lifecycle.LastRefreshRequestedUtc, lifecycle.LastRefreshCompletedUtc),
            search is null ? null : new RunRecordLitigation(search.Status.ToString(), search.ExactMatch, LitigationKeyword.ParseJson(search.KeywordsJson),
                caseCount, shownCount, search.CreatedUtc),
            downloads.Select(d =>
            {
                var p = Read<DossierDownloadPayload>(d.EventPayloadJson);
                return new RunRecordDownload(d.TimestampUtc, d.ActorId, p?.Variant, p?.Bytes, p?.Pages);
            }).ToList());
    }

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, PayloadJson); } catch (JsonException) { return null; }
    }

    /// <summary>The record as Markdown, for <c>artifacts/e2e-&lt;date&gt;/</c> and the issue: every time in IST.</summary>
    public static string ToMarkdown(RunRecord r)
    {
        var md = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        md.AppendLine($"# Run record: {r.RequestNumber}, {r.CompanyName}");
        md.AppendLine();
        md.AppendLine($"- Identifier: {r.Identifier ?? "(none)"} ({r.EntityType}); client: {r.ClientName ?? "-"}");
        md.AppendLine($"- Request created: {Ist.Format(r.CreatedUtc)}");
        md.AppendLine(r.PreflightBefore is { } p
            ? $"- Pre-flight before it: **{p.Overall}** at {Ist.Format(r.PreflightAtUtc)}" + (!r.PreflightChecksKept ? " (the check details were not kept)" : "")
                + (p.Checks.Any(c => c.Outcome != "Pass")
                ? "; " + string.Join("; ", p.Checks.Where(c => c.Outcome != "Pass").Select(c => $"{c.Key} {c.Outcome}: {c.Detail}")) : "")
            : "- Pre-flight before it: **none run**");
        md.AppendLine();

        md.AppendLine($"## Pipeline: {r.PipelineOutcome ?? "no run"}");
        if (r.PipelineCreatedUtc is { } started)
            md.AppendLine($"Started {Ist.Format(started)}; core ready {Ist.Format(r.CoreReadyUtc)}; completed {Ist.Format(r.PipelineCompletedUtc)}.");
        md.AppendLine();
        md.AppendLine("| Stage | State | Reason | Detail | Attempts | Updated |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in r.Stages)
            md.AppendLine($"| {s.Stage} | {s.State}{(s.SkipKind is null ? "" : $" ({s.SkipKind})")} | {s.ReasonCode} | {Cell(s.ReasonDetail)} | {s.Attempts} | {Ist.Format(s.UpdatedUtc)} |");
        md.AppendLine();

        if (r.Fetch is { } f)
        {
            md.AppendLine($"## Fetch: {f.Status}");
            md.AppendLine($"- Registry listed {f.RegistryListed.ToString("N0", inv)} of {f.RegistryTotal.ToString("N0", inv)}; files {f.FilesDownloaded.ToString("N0", inv)} of {f.FilesTotal.ToString("N0", inv)} downloaded, {f.FilesFailed.ToString("N0", inv)} failed ({f.BytesDownloaded / (1024.0 * 1024):N1} MB); attempts {f.Attempts}");
            if (f.FailureReason is not null) md.AppendLine($"- Failure: {f.FailureReason}");
            foreach (var w in f.Warnings) md.AppendLine($"- Warning: {w}");
            md.AppendLine();
        }
        if (r.Lifecycle is { } l)
            md.AppendLine($"Company in the reference tool: {l.State}; unlocked {Ist.Format(l.UnlockedUtc)}; refresh requested {Ist.Format(l.LastRefreshRequestedUtc)}, data as of {Ist.Format(l.LastRefreshCompletedUtc)}.\n");

        if (r.Litigation is { } lit)
        {
            md.AppendLine($"## Litigation search: {lit.Status}");
            md.AppendLine($"Cases {lit.Cases.ToString("N0", inv)}, shown {lit.CasesShown.ToString("N0", inv)} (the rest set aside by attribution); exact match {(lit.ExactMatch is { } e ? (e ? "on" : "off") : "default")}.");
            md.AppendLine();
            md.AppendLine("| # | Keyword | Source | Reason |");
            md.AppendLine("|---|---|---|---|");
            var i = 0;
            foreach (var k in lit.Keywords) md.AppendLine($"| {++i} | {Cell(k.Value)} | {k.Source} | {Cell(k.Reason)} |");
            md.AppendLine();
        }
        else md.AppendLine("## Litigation search: not run\n");

        md.AppendLine("## Dossier");
        if (r.DossierDownloads.Count == 0) md.AppendLine("- Not downloaded.");
        foreach (var d in r.DossierDownloads)
            md.AppendLine($"- Downloaded {Ist.Format(d.AtUtc)} by {d.By}: {d.Variant}, {(d.Bytes is { } b ? $"{b / 1024.0:N0} KB" : "size unknown")}, {(d.Pages is { } pg ? $"{pg} pages" : "pages unknown")}");
        md.AppendLine("- [ ] Opened and reviewed. Notes:");
        return md.ToString();
    }

    private static string Cell(string? text) => (text ?? "").Replace("|", "\\|").ReplaceLineEndings(" ");
}
