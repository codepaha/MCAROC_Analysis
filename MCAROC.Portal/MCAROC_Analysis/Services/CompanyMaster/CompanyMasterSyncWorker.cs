using System;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.CompanyMaster;

public sealed class CompanyMasterSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CompanyMasterSyncWorker> _logger;

    public CompanyMasterSyncWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<CompanyMasterSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool enabled = _configuration.GetValue<bool>("CompanyMasterSync:Enabled", false);
        if (!enabled)
        {
            _logger.LogInformation("CompanyMasterSyncWorker is disabled in configuration (Manual-upload-first operational mode).");
            return;
        }

        int checkIntervalHours = _configuration.GetValue<int>("CompanyMasterSync:CadenceHours", 24);
        _logger.LogInformation("CompanyMasterSyncWorker started with check interval of {Hours} hours.", checkIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScheduledSyncCheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error occurred during scheduled Company Master sync check.");
            }

            await Task.Delay(TimeSpan.FromHours(checkIntervalHours), stoppingToken);
        }
    }

    public async Task RunScheduledSyncCheckAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var deltaService = scope.ServiceProvider.GetRequiredService<ICompanyMasterDeltaService>();
        var lockLease = scope.ServiceProvider.GetRequiredService<ISyncLockLease>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation("Running scheduled probe for Company Master updates...");
        var portalDate = await deltaService.ProbePortalSnapshotDateAsync(cancellationToken);

        if (portalDate.HasValue)
        {
            var lastJob = await db.CompanyMasterSyncJobs
                .Where(j => j.Status == CompanyMasterSyncJobStatus.Completed)
                .OrderByDescending(j => j.PublishedDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (lastJob?.PublishedDate == portalDate.Value)
            {
                _logger.LogInformation("Portal snapshot date {Date} matches latest completed run (Job {JobId}). Skipping download.",
                    portalDate.Value, lastJob.JobId);
                return;
            }
        }

        // Attempt exclusive lock
        var lockResult = await lockLease.TryAcquireAsync("CompanyMasterSyncExclusiveLock", TimeSpan.Zero, cancellationToken);
        if (!lockResult.Succeeded)
        {
            _logger.LogInformation("Sync lock could not be acquired ({Reason}). Another instance is running.", lockResult.FailureReason);
            return;
        }

        try
        {
            var job = await deltaService.CreateJobAsync(CompanyMasterSyncTriggerType.Scheduled, null, cancellationToken);
            _logger.LogInformation("Created scheduled sync job {JobId} with fencing token {Token}", job.JobId, job.FencingToken);

            // Gated by automation consent
            bool consent = _configuration.GetValue<bool>("CompanyMasterSync:AutomationConsent", false);
            if (!consent)
            {
                _logger.LogInformation("Headless automation consent is not enabled. Job {JobId} awaiting manual archive upload.", job.JobId);
                await deltaService.UpdateJobStatusAsync(job.JobId, CompanyMasterSyncJobStatus.Pending, "Automation consent disabled; manual upload required.", cancellationToken);
                return;
            }

            _logger.LogInformation("Automation consent granted. Executing automated sync for Job {JobId}...", job.JobId);
            await deltaService.ExecuteAutomatedSyncAsync(job.JobId, job.FencingToken, cancellationToken: cancellationToken);
            _logger.LogInformation("Automated sync completed successfully for Job {JobId}.", job.JobId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error occurred during scheduled Company Master sync execution.");
        }
        finally
        {
            await lockLease.ReleaseAsync(CancellationToken.None);
        }
    }
}
