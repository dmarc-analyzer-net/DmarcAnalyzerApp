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
    /// <summary>
    /// Refuse to start when another worker already holds the ingestion lock.
    /// <para>
    /// On by default. Two ingestion loops duplicate every sync pass and can send
    /// duplicate alert and digest email — see <see cref="WorkerSingleInstanceLock"/>
    /// for what exactly goes wrong. Turn it off only if you have a reason and know
    /// the consequences.
    /// </para>
    /// </summary>
    public bool EnforceSingleInstance { get; set; } = true;

    public int RetentionBatchSize { get; set; } = 500;
}
