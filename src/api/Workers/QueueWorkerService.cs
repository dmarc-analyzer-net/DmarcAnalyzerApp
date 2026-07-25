using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Ingestion;
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
    ILogger<QueueWorkerService> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

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

    private async Task RunScheduledSyncPassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();

        var activeMailboxSources = await db.MailboxSources
            .AsNoTracking()
            .Where(x => x.IsActive && x.Protocol == "imap")
            .Select(x => x.Id)
            .ToListAsync(ct);

        if (activeMailboxSources.Count == 0)
        {
            logger.LogDebug("No active IMAP mailbox sources found for scheduled pass");
            return;
        }

        logger.LogInformation("Scheduled sync pass for {Count} mailbox sources", activeMailboxSources.Count);

        foreach (var mailboxSourceId in activeMailboxSources)
        {
            try
            {
                var result = await ExecuteWithRetryAsync(mailboxSourceId, ct);

                if (!result.IsSuccess)
                {
                    logger.LogInformation(
                        "Scheduled sync failed to start for mailbox source {MailboxSourceId}: {Error}",
                        mailboxSourceId,
                        result.Error);
                    continue;
                }

                var value = result.Value!;
                if (!value.Success)
                {
                    logger.LogWarning(
                        "Scheduled sync failed for mailbox source {MailboxSourceId}: {Error}",
                        mailboxSourceId,
                        value.Error);
                    continue;
                }

                logger.LogInformation(
                    "Scheduled sync completed for mailbox source {MailboxSourceId}. Messages={MessagesScanned}, Attachments={AttachmentsProcessed}, Inserted={ReportsInserted}, Duplicates={ReportsSkippedAsDuplicate}, ParseFailures={ParseFailures}",
                    mailboxSourceId,
                    value.MessagesScanned,
                    value.AttachmentsProcessed,
                    value.ReportsInserted,
                    value.ReportsSkippedAsDuplicate,
                    value.ParseFailures);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogDebug("Scheduled sync cancelled for mailbox source {MailboxSourceId}", mailboxSourceId);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled sync crashed for mailbox source {MailboxSourceId}", mailboxSourceId);
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

    private async Task<ServiceResult<MailboxSyncResult>> ExecuteWithRetryAsync(Guid mailboxSourceId, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _options.MaxRetryAttempts);
        var baseDelay = Math.Max(1, _options.RetryBaseDelaySeconds);
        ServiceResult<MailboxSyncResult>? lastResult = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var syncScope = scopeFactory.CreateScope();
            var syncService = syncScope.ServiceProvider.GetRequiredService<IMailboxSyncService>();
            var result = await syncService.SyncMailboxSourceAsync(mailboxSourceId, "scheduled", ct);
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
                "Scheduled sync attempt {Attempt}/{MaxAttempts} failed for mailbox source {MailboxSourceId}. Retrying in {DelaySeconds}s",
                attempt,
                maxAttempts,
                mailboxSourceId,
                (int)delay.TotalSeconds);

            await Task.Delay(delay, ct);
        }

        return lastResult ?? ServiceResult<MailboxSyncResult>.Failure("retry pipeline returned no result", 500);
    }
}
