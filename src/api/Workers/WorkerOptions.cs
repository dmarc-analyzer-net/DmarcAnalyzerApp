namespace DmarcAnalyzer.Api.Workers;

public sealed class WorkerOptions
{
    public int ScheduleIntervalSeconds { get; set; } = 300;
    public int MaxMessagesPerSync { get; set; } = 10;
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryBaseDelaySeconds { get; set; } = 2;
    public int StaleRunTimeoutMinutes { get; set; } = 30;
    public int SyncRunTimeoutMinutes { get; set; } = 10;

    /// <summary>Master switch for retention purging.</summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>How often the retention purge runs. Daily is plenty — retention is measured in months.</summary>
    public int RetentionIntervalHours { get; set; } = 24;

    /// <summary>Reports deleted per transaction, so a large backlog doesn't hold locks across the table.</summary>
    public int RetentionBatchSize { get; set; } = 500;
}
