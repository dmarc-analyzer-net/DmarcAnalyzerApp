using DmarcAnalyzer.Api.Application.Ingestion;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue worker started.");

        await CloseStaleRunningSyncsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseStaleRunningSyncsAsync(stoppingToken);
                await RunScheduledSyncPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker scheduler pass failed");
            }

            var delaySeconds = Math.Max(15, _options.ScheduleIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }

        logger.LogInformation("Queue worker stopping.");
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
