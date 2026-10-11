using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed partial class PreLoginLitigationTargetService(
    AppDbContext db,
    BprLitigationClient client,
    IOptions<BprLitigationOptions> options,
    PreLoginReportQueue parentQueue,
    PreLoginLitigationTargetQueue targetQueue,
    ILogger<PreLoginLitigationTargetService> logger)
{
    private const int LeaseSeconds = 120;
    private static readonly string LeaseOwnerId = $"PreLoginTargetWorker-{Guid.NewGuid():N}";

    public IQueryable<PreLoginLitigationTarget> LeaseGuarded(long targetId, Guid leaseToken) =>
        db.PreLoginLitigationTargets.Where(t =>
            t.PreLoginLitigationTargetId == targetId &&
            t.LeaseToken == leaseToken &&
            t.LeaseExpiresUtc != null &&
            t.LeaseExpiresUtc > DateTime.UtcNow);

    public async Task ProcessAsync(long targetId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseToken = Guid.NewGuid();

        // 1. Atomic claim directly from Pending or Polling -> Authenticating (lease expiry check prevents stealing active leases)
        var claimed = await db.PreLoginLitigationTargets
            .Where(t => t.PreLoginLitigationTargetId == targetId
                && (t.Status == "Pending" || t.Status == "Polling")
                && (t.LeaseExpiresUtc == null || t.LeaseExpiresUtc <= now)
                && (t.NextAttemptUtc == null || t.NextAttemptUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Authenticating")
                .SetProperty(t => t.AttemptCount, t => t.AttemptCount + 1)
                .SetProperty(t => t.LeaseOwner, LeaseOwnerId)
                .SetProperty(t => t.LeaseToken, leaseToken)
                .SetProperty(t => t.LeaseExpiresUtc, now.AddSeconds(LeaseSeconds))
                .SetProperty(t => t.StartedUtc, t => t.StartedUtc ?? now)
                .SetProperty(t => t.StatusMessage, "Authenticating with BPR.")
                .SetProperty(t => t.ProgressPercent, 20), ct);

        if (claimed == 0)
        {
            var current = await db.PreLoginLitigationTargets
                .Where(t => t.PreLoginLitigationTargetId == targetId)
                .Select(t => new { t.Status, t.NextAttemptUtc })
                .FirstOrDefaultAsync(ct);

            if (current is { NextAttemptUtc: { } next } && (current.Status == "Pending" || current.Status == "Polling") && next > now)
            {
                ScheduleRetry(targetId, next);
            }
            return;
        }

        var target = await db.PreLoginLitigationTargets.AsNoTracking().FirstAsync(t => t.PreLoginLitigationTargetId == targetId, ct);

        try
        {
            // Ambiguity check: crash during registration before vendor JobId was committed
            if (target.VendorJobId is null && target.RegistrationAttemptedUtc is not null)
            {
                await MarkAmbiguousAsync(targetId, leaseToken, target.PreLoginReportJobId,
                    "A previous registration attempt never confirmed success or failure with BPR. Manual review required.", ct);
                return;
            }

            // Strict lease fence check before network call
            if (!await LeaseGuarded(targetId, leaseToken).AnyAsync(ct))
                throw new PreLoginLitigationTargetLeaseLostException(targetId);

            // Resumed search ALWAYS authenticates before polling
            var token = await client.AuthenticateAsync(ct);

            // Strict lease fence check
            if (!await LeaseGuarded(targetId, leaseToken).AnyAsync(ct))
                throw new PreLoginLitigationTargetLeaseLostException(targetId);

            var vendorJobId = target.VendorJobId;
            if (vendorJobId is null)
            {
                // Registering Intent write: fenced by lease
                var claimedRegistering = await LeaseGuarded(targetId, leaseToken)
                    .Where(t => t.Status == "Authenticating")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Status, "Registering")
                        .SetProperty(t => t.RegistrationAttemptedUtc, DateTime.UtcNow)
                        .SetProperty(t => t.StatusMessage, "Registering litigation search with BPR.")
                        .SetProperty(t => t.ProgressPercent, 40)
                        .SetProperty(t => t.LeaseExpiresUtc, DateTime.UtcNow.AddSeconds(LeaseSeconds)), ct);

                if (claimedRegistering == 0)
                    throw new PreLoginLitigationTargetLeaseLostException(targetId);

                var keywords = JsonSerializer.Deserialize<List<string>>(target.KeywordsJson) ?? [target.TargetName];
                var customerAppId = $"SBI-{target.PreLoginReportJobId}-{target.TargetIndex}";

                try
                {
                    vendorJobId = await client.RegisterJobAsync(
                        token, keywords, target.EntityType, customerAppId, ct, target.ExactMatch);
                }
                catch (Exception ex)
                {
                    // Network or vendor error during registration call -> mark Ambiguous
                    await MarkAmbiguousAsync(targetId, leaseToken, target.PreLoginReportJobId,
                        $"Registration call failed: {ex.Message}. Check BPR directly before retrying.", ct);
                    return;
                }

                var registeredUtc = DateTime.UtcNow;
                var pollTimeoutMinutes = options.Value.PollTimeoutMinutes > 0 ? options.Value.PollTimeoutMinutes : 30;
                var deadline = registeredUtc.AddMinutes(pollTimeoutMinutes);

                // Commit VendorJobId and transition to Polling
                var registered = await LeaseGuarded(targetId, leaseToken)
                    .Where(t => t.Status == "Registering")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.VendorJobId, vendorJobId)
                        .SetProperty(t => t.RegisteredUtc, registeredUtc)
                        .SetProperty(t => t.PollingDeadlineUtc, deadline)
                        .SetProperty(t => t.Status, "Polling")
                        .SetProperty(t => t.StatusMessage, "Registered. Polling BPR for report completion.")
                        .SetProperty(t => t.ProgressPercent, 50)
                        .SetProperty(t => t.LeaseExpiresUtc, DateTime.UtcNow.AddSeconds(LeaseSeconds)), ct);

                if (registered == 0)
                    throw new PreLoginLitigationTargetLeaseLostException(targetId);
            }

            await PollUntilCompleteAsync(targetId, leaseToken, target.PreLoginReportJobId, vendorJobId!, token, target.IsUserSuppliedVendorJobId, ct);
        }
        catch (PreLoginLitigationTargetLeaseLostException)
        {
            logger.LogWarning("Target {TargetId} lease lost during processing.", targetId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error processing target {TargetId}.", targetId);
            await FailOrRetryTargetAsync(targetId, leaseToken, target.PreLoginReportJobId, ex.Message, ct);
        }
    }

    private async Task PollUntilCompleteAsync(
        long targetId, Guid leaseToken, long parentJobId, string vendorJobId, string token, bool isUserSupplied, CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds > 0 ? options.Value.PollIntervalSeconds : 10);

        while (!ct.IsCancellationRequested)
        {
            var target = await db.PreLoginLitigationTargets
                .Where(t => t.PreLoginLitigationTargetId == targetId)
                .Select(t => new { t.PollingDeadlineUtc })
                .FirstOrDefaultAsync(ct);

            if (target?.PollingDeadlineUtc is { } deadline && DateTime.UtcNow > deadline)
            {
                await MarkFailedAsync(targetId, leaseToken, parentJobId, "Polling timed out against configured deadline.", ct);
                return;
            }

            // Extend lease before poll
            var extended = await LeaseGuarded(targetId, leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, "Polling")
                    .SetProperty(t => t.LeaseExpiresUtc, DateTime.UtcNow.AddSeconds(LeaseSeconds))
                    .SetProperty(t => t.StatusMessage, $"Polling BPR report (job {vendorJobId}).")
                    .SetProperty(t => t.ProgressPercent, 60), ct);

            if (extended == 0)
                throw new PreLoginLitigationTargetLeaseLostException(targetId);

            var pollResult = await client.GetReportAsync(token, vendorJobId, ct);

            if (pollResult.Status == BprReportPollStatus.Pending)
            {
                await Task.Delay(pollInterval, ct);
                continue;
            }

            if (pollResult.Status == BprReportPollStatus.Failed)
            {
                await MarkFailedAsync(targetId, leaseToken, parentJobId, pollResult.Message ?? "BPR polling returned failed status.", ct);
                return;
            }

            if (pollResult.Status == BprReportPollStatus.Completed)
            {
                var bytes = pollResult.Bytes!;
                var format = pollResult.Format.ToString();

                if (pollResult.Format == BprReportFormat.Json)
                {
                    var json = System.Text.Encoding.UTF8.GetString(bytes);

                    // 1. Recursive Structural Envelope Validation (§4)
                    var validation = BprLitigationReportParser.ValidateEnvelope(json, vendorJobId);
                    if (validation != BprEnvelopeValidationResult.ValidWithCases && validation != BprEnvelopeValidationResult.NoLitigation)
                    {
                        await MarkFailedAsync(targetId, leaseToken, parentJobId, $"BPR envelope validation failed: {validation}.", ct);
                        return;
                    }

                    // 2. Target Corroboration for User-Supplied IDs (§6)
                    if (isUserSupplied)
                    {
                        var targetEntity = await db.PreLoginLitigationTargets.AsNoTracking().FirstAsync(t => t.PreLoginLitigationTargetId == targetId, ct);
                        var parsedReport = BprLitigationReportParser.Parse(json);
                        var targetKeywords = JsonSerializer.Deserialize<List<string>>(targetEntity.KeywordsJson) ?? [targetEntity.TargetName];

                        var hasOverlap = parsedReport.Request.Keywords.Count > 0 && parsedReport.Request.Keywords.Any(k =>
                            targetKeywords.Any(tk => AreCorroboratingKeywords(k, tk)));

                        if (!hasOverlap)
                        {
                            await MarkAmbiguousAsync(targetId, leaseToken, parentJobId,
                                $"User-supplied vendor job ID {vendorJobId} could not be corroborated: BPR report does not contain matching keywords for '{targetEntity.TargetName}'.", ct);
                            return;
                        }
                    }

                    var noLitigation = validation == BprEnvelopeValidationResult.NoLitigation;
                    await MarkCompletedAsync(targetId, leaseToken, parentJobId, bytes, format, noLitigation, ct);
                    return;
                }
                else
                {
                    // XLSX or binary formats
                    if (isUserSupplied)
                    {
                        var targetEntity = await db.PreLoginLitigationTargets.AsNoTracking().FirstAsync(t => t.PreLoginLitigationTargetId == targetId, ct);
                        var targetKeywords = JsonSerializer.Deserialize<List<string>>(targetEntity.KeywordsJson) ?? [targetEntity.TargetName];

                        List<LegalCaseRecord> parsedCases;
                        try
                        {
                            using var ms = new MemoryStream(bytes);
                            parsedCases = LegalCaseFileParser.Parse(ms, "report.xlsx").ToList();
                        }
                        catch
                        {
                            parsedCases = [];
                        }

                        var hasOverlap = parsedCases.Count > 0 && parsedCases.Any(c =>
                            targetKeywords.Any(tk =>
                                PartyCorroboratesTarget(c.Petitioner, tk)
                                || PartyCorroboratesTarget(c.Respondent, tk)
                                || PartyCorroboratesTarget(c.CaseDetails, tk)));

                        if (!hasOverlap)
                        {
                            await MarkAmbiguousAsync(targetId, leaseToken, parentJobId,
                                $"User-supplied vendor job ID {vendorJobId} could not be corroborated: XLSX report does not contain matching parties or keywords for '{targetEntity.TargetName}'.", ct);
                            return;
                        }
                    }

                    await MarkCompletedAsync(targetId, leaseToken, parentJobId, bytes, format, noLitigation: false, ct);
                    return;
                }
            }
        }
    }

    private static string NormalizeCorroborationName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var norm = Whitespace().Replace(text.Trim(), " ").ToUpperInvariant();
        norm = Regex.Replace(norm, @"\b(M/S|MS)\b\.?", " ");
        norm = Regex.Replace(norm, @"\bLIMITED\b", "LTD");
        norm = Regex.Replace(norm, @"\bPRIVATE\b", "PVT");
        norm = Regex.Replace(norm, @"\bCORPORATION\b", "CORP");
        norm = Regex.Replace(norm, @"\bCOMPANY\b", "CO");
        norm = Regex.Replace(norm, @"[^A-Z0-9 ]", " ");
        return Whitespace().Replace(norm.Trim(), " ");
    }

    private static bool AreCorroboratingKeywords(string reportKeyword, string targetKeyword)
    {
        var nrk = NormalizeCorroborationName(reportKeyword);
        var ntk = NormalizeCorroborationName(targetKeyword);
        if (string.IsNullOrEmpty(nrk) || string.IsNullOrEmpty(ntk)) return false;
        return string.Equals(nrk, ntk, StringComparison.Ordinal);
    }

    private static bool PartyCorroboratesTarget(string? partyText, string targetKeyword)
    {
        if (string.IsNullOrWhiteSpace(partyText)) return false;
        var ntk = NormalizeCorroborationName(targetKeyword);
        if (string.IsNullOrEmpty(ntk)) return false;

        var tokens = Regex.Split(partyText, @"(?i)[,;\r\n]+|\bAND\s+ORS\b|\b&\s*ORS\b|\bET\s+AL\b");
        foreach (var token in tokens)
        {
            var nToken = NormalizeCorroborationName(token);
            if (string.Equals(nToken, ntk, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private async Task MarkCompletedAsync(
        long targetId, Guid leaseToken, long parentJobId, byte[] reportBytes, string reportFormat, bool noLitigation, CancellationToken ct)
    {
        var completed = await LeaseGuarded(targetId, leaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Completed")
                .SetProperty(t => t.RawReportBytes, reportBytes)
                .SetProperty(t => t.RawReportByteLength, (long)reportBytes.Length)
                .SetProperty(t => t.ReportFormat, reportFormat)
                .SetProperty(t => t.NoLitigationFound, noLitigation)
                .SetProperty(t => t.CompletedUtc, DateTime.UtcNow)
                .SetProperty(t => t.StatusMessage, noLitigation ? "Completed (No litigation found)." : "Completed with records.")
                .SetProperty(t => t.ProgressPercent, 100)
                .SetProperty(t => t.LeaseToken, (Guid?)null)
                .SetProperty(t => t.LeaseExpiresUtc, (DateTime?)null), ct);

        if (completed > 0)
        {
            parentQueue.Enqueue(parentJobId);
        }
    }

    private async Task MarkFailedAsync(long targetId, Guid leaseToken, long parentJobId, string failureReason, CancellationToken ct)
    {
        var failed = await LeaseGuarded(targetId, leaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Failed")
                .SetProperty(t => t.FailureReason, failureReason)
                .SetProperty(t => t.StatusMessage, failureReason)
                .SetProperty(t => t.CompletedUtc, DateTime.UtcNow)
                .SetProperty(t => t.ProgressPercent, 100)
                .SetProperty(t => t.LeaseToken, (Guid?)null)
                .SetProperty(t => t.LeaseExpiresUtc, (DateTime?)null), ct);

        if (failed > 0)
        {
            parentQueue.Enqueue(parentJobId);
        }
    }

    private async Task MarkAmbiguousAsync(long targetId, Guid leaseToken, long parentJobId, string reason, CancellationToken ct)
    {
        var affected = await LeaseGuarded(targetId, leaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "AmbiguousRegistration")
                .SetProperty(t => t.FailureReason, reason)
                .SetProperty(t => t.StatusMessage, "Ambiguous search registration. Action required.")
                .SetProperty(t => t.ProgressPercent, 50)
                .SetProperty(t => t.LeaseToken, (Guid?)null)
                .SetProperty(t => t.LeaseExpiresUtc, (DateTime?)null), ct);

        if (affected > 0)
        {
            parentQueue.Enqueue(parentJobId);
        }
    }

    private async Task FailOrRetryTargetAsync(long targetId, Guid leaseToken, long parentJobId, string message, CancellationToken ct)
    {
        var target = await db.PreLoginLitigationTargets
            .Where(t => t.PreLoginLitigationTargetId == targetId)
            .Select(t => new { t.AttemptCount })
            .FirstOrDefaultAsync(ct);

        if (target is { AttemptCount: < 3 })
        {
            var next = DateTime.UtcNow.AddSeconds(20 * target.AttemptCount);
            await LeaseGuarded(targetId, leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, "Pending")
                    .SetProperty(t => t.NextAttemptUtc, next)
                    .SetProperty(t => t.StatusMessage, $"Transient failure: {message}. Retrying shortly.")
                    .SetProperty(t => t.LeaseToken, (Guid?)null)
                    .SetProperty(t => t.LeaseExpiresUtc, (DateTime?)null), ct);

            ScheduleRetry(targetId, next);
        }
        else
        {
            await MarkFailedAsync(targetId, leaseToken, parentJobId, message, ct);
        }
    }

    private void ScheduleRetry(long targetId, DateTime nextUtc)
    {
        var delay = nextUtc - DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }
            targetQueue.Enqueue(targetId);
        });
    }

    public async Task RecoverStaleWorkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.PreLoginLitigationTargets
            .Where(t => (t.Status == "Authenticating" || t.Status == "Registering" || t.Status == "Polling")
                && (t.LeaseExpiresUtc == null || t.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Pending")
                .SetProperty(t => t.StatusMessage, "Resuming after an application restart.")
                .SetProperty(t => t.LeaseToken, (Guid?)null)
                .SetProperty(t => t.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(t => t.LeaseOwner, (string?)null), ct);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

public sealed class PreLoginLitigationTargetLeaseLostException(long targetId)
    : Exception($"Lease for PreLoginLitigationTarget {targetId} was lost or expired.");

