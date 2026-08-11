using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Application.Retention;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Workers;

public sealed class QueueWorkerService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    IOptions<BackupOptions> backupOptions,
    ILogger<QueueWorkerService> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;
    private readonly BackupOptions _backupOptions = backupOptions.Value;

    private const int MinDelaySeconds = 15;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue worker started.");

        // Everything runs inside the loop: a pass that throws (database not
        // reachable, schema not migrated yet) must be caught and retried, not
        // allowed to escape. An exception out of ExecuteAsync stops the whole
        // host, which for worker mode means ingestion silently stops.
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseStaleRunningSyncsAsync(stoppingToken);
                await RunScheduledSyncPassAsync(stoppingToken);
                await RunAlertPassIfDueAsync(stoppingToken);
                await RunDigestPassIfDueAsync(stoppingToken);
                await RunRetentionPassIfDueAsync(stoppingToken);
                await RunDnsRefreshPassIfDueAsync(stoppingToken);
                await RunMtaStsPassIfDueAsync(stoppingToken);
                await RunBackupOffloadPassIfDueAsync(stoppingToken);
                await RunMailboxRetentionPassIfDueAsync(stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                logger.LogError(ex, "Worker scheduler pass failed ({Failures} consecutive)", consecutiveFailures);
            }

            try
            {
                await Task.Delay(NextDelay(consecutiveFailures, _options.ScheduleIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Queue worker stopping.");
    }

    /// <summary>
    /// Delay before the next pass. Healthy passes wait the configured interval;
    /// after a failure it retries far sooner and backs off exponentially
    /// (5s, 10s, 20s, …) up to that interval — so a worker that starts before
    /// the database is ready recovers in seconds instead of idling for the full
    /// production hour.
    /// </summary>
    public static TimeSpan NextDelay(int consecutiveFailures, int intervalSeconds)
    {
        var normalSeconds = Math.Max(MinDelaySeconds, intervalSeconds);
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.FromSeconds(normalSeconds);
        }

        var backoffSeconds = 5L << Math.Min(consecutiveFailures - 1, 10);
        return TimeSpan.FromSeconds(Math.Min(backoffSeconds, normalSeconds));
    }

    private DateTime? _lastAlertRunUtc;

    /// <summary>
    /// Evaluates alert rules on their own cadence (<c>Alerts:IntervalMinutes</c>).
    /// Separate from the sync interval because reports arrive daily — evaluating
    /// far more often than that only risks duplicate work, and the cooldown in the
    /// evaluation service is what actually prevents repeat notifications.
    /// </summary>
    private async Task RunAlertPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var alertOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<AlertOptions>>().Value;

        if (!alertOptions.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, alertOptions.IntervalMinutes));
        if (_lastAlertRunUtc is { } last && DateTime.UtcNow - last < interval)
        {
            return;
        }

        var alerts = scope.ServiceProvider.GetRequiredService<IAlertEvaluationService>();
        await alerts.EvaluateAsync(ct);

        // Only on success, so a failure retries next pass.
        _lastAlertRunUtc = DateTime.UtcNow;
    }

    private DateTime? _lastDnsRefreshUtc;

    /// <summary>
    /// Keeps each domain's cached DMARC policy fresh so list views can render the real
    /// policy from one query. Detail-page views correct individual domains as a side
    /// effect of the lookup they already make; this pass is what covers the domains
    /// nobody opens — including the ones that stopped reporting, which are exactly the
    /// ones whose policy would otherwise be silently wrong.
    /// </summary>
    private async Task RunDnsRefreshPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dnsOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<DnsOptions>>().Value;

        if (!dnsOptions.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, dnsOptions.RefreshIntervalHours));
        if (_lastDnsRefreshUtc is { } last && DateTime.UtcNow - last < interval)
        {
            return;
        }

        var cache = scope.ServiceProvider.GetRequiredService<IDnsPolicyCache>();
        await cache.RefreshAllAsync(ct);

        // Only on success, so a failure retries next pass.
        _lastDnsRefreshUtc = DateTime.UtcNow;
    }

    private DateTime? _lastDigestCheckUtc;

    /// <summary>
    /// Checks a few times a day whether the monthly digest is due. The real
    /// guard against duplicates is the unique (client, period) row the digest
    /// service writes, so a restart or an extra check is harmless.
    /// </summary>
    private async Task RunDigestPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DigestOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, options.CheckIntervalHours));
        if (_lastDigestCheckUtc is { } last && DateTime.UtcNow - last < interval)
        {
            return;
        }

        var digest = scope.ServiceProvider.GetRequiredService<IDigestService>();
        await digest.SendDueAsync(ct);
        _lastDigestCheckUtc = DateTime.UtcNow;
    }

    private DateTime? _lastRetentionRunUtc;

    /// <summary>
    /// Enforces per-client retention. Runs on its own slow cadence
    /// (<c>Worker:RetentionIntervalHours</c>, daily by default) rather than every
    /// sync pass — retention is measured in months, so there is nothing to gain
    /// from checking hourly. The timestamp is in-memory, so a restart simply runs
    /// it once more; purging is idempotent.
    /// </summary>
    private async Task RunRetentionPassIfDueAsync(CancellationToken ct)
    {
        if (!_options.RetentionEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.RetentionIntervalHours));
        if (_lastRetentionRunUtc is { } last && DateTime.UtcNow - last < interval)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<IRetentionPurgeService>();
        await retention.PurgeAsync(dryRun: false, _options.RetentionBatchSize, ct);

        // Only mark it done on success, so a failure retries on the next pass
        // instead of waiting out the whole interval.
        _lastRetentionRunUtc = DateTime.UtcNow;
    }

    private DateTime? _lastMtaStsRunUtc;

    /// <summary>
    /// Keeps each domain's MTA-STS state fresh: the `_mta-sts` TXT record, the
    /// policy fetch, and the MX cross-check. The alert pass reads what this
    /// writes, so this is also what makes an id change or a broken policy host
    /// visible without anyone opening the domain.
    /// <para>
    /// Swallows its own exceptions, like the backup pass below and for the same
    /// structural reason: it talks to third parties (every client's DNS and
    /// policy host), which makes it likelier to fail than the passes after it —
    /// and per-domain failures are already absorbed inside the refresh, so an
    /// exception here means the pass itself broke, not a domain.
    /// </para>
    /// </summary>
    private async Task RunMtaStsPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var mtaStsOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<MtaStsOptions>>().Value;

        if (!mtaStsOptions.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, mtaStsOptions.CheckIntervalHours));
        if (_lastMtaStsRunUtc is { } last && DateTime.UtcNow - last < interval)
        {
            return;
        }

        try
        {
            var cache = scope.ServiceProvider.GetRequiredService<IMtaStsStateCache>();
            await cache.RefreshAllAsync(ct);
            _lastMtaStsRunUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MTA-STS check pass failed; ingestion is unaffected");
            _lastMtaStsRunUtc = DateTime.UtcNow;
        }
    }

    private DateTime? _lastBackupOffloadUtc;

    /// <summary>
    /// Ships the configuration snapshot and any new history rows to object storage.
    /// <para>
    /// Runs last, and swallows its own exceptions, for a structural reason: all six passes
    /// share one try/catch in the loop above, so a pass that throws skips every pass after
    /// it. Backup depends on a third party being reachable — a bucket, over the network —
    /// which makes it the pass most likely to fail, and the least acceptable one to let
    /// stop ingestion. Its own failures are recorded in <c>backup_stream_state</c> and
    /// surfaced in the console instead.
    /// </para>
    /// <para>
    /// Interval resolution is <c>Worker:ScheduleIntervalSeconds</c>, like every other gate
    /// here: with the shipped hourly schedule, a 30-minute backup interval means roughly
    /// hourly. Not floored to an hour, though, because a shortened schedule interval should
    /// actually deliver the configured cadence.
    /// </para>
    /// </summary>
    private async Task RunBackupOffloadPassIfDueAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _backupOptions.IntervalMinutes));
        if (_lastBackupOffloadUtc is { } last && DateTime.UtcNow - last < interval)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var offload = scope.ServiceProvider.GetRequiredService<IBackupOffloadService>();
            var result = await offload.RunAsync(ct);

            // A pass that did nothing because no bucket is configured must not start the
            // clock, or enabling offload later would wait out a whole interval.
            if (result.Ran)
            {
                _lastBackupOffloadUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup offload pass failed; ingestion is unaffected");
            _lastBackupOffloadUtc = DateTime.UtcNow;
        }
    }

    private DateTime? _lastMailboxRetentionUtc;

    /// <summary>
    /// Deletes report mail that has aged past the retention window, so the mailbox stops
    /// being an unbounded second copy of data the database has already purged.
    /// <para>
    /// Runs last and swallows its own exceptions, like the offload pass above and for the
    /// same structural reason. Every source is opt-in, so on a default install this pass
    /// connects to nothing at all — the planner suspends each source before any mailbox is
    /// opened.
    /// </para>
    /// </summary>
    private async Task RunMailboxRetentionPassIfDueAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _options.MailboxRetentionIntervalHours));
        if (_lastMailboxRetentionUtc is { } last && DateTime.UtcNow - last < interval)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var retention = scope.ServiceProvider.GetRequiredService<IMailboxRetentionService>();
            await retention.RunAsync(dryRun: false, ct);

            _lastMailboxRetentionUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mailbox retention pass failed; ingestion is unaffected");
            _lastMailboxRetentionUtc = DateTime.UtcNow;
        }
    }

    private async Task RunScheduledSyncPassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();

        var activeReportSources = await db.ReportSources
            .AsNoTracking()
            .Where(x => x.IsActive && x.Protocol == "imap")
            .Select(x => x.Id)
            .ToListAsync(ct);

        if (activeReportSources.Count == 0)
        {
            logger.LogDebug("No active report sources with protocol=imap found for scheduled pass");
            return;
        }

        logger.LogInformation("Scheduled sync pass for {Count} report sources", activeReportSources.Count);

        foreach (var reportSourceId in activeReportSources)
        {
            try
            {
                var result = await ExecuteWithRetryAsync(reportSourceId, ct);

                if (!result.IsSuccess)
                {
                    logger.LogInformation(
                        "Scheduled sync failed to start for report source {ReportSourceId}: {Error}",
                        reportSourceId,
                        result.Error);
                    continue;
                }

                var value = result.Value!;
                if (!value.Success)
                {
                    logger.LogWarning(
                        "Scheduled sync failed for report source {ReportSourceId}: {Error}",
                        reportSourceId,
                        value.Error);
                    continue;
                }

                logger.LogInformation(
                    "Scheduled sync completed for report source {ReportSourceId}. Messages={MessagesScanned}, Attachments={AttachmentsProcessed}, Inserted={ReportsInserted}, Duplicates={ReportsSkippedAsDuplicate}, ParseFailures={ParseFailures}",
                    reportSourceId,
                    value.MessagesScanned,
                    value.AttachmentsProcessed,
                    value.ReportsInserted,
                    value.ReportsSkippedAsDuplicate,
                    value.ParseFailures);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogDebug("Scheduled sync cancelled for report source {ReportSourceId}", reportSourceId);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled sync crashed for report source {ReportSourceId}", reportSourceId);
            }
        }
    }

    private async Task CloseStaleRunningSyncsAsync(CancellationToken ct)
    {
        var staleRunTimeoutMinutes = Math.Max(5, _options.StaleRunTimeoutMinutes);
        var staleBeforeUtc = DateTime.UtcNow.AddMinutes(-staleRunTimeoutMinutes);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();

        var staleRuns = await db.MailboxSyncRuns
            .Where(x => x.Status == "running" && x.StartedAtUtc < staleBeforeUtc)
            .ToListAsync(ct);

        if (staleRuns.Count == 0)
        {
            return;
        }

        foreach (var staleRun in staleRuns)
        {
            staleRun.Status = "failed";
            staleRun.FinishedAtUtc = DateTime.UtcNow;
            staleRun.Error = string.IsNullOrWhiteSpace(staleRun.Error)
                ? $"auto-closed stale running sync after {staleRunTimeoutMinutes} minutes"
                : staleRun.Error;
        }

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Auto-closed {Count} stale running mailbox sync runs older than {TimeoutMinutes} minutes",
            staleRuns.Count,
            staleRunTimeoutMinutes);
    }

    private async Task<ServiceResult<MailboxSyncResult>> ExecuteWithRetryAsync(Guid reportSourceId, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _options.MaxRetryAttempts);
        var baseDelay = Math.Max(1, _options.RetryBaseDelaySeconds);
        ServiceResult<MailboxSyncResult>? lastResult = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var syncScope = scopeFactory.CreateScope();
            var syncService = syncScope.ServiceProvider.GetRequiredService<IMailboxSyncService>();
            var result = await syncService.SyncReportSourceAsync(reportSourceId, "scheduled", ct);
            lastResult = result;

            if (!result.IsSuccess)
            {
                return result;
            }

            if (result.Value?.Success == true)
            {
                return result;
            }

            if (attempt == maxAttempts)
            {
                return result;
            }

            var delay = TimeSpan.FromSeconds(baseDelay * Math.Pow(2, attempt - 1));
            logger.LogWarning(
                "Scheduled sync attempt {Attempt}/{MaxAttempts} failed for report source {ReportSourceId}. Retrying in {DelaySeconds}s",
                attempt,
                maxAttempts,
                reportSourceId,
                (int)delay.TotalSeconds);

            await Task.Delay(delay, ct);
        }

        return lastResult ?? ServiceResult<MailboxSyncResult>.Failure("retry pipeline returned no result", 500);
    }
}
