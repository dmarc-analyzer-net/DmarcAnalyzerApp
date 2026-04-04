namespace DmarcAnalyzer.Api.Workers;

public sealed class WorkerOptions
{
    public int ScheduleIntervalSeconds { get; set; } = 300;
    public int MaxMessagesPerSync { get; set; } = 10;
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryBaseDelaySeconds { get; set; } = 2;
    public int StaleRunTimeoutMinutes { get; set; } = 30;
    public int SyncRunTimeoutMinutes { get; set; } = 10;
}
